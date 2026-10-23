using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DOL.GS.Expedition;

/// <summary>
/// Builds expedition acts from the embedded expedition-zones.json (which zones
/// each realm may use per act) and a camp source (where the monsters are): by
/// default the live world's Mob table, with the CapnBry goal catalog for zones
/// the world gives nothing (<see cref="WorldThenCatalogCampSource"/>).
///
/// A listed zone is used only when it has at least <see cref="MinimumBandCamps"/>
/// camps in the act's level band; when none has, the realm's zone (from any act)
/// with the most camps in the band is used instead.
///
/// Generation is deterministic for a given seed and camp data: one System.Random
/// seeded with unchecked(seed * 31 + act) drives every choice, in a fixed order,
/// over lists kept in the source's order (catalog order, or the world source's
/// sorted order). Camp coordinates are zone-local; they are made global with the
/// zone offset lookup (WorldMgr by default, injectable for tests).
/// </summary>
public static class ActGenerator
{
    public const int ActCount = 5;

    /// <summary>A listed zone needs this many camps in the act's band to be chosen.</summary>
    public const int MinimumBandCamps = 3;

    private const string ResourceSuffix = "expedition-zones.json";

    // A computed boss is a mob at most this many levels above the band's top (keeps epic encounters out).
    private const int BossLevelCeiling = 10;

    private const int ClearCampRadius = 1500;
    private const int KillNamedRadius = 1500;
    private const int ReachPointRadius = 400;
    private static readonly (int Min, int Max)[] LevelBands = [(1, 10), (10, 20), (20, 32), (32, 44), (44, 50)];
    private static readonly string[] CompassAreas = ["south", "south-west", "west", "north-west", "north", "north-east", "east", "south-east"];
    private static readonly Lazy<ZoneTable> LoadedTable = new(LoadTable, true);

    public sealed class CampPoint
    {
        [JsonPropertyName("localX")]
        public int LocalX { get; set; }

        [JsonPropertyName("localY")]
        public int LocalY { get; set; }

        [JsonPropertyName("z")]
        public int Z { get; set; }

        [JsonPropertyName("heading")]
        public ushort Heading { get; set; }
    }

    public sealed class ZoneCandidate
    {
        [JsonPropertyName("zoneId")]
        public int ZoneId { get; set; }

        [JsonPropertyName("zoneName")]
        public string ZoneName { get; set; } = string.Empty;

        [JsonPropertyName("regionId")]
        public int RegionId { get; set; }

        [JsonPropertyName("minLevel")]
        public int MinLevel { get; set; }

        [JsonPropertyName("maxLevel")]
        public int MaxLevel { get; set; }

        // Null means "compute": the median position of the zone's lowest-level goals.
        [JsonPropertyName("camp")]
        public CampPoint Camp { get; set; }

        // Catalog mob names in this zone; empty means "compute".
        [JsonPropertyName("bossCandidates")]
        public string[] BossCandidates { get; set; } = [];
    }

    private sealed class ActEntry
    {
        [JsonPropertyName("act")]
        public int Act { get; set; }

        [JsonPropertyName("zones")]
        public ZoneCandidate[] Zones { get; set; } = [];
    }

    private sealed class RealmEntry
    {
        [JsonPropertyName("realm")]
        public string Realm { get; set; } = string.Empty;

        [JsonPropertyName("acts")]
        public ActEntry[] Acts { get; set; } = [];
    }

    private sealed class ZoneTable
    {
        [JsonPropertyName("schema")]
        public int Schema { get; set; }

        [JsonPropertyName("realms")]
        public RealmEntry[] Realms { get; set; } = [];
    }

    public static (int Min, int Max) GetLevelBand(int act)
    {
        if (act < 1 || act > ActCount)
        {
            throw new ArgumentOutOfRangeException(nameof(act), act, $"Act must be 1..{ActCount}.");
        }

        return LevelBands[act - 1];
    }

    /// <summary>
    /// True when expedition-zones.json lists zones for this realm and act; false when
    /// GetCandidates had to fall back (unknown realm, or an act the realm has no data for).
    /// </summary>
    public static bool HasOwnCandidates(int act, eRealm realm)
    {
        RealmEntry entry = LoadedTable.Value.Realms.FirstOrDefault(r => IsRealm(r, realm));

        return entry?.Acts.FirstOrDefault(a => a.Act == act)?.Zones is { Length: > 0 };
    }

