using System.Collections.Generic;
using System.Linq;
using CampEntry = DOL.GS.AutonomousCapnBryGoalCatalog.Entry;

namespace DOL.GS.Expedition;

/// <summary>
/// Where expedition acts find their monster camps: one entry per camp, with zone-local
/// coordinates, the levels seen there and how many spawns (or sightings) it has.
/// </summary>
public interface IExpeditionCampSource
{
    /// <summary>The zone's camps in a fixed order; never null, empty when the source knows none.</summary>
    IReadOnlyList<CampEntry> GetZoneCamps(int regionId, int zoneId);
}

public static class ExpeditionCampEdges
{
    /// <summary>
    /// Fork (owner 2026-10-25): camps this close to a zone's border are left out; they sit on or behind the zone wall
    /// (Myrkwood's wind wisps at /loc 5598, 271 could not be reached). Zones are 65536 units across.
    /// </summary>
    public const int Margin = 500;

    public static bool OnZoneEdge(CampEntry e)
    {
        return e.LocalX < Margin || e.LocalY < Margin || e.LocalX > 65536 - Margin || e.LocalY > 65536 - Margin;
    }
}

/// <summary>The CapnBry bestiary snapshot (<see cref="AutonomousCapnBryGoalCatalog"/>), in catalog order.</summary>
public sealed class CatalogCampSource : IExpeditionCampSource
{
    public static readonly CatalogCampSource Instance = new();

    private CatalogCampSource()
    {
    }

    public IReadOnlyList<CampEntry> GetZoneCamps(int regionId, int zoneId)
    {
        return AutonomousCapnBryGoalCatalog.Entries
            .Where(e => e.ZoneId == zoneId && e.RegionId == regionId && !ExpeditionCampEdges.OnZoneEdge(e))
            .ToList();
    }
}

/// <summary>
/// The default source: the live world's Mob table (<see cref="WorldCampSource"/>) for every
/// zone it has camps in, the CapnBry catalog for the rest (no database, world not loaded, or
/// a zone the world leaves empty).
/// </summary>
public sealed class WorldThenCatalogCampSource : IExpeditionCampSource
{
    public static readonly WorldThenCatalogCampSource Instance = new();

    private WorldThenCatalogCampSource()
    {
    }

    public IReadOnlyList<CampEntry> GetZoneCamps(int regionId, int zoneId)
    {
        IReadOnlyList<CampEntry> world = WorldCampSource.Instance.GetZoneCamps(regionId, zoneId).Where(e => !ExpeditionCampEdges.OnZoneEdge(e)).ToList();

        if (world.Count > 0)
        {
            return world;
        }

        return CatalogCampSource.Instance.GetZoneCamps(regionId, zoneId);
    }
}
