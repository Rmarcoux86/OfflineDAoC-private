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
            .Where(e => e.ZoneId == zoneId && e.RegionId == regionId)
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
        IReadOnlyList<CampEntry> world = WorldCampSource.Instance.GetZoneCamps(regionId, zoneId);

        if (world.Count > 0)
        {
            return world;
        }

        return CatalogCampSource.Instance.GetZoneCamps(regionId, zoneId);
    }
}
