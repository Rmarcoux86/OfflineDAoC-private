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

    /// <summary>An elite camp's mobs are looked for within this radius of its point.</summary>
    public const int EliteCampRadius = 1200;

    /// <summary>An elite camp is farther than this from every objective point (and from the other elite camp).</summary>
    public const int EliteMinDistance = 1500;

    /// <summary>Elite camps may be up to this many levels above the act's band.</summary>
    public const int EliteLevelSlack = 3;

    // Closer than this to the act camp, an elite camp is described as "right by the camp".
    private const int EliteNearCampDistance = 600;

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
            // Darkness Falls belongs to the last act only.
            if (zone.ZoneId == DarknessFallsZoneId && act != ActCount)
            {
                continue;
            }

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

    /// <summary>Objective levels around the entry level (owner 2026-10-25): camps from 1 below to 4 above, named 2 to 6 above.</summary>
    public const int EntryLevelBelow = 1;
    public const int EntryLevelCampAbove = 4;
    public const int EntryLevelNamedFrom = 2;
    public const int EntryLevelNamedAbove = 6;

    /// <summary>Darkness Falls: the last act's zone for every realm, and in no other act (expedition-zones.json; owner, 2026-10-24).</summary>
    public const int DarknessFallsZoneId = 249;

    /// <summary>
    /// The zone of each act of a run (index = act; -1 = none eligible), from the seed alone: the last act is Darkness
    /// Falls when the realm lists it there; then the other acts, the most constrained first, each draw from their
    /// eligible zones not used by another act of the run (owner, 2026-10-24: no zone twice in a run), repeating one only
    /// when none is left.
    /// </summary>
    public static int[] RunZonePlan(eRealm realm, int seed, IExpeditionCampSource campSource)
    {
        int[] zones = Enumerable.Repeat(-1, ActCount + 1).ToArray();
        HashSet<int> used = new();

        if (GetEligibleCandidates(ActCount, realm, campSource).Any(z => z.ZoneId == DarknessFallsZoneId))
        {
            zones[ActCount] = DarknessFallsZoneId;
            used.Add(DarknessFallsZoneId);
        }

        // The most constrained acts choose first (fewest eligible zones, then act order), so an act with one possible
        // zone keeps it rather than finding it taken by an act that had other choices.
        Dictionary<int, IReadOnlyList<ZoneCandidate>> eligible = Enumerable.Range(1, ActCount)
            .ToDictionary(act => act, act => GetEligibleCandidates(act, realm, campSource));

        foreach (int act in Enumerable.Range(1, ActCount).OrderBy(a => eligible[a].Count).ThenBy(a => a))
        {
            if (zones[act] >= 0)
            {
                continue;
            }

            IReadOnlyList<ZoneCandidate> candidates = eligible[act];

            if (candidates.Count == 0)
            {
                continue;
            }

            List<ZoneCandidate> fresh = candidates.Where(z => !used.Contains(z.ZoneId)).ToList();

            if (fresh.Count == 0)
            {
                fresh = candidates.ToList();
            }

            ZoneCandidate pick = fresh[new Random(unchecked(seed * 31 + act)).Next(fresh.Count)];
            zones[act] = pick.ZoneId;
            used.Add(pick.ZoneId);
        }

        return zones;
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
    /// <param name="entryLevel">The player's level as the act begins: objectives are picked around it (0: the whole band).</param>
    public static ActPlan Generate(int act, eRealm realm, int seed, int previousZoneId, int entryLevel = 0)
    {
        return Generate(act, realm, seed, previousZoneId, WorldZoneOffset, WorldThenCatalogCampSource.Instance, -1, entryLevel);
    }

    /// <summary>The default camp source (world, then catalog) with an injected zone offset lookup.</summary>
    public static ActPlan Generate(int act, eRealm realm, int seed, int previousZoneId, Func<int, (int ox, int oy)> zoneOffset)
    {
        return Generate(act, realm, seed, previousZoneId, zoneOffset, WorldThenCatalogCampSource.Instance);
    }

    public static ActPlan Generate(int act, eRealm realm, int seed, int previousZoneId, Func<int, (int ox, int oy)> zoneOffset, IExpeditionCampSource campSource,
        int forcedZoneId = -1, int entryLevel = 0)
    {
        ArgumentNullException.ThrowIfNull(zoneOffset);
        ArgumentNullException.ThrowIfNull(campSource);
        (int minLevel, int maxLevel) = GetLevelBand(act);
        IReadOnlyList<ZoneCandidate> candidates = GetEligibleCandidates(act, realm, campSource);
        Random rng = new(unchecked(seed * 31 + act));

        // Zone: the run's zone for this act (RunZonePlan: no zone twice in a run, Darkness Falls last where listed).
        // A previous zone passed in is still avoided when the plan would repeat it and another is left.
        int[] runZones = RunZonePlan(realm, seed, campSource);
        ZoneCandidate zone = candidates.FirstOrDefault(z => z.ZoneId == runZones[act]);

        if (zone == null || (previousZoneId >= 0 && zone.ZoneId == previousZoneId))
        {
            List<ZoneCandidate> choices = candidates.Where(z => previousZoneId < 0 || z.ZoneId != previousZoneId).ToList();

            if (choices.Count == 0)
            {
                choices = candidates.ToList();
            }

            zone = choices[rng.Next(choices.Count)];
        }
        else
        {
            // Keep the random sequence after the zone draw as it was.
            rng.Next();
        }

        // Re-planning an act already under way (UpgradeInProgress): its own zone, with the same random sequence after.
        if (forcedZoneId >= 0)
        {
            zone = candidates.FirstOrDefault(z => z.ZoneId == forcedZoneId);

            if (zone == null)
            {
                return null;
            }
        }
        List<AutonomousCapnBryGoalCatalog.Entry> zoneGoals = campSource.GetZoneCamps(zone.RegionId, zone.ZoneId).ToList();

        if (zoneGoals.Count == 0)
        {
            throw new InvalidOperationException($"The camp source has no camps for expedition zone {zone.ZoneId} ({zone.ZoneName}).");
        }

        if (zone.ZoneId == DarknessFallsZoneId)
        {
            zoneGoals = NearRealmEntrance(zoneGoals, realm);
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

        // Fork (owner 2026-10-25): objectives fit the player as the act begins, not grey to them and not the act's
        // last levels. With the entry level known, camps are within a level below to four above it and the named
        // monster two to six above it; a zone with nothing there keeps the band rule below.
        bool Overlaps(AutonomousCapnBryGoalCatalog.Entry e, int low, int high) => e.Levels.Min() <= high && e.Levels.Max() >= low;
        List<AutonomousCapnBryGoalCatalog.Entry> fitting = entryLevel > 0
            ? inBand.Where(e => Overlaps(e, entryLevel - EntryLevelBelow, entryLevel + EntryLevelCampAbove)).ToList()
            : new List<AutonomousCapnBryGoalCatalog.Entry>();

        // ClearCamp: a band camp (one fitting the entry level when known), preferring ones seen at least twice.
        List<AutonomousCapnBryGoalCatalog.Entry> campGoals = fitting.Count > 0 ? fitting : inBand;
        List<AutonomousCapnBryGoalCatalog.Entry> clearPool = campGoals.Where(e => e.Sightings >= 2).ToList();

        if (clearPool.Count == 0)
        {
            clearPool = campGoals;
        }

        AutonomousCapnBryGoalCatalog.Entry clearGoal = clearPool[rng.Next(clearPool.Count)];
        (int clearMin, int clearMax) = ClearCountRange(act);

        // KillNamed: a different mob from the top third of the band.
        int namedThreshold = minLevel + (maxLevel - minLevel) * 2 / 3;
        List<AutonomousCapnBryGoalCatalog.Entry> otherMobs = inBand.Where(e => e.Name != clearGoal.Name).ToList();

        if (otherMobs.Count == 0)
        {
            otherMobs = inBand;
        }

        List<AutonomousCapnBryGoalCatalog.Entry> namedPool = entryLevel > 0
            ? otherMobs.Where(e => Overlaps(e, entryLevel + EntryLevelNamedFrom, entryLevel + EntryLevelNamedAbove)).ToList()
            : new List<AutonomousCapnBryGoalCatalog.Entry>();

        if (namedPool.Count == 0)
        {
            namedPool = otherMobs.Where(e => e.Levels.Max() >= namedThreshold).ToList();
        }

        if (namedPool.Count == 0)
        {
            namedPool = otherMobs
                .OrderByDescending(e => e.Levels.Max())
                .Take(Math.Max(1, otherMobs.Count / 3))
                .ToList();
        }

        // ReachPoint: one of the quarter of the zone's camps farthest from the camp.
        List<AutonomousCapnBryGoalCatalog.Entry> farPool = zoneGoals
            .OrderByDescending(e => DistanceSquared(e.LocalX, e.LocalY, campLocalX, campLocalY))
            .Take(Math.Max(1, zoneGoals.Count / 4))
            .ToList();

        // Later acts have more objectives (owner, 2026-10-24): the camp first, then a named monster and a far
        // marker, then a second camp and a second named monster. Act 1 keeps two: the camp and one of the others.
        int objectiveCount = ObjectiveCount(act, rng);
        List<eObjectiveKind> kinds = [eObjectiveKind.ClearCamp];

        if (objectiveCount == 2)
        {
            kinds.Add(rng.Next(2) == 0 ? eObjectiveKind.KillNamed : eObjectiveKind.ReachPoint);
        }
        else
        {
            eObjectiveKind[] order = [eObjectiveKind.KillNamed, eObjectiveKind.ReachPoint, eObjectiveKind.ClearCamp, eObjectiveKind.KillNamed];
            kinds.AddRange(order.Take(objectiveCount - 1));
        }

        // Fork (owner 2026-10-25): compass words mean little in a dungeon of several floors, and the far marker could
        // lead to the grey corners of Darkness Falls: there it is another camp to clear.
        if (zone.ZoneId == DarknessFallsZoneId)
        {
            kinds = kinds.Select(kind => kind == eObjectiveKind.ReachPoint ? eObjectiveKind.ClearCamp : kind).ToList();
        }

        List<ActObjective> objectives = new();
        HashSet<string> usedNames = new(StringComparer.Ordinal);
        HashSet<AutonomousCapnBryGoalCatalog.Entry> usedGoals = new();

        foreach (eObjectiveKind kind in kinds)
        {
            AutonomousCapnBryGoalCatalog.Entry goal = kind switch
            {
                eObjectiveKind.ClearCamp when objectives.Count == 0 => clearGoal,
                eObjectiveKind.ClearCamp => PickUnused(rng, clearPool, inBand, usedNames, usedGoals),
                eObjectiveKind.KillNamed => PickUnused(rng, namedPool, otherMobs, usedNames, usedGoals),
                _ => PickUnused(rng, farPool, farPool, null, usedGoals)
            };

            usedGoals.Add(goal);

            if (kind != eObjectiveKind.ReachPoint)
            {
                usedNames.Add(goal.Name);
            }

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
                Count = kind == eObjectiveKind.ClearCamp ? rng.Next(clearMin, clearMax + 1) : 1,
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

        // Elite packs come last, so the seed's earlier choices are the same as before elites existed.
        List<EliteCamp> elites = ChooseElites(rng, zoneGoals, objectives, bossName, minLevel, maxLevel, zone.RegionId, ox, oy, campX, campY);
        List<ShrineSpec> shrines = ExpeditionShrines.Choose(rng, zoneGoals.Select(e => (ox + e.LocalX, oy + e.LocalY, e.Z)).ToList(),
            (campX, campY), objectives, elites, zone.RegionId, (x, y) => DescribeFromCamp(campX, campY, x, y));

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
                Title = BossTitle(act, seed, zone.ZoneName),
                HealthMultiplier = 3 + act
            },
            BossSpawned = false,
            BossKilled = false,
            Elites = elites,
            Shrines = shrines
        };
    }

    /// <summary>The named final bosses of acts 3 to 5 (owner, 2026-10-24). A run draws three of them, none twice.</summary>
    /// <summary>The bosses of acts 2-4, in random order per run, none twice (owner 2026-10-25).</summary>
    public static readonly IReadOnlyList<string> NamedBosses = ["Drunkstains", "Ios", "Toothmouth", "Gwyraan"];

    /// <summary>The boss of the last act of every run (owner 2026-10-25).</summary>
    public const string FinalBoss = "Preesh";

    /// <summary>What a named boss shouts when it appears; null for one with no line.</summary>
    public static string BossQuote(string title)
    {
        return title switch
        {
            FirstActBoss => "You want this ring? It's got\u2026",
            _ => null
        };
    }

    /// <summary>The first act whose boss is one of <see cref="NamedBosses"/>.</summary>
    public const int FirstNamedBossAct = 2;

    /// <summary>The act 1 boss of every run (owner, 2026-10-24).</summary>
    public const string FirstActBoss = "Three Point Oh";

    /// <summary>
    /// The act boss's name: <see cref="FirstActBoss"/> in act 1, <see cref="FinalBoss"/> in the last act, and in between
    /// a named boss picked by a shuffle of <see cref="NamedBosses"/> seeded by the run alone, so the acts of one run
    /// never repeat a name.
    /// </summary>
    public static string BossTitle(int act, int seed, string zoneName)
    {
        return BossTitle(act, seed, zoneName, null);
    }

    /// <summary>
    /// As above, skipping the bosses this run has already faced (<paramref name="faced"/>): a run started under the
    /// earlier boss rules may have met one of the named bosses already (owner 2026-10-25: no boss twice in a run).
    /// </summary>
    public static string BossTitle(int act, int seed, string zoneName, IReadOnlyCollection<string> faced)
    {
        if (act <= 1)
        {
            return FirstActBoss;
        }

        if (act >= ActCount)
        {
            return FinalBoss;
        }

        if (act < FirstNamedBossAct || NamedBosses.Count == 0)
        {
            return $"Warden of {zoneName}";
        }

        List<string> order = NamedBosses.ToList();
        var rng = new Random(unchecked(seed * 977 + 13));

        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        string pick = order[(act - FirstNamedBossAct) % order.Count];

        if (faced == null || !faced.Contains(pick))
        {
            return pick;
        }

        // The first in the run's order that this run has not met; all met: the seeded one.
        return order.FirstOrDefault(name => !faced.Contains(name)) ?? pick;
    }

    /// <summary>Objectives per act: 2, 3, 3-4, 4, 4-5.</summary>
    public static int ObjectiveCount(int act, Random rng)
    {
        return act switch
        {
            <= 1 => 2,
            2 => 3,
            3 => rng.Next(3, 5),
            4 => 4,
            _ => rng.Next(4, 6)
        };
    }

    /// <summary>Kills a camp objective asks for: 5-8 in act 1, one more at each end per act (9-12 in act 5).</summary>
    public static (int Min, int Max) ClearCountRange(int act)
    {
        int extra = Math.Clamp(act, 1, ActCount) - 1;

        return (5 + extra, 8 + extra);
    }

    /// <summary>
    /// A goal from <paramref name="pool"/> not used by an earlier objective (by mob name when
    /// <paramref name="usedNames"/> is given, else by camp); then from <paramref name="fallback"/>; then anything in the pool.
    /// </summary>
    private static AutonomousCapnBryGoalCatalog.Entry PickUnused(Random rng, List<AutonomousCapnBryGoalCatalog.Entry> pool,
        List<AutonomousCapnBryGoalCatalog.Entry> fallback, HashSet<string> usedNames, HashSet<AutonomousCapnBryGoalCatalog.Entry> usedGoals)
    {
        bool Unused(AutonomousCapnBryGoalCatalog.Entry e) => !usedGoals.Contains(e) && (usedNames == null || !usedNames.Contains(e.Name));

        List<AutonomousCapnBryGoalCatalog.Entry> candidates = pool.Where(Unused).ToList();

        if (candidates.Count == 0)
        {
            candidates = fallback.Where(Unused).ToList();
        }

        if (candidates.Count == 0)
        {
            candidates = pool;
        }

        return candidates[rng.Next(candidates.Count)];
    }

    /// <summary>
    /// One or two elite camps: zone camps of the band (or up to <see cref="EliteLevelSlack"/> levels above) whose mob
    /// is neither an objective's nor the boss's, farther than <see cref="EliteMinDistance"/> from every objective point
    /// and from each other, preferring camps seen at least twice (packs). Empty when no camp qualifies.
    /// </summary>
    private static List<EliteCamp> ChooseElites(Random rng, List<AutonomousCapnBryGoalCatalog.Entry> zoneGoals, List<ActObjective> objectives,
        string bossName, int minLevel, int maxLevel, int regionId, int ox, int oy, int campX, int campY)
    {
        HashSet<string> excludedNames = new(StringComparer.OrdinalIgnoreCase) { bossName ?? string.Empty };

        foreach (ActObjective objective in objectives)
        {
            if (!string.IsNullOrEmpty(objective.MobName))
            {
                excludedNames.Add(objective.MobName);
            }
        }

        long minDistanceSquared = (long)EliteMinDistance * EliteMinDistance;
        List<AutonomousCapnBryGoalCatalog.Entry> candidates = zoneGoals
            .Where(e => e.Levels is { Length: > 0 } && e.Levels.Min() <= maxLevel + EliteLevelSlack && e.Levels.Max() >= minLevel)
            .Where(e => !excludedNames.Contains(e.Name))
            .Where(e => objectives.All(o => DistanceSquared(ox + e.LocalX, oy + e.LocalY, o.X, o.Y) > minDistanceSquared))
            .ToList();
        List<EliteCamp> elites = new();
        int wanted = rng.Next(1, 3);

        while (elites.Count < wanted && candidates.Count > 0)
        {
            List<AutonomousCapnBryGoalCatalog.Entry> pool = candidates.Where(e => e.Sightings >= 2).ToList();

            if (pool.Count == 0)
            {
                pool = candidates;
            }

            AutonomousCapnBryGoalCatalog.Entry goal = pool[rng.Next(pool.Count)];
            string affix = ExpeditionElites.Affixes[rng.Next(ExpeditionElites.Affixes.Count)];
            int x = ox + goal.LocalX;
            int y = oy + goal.LocalY;

            elites.Add(new EliteCamp
            {
                MobName = goal.Name,
                Affix = affix,
                RegionId = regionId,
                X = x,
                Y = y,
                Z = goal.Z,
                Radius = EliteCampRadius,
                Level = goal.Levels.Max() + 2,
                Description = $"A {affix} pack of {goal.Name} lairs {DescribeFromCamp(campX, campY, x, y)}"
            });

            // The next pack is another mob, away from this one.
            candidates = candidates
                .Where(e => !string.Equals(e.Name, goal.Name, StringComparison.OrdinalIgnoreCase))
                .Where(e => DistanceSquared(ox + e.LocalX, oy + e.LocalY, x, y) > minDistanceSquared)
                .ToList();
        }

        return elites;
    }

    /// <summary>"to the north-east of the camp" (DOL headings, as <see cref="DescribeArea"/>), or "right by the camp".</summary>
    /// <summary>
    /// Brings an act planned under older rules up to date (owner, 2026-10-24: updates should reach runs already going).
    /// Adds shrines when it has none, and, while its boss has not come, objectives up to what the act gets now (kill
    /// objectives on monsters it does not use yet, reach markers at new spots). Nothing already done is changed.
    /// Returns a short description of what was added, or null when nothing was.
    /// </summary>
    public static string UpgradeInProgress(ActPlan plan, eRealm realm, int seed)
    {
        if (plan == null || plan.Objectives == null || plan.BossKilled)
        {
            return null;
        }

        bool needShrines = plan.Shrines == null || plan.Shrines.Count == 0;
        ActPlan fresh;

        try
        {
            fresh = Generate(plan.Act, realm, seed, -1, WorldZoneOffset, WorldThenCatalogCampSource.Instance, plan.ZoneId);
        }
        catch (Exception)
        {
            return null;
        }

        if (fresh == null || fresh.ZoneId != plan.ZoneId)
        {
            return null;
        }

        int objectivesAdded = 0;

        if (!plan.BossSpawned)
        {
            foreach (ActObjective candidate in fresh.Objectives)
            {
                if (plan.Objectives.Count >= fresh.Objectives.Count)
                {
                    break;
                }

                bool clash = plan.Objectives.Any(o =>
                    (candidate.Kind != eObjectiveKind.ReachPoint && string.Equals(o.MobName, candidate.MobName, StringComparison.OrdinalIgnoreCase))
                    || (o.X == candidate.X && o.Y == candidate.Y));

                if (clash)
                {
                    continue;
                }

                candidate.Id = $"a{plan.Act}-{plan.Objectives.Count + 1}";
                candidate.Progress = 0;
                plan.Objectives.Add(candidate);
                objectivesAdded++;
            }
        }

        int shrinesAdded = 0;

        if (needShrines)
        {
            List<(int X, int Y, int Z)> camps;

            try
            {
                (int ox, int oy) = WorldZoneOffset(plan.ZoneId);
                camps = WorldThenCatalogCampSource.Instance.GetZoneCamps(plan.RegionId, plan.ZoneId).Select(e => (ox + e.LocalX, oy + e.LocalY, e.Z)).ToList();
            }
            catch (Exception)
            {
                camps = new List<(int X, int Y, int Z)>();
            }

            if (camps.Count > 0)
            {
                // Placed around this act's own objectives and elite packs, not the fresh plan's.
                plan.Shrines = ExpeditionShrines.Choose(new Random(unchecked(seed * 53 + plan.Act)), camps, (plan.CampX, plan.CampY), plan.Objectives,
                    plan.Elites, plan.RegionId, (x, y) => DescribeFromCamp(plan.CampX, plan.CampY, x, y));
                shrinesAdded = plan.Shrines.Count;
            }
        }

        if (objectivesAdded == 0 && shrinesAdded == 0)
        {
            return null;
        }

        List<string> parts = new();

        if (objectivesAdded > 0)
        {
            parts.Add($"{objectivesAdded} new objective{(objectivesAdded == 1 ? string.Empty : "s")}");
        }

        if (shrinesAdded > 0)
        {
            parts.Add($"{shrinesAdded} shrine{(shrinesAdded == 1 ? string.Empty : "s")} to find");
        }

        return string.Join(" and ", parts);
    }

    /// <summary>The 8-point compass direction ("north-east") from one global point to another.</summary>
    public static string CompassDirection(int fromX, int fromY, int x, int y)
    {
        int heading = new Point2D(fromX, fromY).GetHeading(x, y) % 4096;

        return CompassAreas[(heading + 256) / 512 % 8];
    }

    public static string DescribeFromCamp(int campX, int campY, int x, int y)
    {
        if (DistanceSquared(campX, campY, x, y) <= (long)EliteNearCampDistance * EliteNearCampDistance)
        {
            return "right by the camp";
        }

        int heading = new Point2D(campX, campY).GetHeading(x, y) % 4096;

        return $"to the {CompassAreas[(heading + 256) / 512 % 8]} of the camp";
    }

    /// <summary>
    /// "/loc 33479, 45931": a global point as the zone coordinates the in-game /loc command shows, or null when the
    /// world is not loaded (unit tests) or the point is outside every zone.
    /// </summary>
    public static string DescribeLoc(int regionId, int x, int y)
    {
        try
        {
            Zone zone = DOL.GS.WorldMgr.GetRegion((ushort) regionId)?.GetZone(x, y);

            if (zone == null)
            {
                return null;
            }

            return $"/loc {x - zone.XOffset}, {y - zone.YOffset}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The act's journal lines: zone and band, each objective, the boss.</summary>
    public static IReadOnlyList<string> DescribePlan(ActPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<string> lines = [$"Act {plan.Act} of {ActCount}: {plan.ZoneName} (levels {plan.MinLevel}-{plan.MaxLevel})"];
        string campLoc = DescribeLoc(plan.RegionId, plan.CampX, plan.CampY);

        if (campLoc != null)
        {
            lines.Add($"Camp: {campLoc}");
        }

        foreach (ActObjective objective in plan.Objectives ?? [])
        {
            string marker = objective.Done ? "[done]" : "[ ]";
            string loc = objective.Done ? null : DescribeLoc(objective.RegionId, objective.X, objective.Y);
            string where = loc == null ? string.Empty : $" - {loc}";
            lines.Add($"{marker} {objective.Description} ({Math.Min(objective.Progress, objective.Count)}/{objective.Count}){where}");
        }

        if (plan.Boss != null)
        {
            string status = plan.BossKilled ? "slain" : plan.BossSpawned ? "has appeared" : "appears when every objective is done";
            string bossLoc = plan.BossKilled ? null : DescribeLoc(plan.Boss.RegionId, plan.Boss.X, plan.Boss.Y);
            string bossWhere = bossLoc == null ? string.Empty : $" ({bossLoc})";
            lines.Add($"Boss: {plan.Boss.Title}, a level {plan.Boss.Level} {plan.Boss.BaseMobName} - {status}{bossWhere}");
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

    /// <summary>
    /// Fork (owner 2026-10-27): Darkness Falls is shared by the three realms, each with its own entrance, and the act
    /// camp stands at the run's realm's entrance (ExpeditionArrival). A Midgard run was sent across the whole dungeon
    /// for experienced necyomancers, which only spawn on the Albion side. Objectives, the boss and elite packs come
    /// from the camps within <see cref="DarknessFallsNearRadius"/> of the realm's own entrance, or the nearest
    /// <see cref="DarknessFallsNearMinimum"/> camps when fewer lie that close (the Hibernia side is sparse).
    /// </summary>
    public const int DarknessFallsNearRadius = 14000;
    public const int DarknessFallsNearMinimum = 12;

    /// <summary>Where each realm's Darkness Falls entrance puts the player (ZonePoint targets, zone-local).</summary>
    public static (int X, int Y) DarknessFallsEntrance(eRealm realm)
    {
        return realm switch
        {
            eRealm.Midgard => (18798 - 8192, 18667 - 8192),
            eRealm.Hibernia => (46325 - 8192, 40969 - 8192),
            _ => (31211 - 8192, 27924 - 8192)
        };
    }

    /// <summary>The camps near the realm's entrance (see <see cref="DarknessFallsNearRadius"/>), in their given order.</summary>
    public static List<AutonomousCapnBryGoalCatalog.Entry> NearRealmEntrance(List<AutonomousCapnBryGoalCatalog.Entry> goals, eRealm realm)
    {
        (int homeX, int homeY) = DarknessFallsEntrance(realm);
        long radiusSquared = (long) DarknessFallsNearRadius * DarknessFallsNearRadius;
        List<AutonomousCapnBryGoalCatalog.Entry> near = goals.Where(e => DistanceSquared(e.LocalX, e.LocalY, homeX, homeY) <= radiusSquared).ToList();

        if (near.Count >= DarknessFallsNearMinimum || near.Count == goals.Count)
        {
            return near;
        }

        HashSet<AutonomousCapnBryGoalCatalog.Entry> nearest = goals
            .OrderBy(e => DistanceSquared(e.LocalX, e.LocalY, homeX, homeY))
            .Take(DarknessFallsNearMinimum)
            .ToHashSet();

        return goals.Where(nearest.Contains).ToList();
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