    /// <summary>
    /// The candidate zones for an act. Never empty while the embedded table has data:
    /// a realm missing from the table uses the first realm listed (Albion), and an act
    /// a realm has no zones for uses that realm's highest-level zone.
    /// </summary>
    public static IReadOnlyList<ZoneCandidate> GetCandidates(int act, eRealm realm)
    {
        GetLevelBand(act);
        RealmEntry[] realms = LoadedTable.Value.Realms;

        if (realms.Length == 0)
        {
            throw new InvalidOperationException($"Embedded '{ResourceSuffix}' is missing or empty.");
        }

        RealmEntry entry = realms.FirstOrDefault(r => IsRealm(r, realm)) ?? realms[0];
        ZoneCandidate[] zones = entry.Acts.FirstOrDefault(a => a.Act == act)?.Zones ?? [];

        if (zones.Length > 0)
        {
            return zones;
        }

        // No zones for this act: the realm's highest-level zone (later acts win ties).
        ZoneCandidate highest = entry.Acts
            .OrderBy(a => a.Act)
            .SelectMany(a => a.Zones)
            .Aggregate((ZoneCandidate)null, (best, zone) => best == null || zone.MaxLevel >= best.MaxLevel ? zone : best);

        if (highest == null)
        {
            throw new InvalidOperationException($"Realm '{entry.Realm}' has no expedition zones at all.");
        }

        return [highest];
    }

    /// <summary>
    /// The zones an act can actually use with this camp source: the listed candidates with at
    /// least <see cref="MinimumBandCamps"/> camps in the band; if none qualifies, the realm's
    /// zone (from any act) with the most camps in the band; if no zone has any, the listed
    /// candidates the source has camps for (the act then uses the camps closest to the band).
    /// </summary>
    public static IReadOnlyList<ZoneCandidate> GetEligibleCandidates(int act, eRealm realm, IExpeditionCampSource campSource)
    {
        ArgumentNullException.ThrowIfNull(campSource);
        (int minLevel, int maxLevel) = GetLevelBand(act);
        IReadOnlyList<ZoneCandidate> listed = GetCandidates(act, realm);
        List<ZoneCandidate> eligible = listed
            .Where(z => CountBandCamps(campSource, z, minLevel, maxLevel) >= MinimumBandCamps)
            .ToList();

        if (eligible.Count > 0)
        {
            return eligible;
        }

        // None qualifies: the realm's zone with the most camps in the band (earlier acts and list order win ties).
        RealmEntry[] realms = LoadedTable.Value.Realms;
        RealmEntry entry = realms.FirstOrDefault(r => IsRealm(r, realm)) ?? realms[0];
        ZoneCandidate best = null;
        int bestCount = 0;

        foreach (ZoneCandidate zone in entry.Acts.OrderBy(a => a.Act).SelectMany(a => a.Zones))
        {
            int count = CountBandCamps(campSource, zone, minLevel, maxLevel);

            if (count > bestCount)
            {
                best = zone;
                bestCount = count;
            }
        }

        if (best != null)
        {
            return [best];
        }

        // Nothing in band anywhere: the old behaviour, over the listed zones that have camps at all.
        List<ZoneCandidate> populated = listed.Where(z => campSource.GetZoneCamps(z.RegionId, z.ZoneId).Count > 0).ToList();

        return populated.Count > 0 ? populated : listed;
    }

    /// <summary>The live world's zone offset. Requires a loaded world.</summary>
    public static (int ox, int oy) WorldZoneOffset(int zoneId)
    {
        Zone zone = WorldMgr.GetZone((ushort)zoneId);

        if (zone == null)
        {
            throw new InvalidOperationException($"Zone {zoneId} is not loaded; expedition coordinates can't be made global.");
        }

        return (zone.XOffset, zone.YOffset);
    }

    /// <param name="previousZoneId">The previous act's zone, which is avoided; negative means none (zone 0 is Camelot Hills).</param>
    public static ActPlan Generate(int act, eRealm realm, int seed, int previousZoneId)
    {
        return Generate(act, realm, seed, previousZoneId, WorldZoneOffset, WorldThenCatalogCampSource.Instance);
    }

    /// <summary>The default camp source (world, then catalog) with an injected zone offset lookup.</summary>
    public static ActPlan Generate(int act, eRealm realm, int seed, int previousZoneId, Func<int, (int ox, int oy)> zoneOffset)
    {
        return Generate(act, realm, seed, previousZoneId, zoneOffset, WorldThenCatalogCampSource.Instance);
    }

