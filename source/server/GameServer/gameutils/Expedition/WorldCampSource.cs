using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Logging;
using CampEntry = DOL.GS.AutonomousCapnBryGoalCatalog.Entry;

namespace DOL.GS.Expedition;

/// <summary>
/// Expedition camps from the live world's Mob table, so every zone the world populates can
/// host an act, not only the zones in the CapnBry snapshot.
///
/// A region is read once, on first use, and kept, from the Mob rows WorldMgr loaded at server
/// start (no database query): its hostile monster spawns (realm 0, not peaceful or
/// untargetable, no merchant/trainer/guard-style or epic class or guild, no classic quest NPC
/// or Darkness Falls raid/event spawn) are placed in their zone and grouped into camps by
/// name and a 4200-unit cell, the catalog's camp size.
/// A camp sits at the spawn nearest its median point, lists the levels seen and counts its
/// spawns as sightings.
/// Camps come out sorted (region, zone, name, cell), so a given world database always gives
/// the same list and the same plans for a seed. Nothing is cached while the world isn't
/// loaded, so a later call can still read it.
/// </summary>
public sealed class WorldCampSource : IExpeditionCampSource
{
    public const int CampCellSize = 4200;

    private const uint ExcludedFlags = (uint)(GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET);
    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    // Any of these in a spawn's ClassType or Guild marks a service NPC (or an epic encounter), not a monster.
    private static readonly string[] ServiceWords =
    [
        "Merchant", "Trainer", "Guard", "Teleporter", "Keep", "Herald", "Master", "Banker", "Healer",
        "Vault", "Stable", "Siege", "Boat", "Quest", "Commander", "Lord", "Epic"
    ];

    public static readonly WorldCampSource Instance = new();

    private readonly object _cacheLock = new();
    private readonly Dictionary<int, Dictionary<int, CampEntry[]>> _campsByRegion = new();

    /// <summary>One Mob row, already placed in its zone (zone-local X/Y).</summary>
    public sealed record SpawnRecord(
        string Name,
        int RegionId,
        int ZoneId,
        string ZoneName,
        int LocalX,
        int LocalY,
        int Z,
        int Level,
        byte Realm,
        uint Flags,
        string ClassType,
        string Guild);

    private WorldCampSource()
    {
    }

    public IReadOnlyList<CampEntry> GetZoneCamps(int regionId, int zoneId)
    {
        Dictionary<int, CampEntry[]> zones;

        lock (_cacheLock)
        {
            if (!_campsByRegion.TryGetValue(regionId, out zones))
            {
                zones = LoadRegion(regionId);

                if (zones == null)
                {
                    return [];
                }

                _campsByRegion[regionId] = zones;
            }
        }

        return zones.TryGetValue(zoneId, out CampEntry[] camps) ? camps : [];
    }

    /// <summary>True for a spawn that is a hostile monster an act can send the player after.</summary>
    public static bool IsHostileMonster(SpawnRecord spawn)
    {
        if (spawn == null || string.IsNullOrWhiteSpace(spawn.Name) || spawn.Level < 1 || spawn.Realm != 0)
        {
            return false;
        }

        if ((spawn.Flags & ExcludedFlags) != 0)
        {
            return false;
        }

        string serviceText = $"{spawn.ClassType} {spawn.Guild}";

        return !ServiceWords.Any(word => serviceText.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Filters spawns with <see cref="IsHostileMonster"/> and groups them into camps: same
    /// region, zone and exact name, same <see cref="CampCellSize"/> cell. Pure and deterministic:
    /// the output order depends only on the spawns, never on their input order.
    /// </summary>
    public static List<CampEntry> BuildCamps(IEnumerable<SpawnRecord> spawns)
    {
        ArgumentNullException.ThrowIfNull(spawns);

        return spawns
            .Where(IsHostileMonster)
            .GroupBy(s => (s.RegionId, s.ZoneId, s.Name, CellX: FloorDiv(s.LocalX), CellY: FloorDiv(s.LocalY)))
            .OrderBy(g => g.Key.RegionId)
            .ThenBy(g => g.Key.ZoneId)
            .ThenBy(g => g.Key.Name, StringComparer.Ordinal)
            .ThenBy(g => g.Key.CellY)
            .ThenBy(g => g.Key.CellX)
            .Select(g =>
            {
                List<SpawnRecord> members = g.ToList();
                List<int> xs = members.Select(s => s.LocalX).OrderBy(v => v).ToList();
                List<int> ys = members.Select(s => s.LocalY).OrderBy(v => v).ToList();
                int medianX = xs[xs.Count / 2];
                int medianY = ys[ys.Count / 2];

                // The real spawn nearest the median point, so the camp stands where a mob does (and at its Z).
                SpawnRecord centre = members
                    .OrderBy(s => (long)(s.LocalX - medianX) * (s.LocalX - medianX) + (long)(s.LocalY - medianY) * (s.LocalY - medianY))
                    .ThenBy(s => s.LocalX)
                    .ThenBy(s => s.LocalY)
                    .ThenBy(s => s.Z)
                    .First();

                return new CampEntry
                {
                    Id = $"world:{g.Key.RegionId}:{g.Key.ZoneId}:{g.Key.CellX}:{g.Key.CellY}:{g.Key.Name}",
                    Name = g.Key.Name,
                    Zone = members[0].ZoneName ?? string.Empty,
                    ZoneId = (ushort)g.Key.ZoneId,
                    RegionId = (ushort)g.Key.RegionId,
                    LocalX = centre.LocalX,
                    LocalY = centre.LocalY,
                    Z = centre.Z,
                    Levels = members.Select(s => s.Level).Distinct().OrderBy(v => v).ToArray(),
                    Sightings = members.Count
                };
            })
            .ToList();
    }

    private static int FloorDiv(int value)
    {
        return (int)Math.Floor(value / (double)CampCellSize);
    }

    // Null means "not available now" (no server or loaded region) and is not cached.
    // The rows are the region's Mob table as WorldMgr read it at server start (RegionData.Mobs, kept in
    // memory), so this runs on the game loop without a database query.
    private static Dictionary<int, CampEntry[]> LoadRegion(int regionId)
    {
        if (GameServer.Instance == null || regionId < 0 || regionId > ushort.MaxValue)
        {
            return null;
        }

        try
        {
            Region region = WorldMgr.GetRegion((ushort)regionId);
            DbMob[] rows = region?.RegionData?.Mobs;

            if (rows == null)
            {
                return null;
            }

            List<SpawnRecord> spawns = new(rows.Length);

            foreach (DbMob row in rows)
            {
                // Classic quest NPCs and Darkness Falls raid/event spawns are not camps an act may use.
                if (row == null || string.Equals(row.PackageID, AutonomousDarknessFallsGoalScope.ClassicQuestPackage, StringComparison.Ordinal)
                    || AutonomousDarknessFallsGoalScope.IsRaidEventId(row.ObjectId))
                {
                    continue;
                }

                Zone zone = region.GetZone(row.X, row.Y);

                if (zone == null)
                {
                    continue;
                }

                spawns.Add(new SpawnRecord(row.Name, regionId, zone.ID, zone.Description, row.X - zone.XOffset, row.Y - zone.YOffset,
                    row.Z, row.Level, row.Realm, row.Flags, row.ClassType, row.Guild));
            }

            List<CampEntry> camps = BuildCamps(spawns);
            TryLogInfo($"[EXPEDITION] Region {regionId}: {camps.Count:N0} monster camps from {rows.Length:N0} Mob rows.");

            return camps
                .GroupBy(c => (int)c.ZoneId)
                .ToDictionary(g => g.Key, g => g.ToArray());
        }
        catch (Exception ex)
        {
            TryLogWarn($"[EXPEDITION] Could not read region {regionId}'s monsters from the world; the goal catalog is used.", ex);

            return null;
        }
    }

    private static void TryLogInfo(string message)
    {
        try
        {
            Log.Info(message);
        }
        catch
        {
        }
    }

    private static void TryLogWarn(string message, Exception exception)
    {
        try
        {
            Log.Warn(message, exception);
        }
        catch
        {
        }
    }
}