    public static ActPlan Generate(int act, eRealm realm, int seed, int previousZoneId, Func<int, (int ox, int oy)> zoneOffset, IExpeditionCampSource campSource)
    {
        ArgumentNullException.ThrowIfNull(zoneOffset);
        ArgumentNullException.ThrowIfNull(campSource);
        (int minLevel, int maxLevel) = GetLevelBand(act);
        IReadOnlyList<ZoneCandidate> candidates = GetEligibleCandidates(act, realm, campSource);
        Random rng = new(unchecked(seed * 31 + act));

        // Zone: anything but the previous act's zone (negative: none), unless that is the only choice.
        List<ZoneCandidate> choices = candidates.Where(z => previousZoneId < 0 || z.ZoneId != previousZoneId).ToList();

        if (choices.Count == 0)
        {
            choices = candidates.ToList();
        }

        ZoneCandidate zone = choices[rng.Next(choices.Count)];
        List<AutonomousCapnBryGoalCatalog.Entry> zoneGoals = campSource.GetZoneCamps(zone.RegionId, zone.ZoneId).ToList();

        if (zoneGoals.Count == 0)
        {
            throw new InvalidOperationException($"The camp source has no camps for expedition zone {zone.ZoneId} ({zone.ZoneName}).");
        }

        (int ox, int oy) = zoneOffset(zone.ZoneId);

        // Area words ("north-east", "heart") are relative to the centre of the zone's goals.
        int areaMinX = zoneGoals.Min(e => e.LocalX);
        int areaMaxX = zoneGoals.Max(e => e.LocalX);
        int areaMinY = zoneGoals.Min(e => e.LocalY);
        int areaMaxY = zoneGoals.Max(e => e.LocalY);
        Point2D areaCentre = new((areaMinX + areaMaxX) / 2, (areaMinY + areaMaxY) / 2);
        long heartRadius = Math.Max(1, Math.Max(areaMaxX - areaMinX, areaMaxY - areaMinY)) * 15L / 100;

        // Goals in the act's level band; thin or fallback zones use the goals closest to it.
        List<AutonomousCapnBryGoalCatalog.Entry> inBand = zoneGoals
            .Where(e => e.Levels.Min() <= maxLevel && e.Levels.Max() >= minLevel)
            .ToList();

        if (inBand.Count < 3)
        {
            inBand = zoneGoals
                .OrderBy(e => e.Levels.Max() < minLevel ? minLevel - e.Levels.Max() : Math.Max(0, e.Levels.Min() - maxLevel))
                .Take(Math.Max(3, zoneGoals.Count / 3))
                .ToList();
        }

        // Camp: the lowest-level goal nearest the median of the zone's lowest-level goals.
        int campLocalX;
        int campLocalY;
        int campZ;
        ushort? campHeading = null;

        if (zone.Camp != null)
        {
            campLocalX = zone.Camp.LocalX;
            campLocalY = zone.Camp.LocalY;
            campZ = zone.Camp.Z;
            campHeading = zone.Camp.Heading;
        }
        else
        {
            int lowest = zoneGoals.Min(e => e.Levels.Min());
            List<AutonomousCapnBryGoalCatalog.Entry> lowGoals = zoneGoals.Where(e => e.Levels.Min() <= lowest + 1).ToList();

            if (lowGoals.Count < 3)
            {
                lowGoals = zoneGoals.OrderBy(e => e.Levels.Min()).Take(3).ToList();
            }

            int medianX = Median(lowGoals.Select(e => e.LocalX));
            int medianY = Median(lowGoals.Select(e => e.LocalY));
            AutonomousCapnBryGoalCatalog.Entry campGoal = lowGoals
                .OrderBy(e => DistanceSquared(e.LocalX, e.LocalY, medianX, medianY))
                .First();
            campLocalX = campGoal.LocalX;
            campLocalY = campGoal.LocalY;
            campZ = campGoal.Z;
        }

        // ClearCamp: a band camp, preferring ones seen at least twice.
        List<AutonomousCapnBryGoalCatalog.Entry> clearPool = inBand.Where(e => e.Sightings >= 2).ToList();

        if (clearPool.Count == 0)
        {
            clearPool = inBand;
        }

        AutonomousCapnBryGoalCatalog.Entry clearGoal = clearPool[rng.Next(clearPool.Count)];
        int clearCount = rng.Next(5, 9);

        // KillNamed: a different mob from the top third of the band.
        int namedThreshold = minLevel + (maxLevel - minLevel) * 2 / 3;
        List<AutonomousCapnBryGoalCatalog.Entry> otherMobs = inBand.Where(e => e.Name != clearGoal.Name).ToList();

        if (otherMobs.Count == 0)
        {
            otherMobs = inBand;
        }

        List<AutonomousCapnBryGoalCatalog.Entry> namedPool = otherMobs.Where(e => e.Levels.Max() >= namedThreshold).ToList();

        if (namedPool.Count == 0)
        {
            namedPool = otherMobs
                .OrderByDescending(e => e.Levels.Max())
                .Take(Math.Max(1, otherMobs.Count / 3))
                .ToList();
        }

        AutonomousCapnBryGoalCatalog.Entry namedGoal = namedPool[rng.Next(namedPool.Count)];

        // ReachPoint: one of the quarter of the zone's camps farthest from the camp.
        List<AutonomousCapnBryGoalCatalog.Entry> farPool = zoneGoals
            .OrderByDescending(e => DistanceSquared(e.LocalX, e.LocalY, campLocalX, campLocalY))
            .Take(Math.Max(1, zoneGoals.Count / 4))
            .ToList();
        AutonomousCapnBryGoalCatalog.Entry farGoal = farPool[rng.Next(farPool.Count)];

        // Two objectives (ClearCamp plus one other) or all three.
        List<eObjectiveKind> kinds = [eObjectiveKind.ClearCamp];

        if (rng.Next(2, 4) == 3)
        {
            kinds.Add(eObjectiveKind.KillNamed);
            kinds.Add(eObjectiveKind.ReachPoint);
        }
        else
        {
            kinds.Add(rng.Next(2) == 0 ? eObjectiveKind.KillNamed : eObjectiveKind.ReachPoint);
        }

        List<ActObjective> objectives = new();

        foreach (eObjectiveKind kind in kinds)
        {
            AutonomousCapnBryGoalCatalog.Entry goal = kind switch
            {
                eObjectiveKind.ClearCamp => clearGoal,
                eObjectiveKind.KillNamed => namedGoal,
                _ => farGoal
            };
            string area = DescribeArea(areaCentre, heartRadius, goal.LocalX, goal.LocalY);
            string description = kind switch
            {
                eObjectiveKind.ClearCamp => $"Clear the {goal.Name} camp in the {area} of {zone.ZoneName}",
                eObjectiveKind.KillNamed => $"Slay a {goal.Name} in the {area} of {zone.ZoneName}",
                _ => $"Reach the far marker in the {area} of {zone.ZoneName}"
            };

            objectives.Add(new ActObjective
            {
                Id = $"a{act}-{objectives.Count + 1}",
                Kind = kind,
                MobName = kind == eObjectiveKind.ReachPoint ? string.Empty : goal.Name,
                Count = kind == eObjectiveKind.ClearCamp ? clearCount : 1,
                Progress = 0,
                RegionId = zone.RegionId,
                X = ox + goal.LocalX,
                Y = oy + goal.LocalY,
                Z = goal.Z,
                Radius = kind == eObjectiveKind.ClearCamp ? ClearCampRadius : kind == eObjectiveKind.KillNamed ? KillNamedRadius : ReachPointRadius,
                Description = description
            });
        }

        // Boss: a json candidate present in the zone (picked by the seed). Otherwise a named mob
        // (one with at most 2 spawns in the zone) of the band or above, highest level first;
        // else the most common of the band's top mobs.
        HashSet<string> zoneNames = new(zoneGoals.Select(e => e.Name), StringComparer.Ordinal);
        List<string> bossNames = (zone.BossCandidates ?? []).Where(zoneNames.Contains).Distinct().ToList();
        string bossName;

        if (bossNames.Count > 0)
        {
            bossName = bossNames[rng.Next(bossNames.Count)];
        }
        else
        {
            Dictionary<string, int> spawnsByName = zoneGoals
                .GroupBy(e => e.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Sum(e => Math.Max(1, e.Sightings)), StringComparer.Ordinal);
            bossName = zoneGoals
                .Where(e => e.Levels.Max() >= minLevel && e.Levels.Max() <= maxLevel + BossLevelCeiling && spawnsByName[e.Name] <= 2)
                .OrderByDescending(e => e.Levels.Max())
                .ThenBy(e => e.Name, StringComparer.Ordinal)
                .Select(e => e.Name)
                .FirstOrDefault();

            if (bossName == null)
            {
                int topLevel = inBand.Max(e => e.Levels.Max());
                bossName = inBand
                    .Where(e => e.Levels.Max() >= topLevel - 3)
                    .GroupBy(e => e.Name, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Sum(e => Math.Max(1, e.Sightings)))
                    .ThenByDescending(g => g.Max(e => e.Levels.Max()))
                    .ThenBy(g => g.Key, StringComparer.Ordinal)
                    .First()
                    .Key;
            }
        }

        AutonomousCapnBryGoalCatalog.Entry bossGoal = zoneGoals
            .Where(e => e.Name == bossName)
            .OrderByDescending(e => e.Levels.Max())
            .ThenByDescending(e => e.Sightings)
            .First();

        int campX = ox + campLocalX;
        int campY = oy + campLocalY;
        ActObjective first = objectives[0];

        return new ActPlan
        {
            Act = act,
            MinLevel = minLevel,
            MaxLevel = maxLevel,
            ZoneName = zone.ZoneName,
            ZoneId = zone.ZoneId,
            RegionId = zone.RegionId,
            CampX = campX,
            CampY = campY,
            CampZ = campZ,
            CampHeading = campHeading ?? (ushort)(new Point2D(campX, campY).GetHeading(first.X, first.Y) % 4096),
            Objectives = objectives,
            Boss = new BossSpec
            {
                BaseMobName = bossName,
                Level = maxLevel + 2,
                RegionId = zone.RegionId,
                X = ox + bossGoal.LocalX,
                Y = oy + bossGoal.LocalY,
                Z = bossGoal.Z,
                Title = $"Warden of {zone.ZoneName}",
                HealthMultiplier = 3 + act
            },
            BossSpawned = false,
            BossKilled = false
        };
    }

    /// <summary>The act's journal lines: zone and band, each objective, the boss.</summary>
    public static IReadOnlyList<string> DescribePlan(ActPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<string> lines = [$"Act {plan.Act} of {ActCount}: {plan.ZoneName} (levels {plan.MinLevel}-{plan.MaxLevel})"];

        foreach (ActObjective objective in plan.Objectives ?? [])
        {
            string marker = objective.Done ? "[done]" : "[ ]";
            lines.Add($"{marker} {objective.Description} ({Math.Min(objective.Progress, objective.Count)}/{objective.Count})");
        }

        if (plan.Boss != null)
        {
            string status = plan.BossKilled ? "slain" : plan.BossSpawned ? "has appeared" : "appears when every objective is done";
            lines.Add($"Boss: {plan.Boss.Title}, a level {plan.Boss.Level} {plan.Boss.BaseMobName} - {status}");
        }

        return lines;
    }

    // eRealm has aliased values (Albion = _FirstPlayerRealm, Hibernia = _Last), so
    // realm.ToString() is unreliable; compare parsed values instead.
    private static bool IsRealm(RealmEntry entry, eRealm realm)
    {
        return Enum.TryParse(entry.Realm, true, out eRealm parsed) && parsed == realm;
    }

    /// <summary>The zone's camps whose levels overlap the band.</summary>
    public static int CountBandCamps(IExpeditionCampSource campSource, ZoneCandidate zone, int minLevel, int maxLevel)
    {
        return campSource.GetZoneCamps(zone.RegionId, zone.ZoneId)
            .Count(e => e.Levels is { Length: > 0 } && e.Levels.Min() <= maxLevel && e.Levels.Max() >= minLevel);
    }

    private static int Median(IEnumerable<int> values)
    {
        List<int> sorted = values.OrderBy(v => v).ToList();

        return sorted[sorted.Count / 2];
    }

    private static long DistanceSquared(int x1, int y1, int x2, int y2)
    {
        long dx = x1 - x2;
        long dy = y1 - y2;

        return dx * dx + dy * dy;
    }

    // DOL headings: 0 faces +Y (south), 1024 west, 2048 north, 3072 east (Point2D.GetHeading, where.cs).
    private static string DescribeArea(Point2D centre, long heartRadius, int localX, int localY)
    {
        if (DistanceSquared(centre.X, centre.Y, localX, localY) <= heartRadius * heartRadius)
        {
            return "heart";
        }

        int heading = centre.GetHeading(localX, localY) % 4096;

        return CompassAreas[(heading + 256) / 512 % 8];
    }

    private static ZoneTable LoadTable()
    {
        try
        {
            Assembly assembly = typeof(ActGenerator).Assembly;
            string resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase));

            if (resource == null)
            {
                return new ZoneTable();
            }

            using Stream stream = assembly.GetManifestResourceStream(resource);
            ZoneTable table = JsonSerializer.Deserialize<ZoneTable>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return table ?? new ZoneTable();
        }
        catch (Exception)
        {
            // Generate() reports the empty table with a clear message.
            return new ZoneTable();
        }
    }
}
