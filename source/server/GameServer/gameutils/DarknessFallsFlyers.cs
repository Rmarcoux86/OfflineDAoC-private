using System.Numerics;
using DOL.AI.Brain;

namespace DOL.GS;

/// <summary>
/// Darkness Falls's flying demons (molochian tempters, succubi, avernal quasits, chaosians) spawn hovering up to about
/// 1,100 units above the floor, out of reach of throwing weapons (1,160 range, measured in 3D) and awkward for any
/// ranged attack (owner 2026-10-27). One that spawns more than <see cref="MaxHover"/> above the floor beneath it is set
/// <see cref="Hover"/> above that floor instead; it still flies. Applied in GameNPC.AddToWorld, so respawns keep it.
/// </summary>
public static class DarknessFallsFlyers
{
    public const ushort RegionId = 249;
    public const int MaxHover = 300;
    public const int Hover = 150;

    /// <summary>How deep below the spawn the floor is looked for.</summary>
    public const int MaxDepth = 2500;

    /// <summary>The height to spawn at, or null to keep it: Hover above the floor when the spawn is more than MaxHover above it.</summary>
    public static int? LoweredZ(int z, int floorZ)
    {
        return z - floorZ > MaxHover ? floorZ + Hover : null;
    }

    /// <summary>The lowered height for this monster as it is added to the world, or null to keep its own.</summary>
    public static int? LoweredZ(GameNPC npc)
    {
        if (npc == null || npc.CurrentRegionID != RegionId || (npc.Flags & GameNPC.eFlags.FLYING) == 0 || npc.Realm != eRealm.None ||
            npc is GameBot || npc.Brain is IControlledBrain)
        {
            return null;
        }

        Zone zone = npc.CurrentRegion?.GetZone(npc.X, npc.Y);
        IPathfindingMgr nav = PathfindingProvider.Instance;

        if (zone == null || nav == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
        {
            return null;
        }

        Vector3? floor = nav.GetFloorBeneath(zone, new Vector3(npc.X, npc.Y, npc.Z), MaxDepth, nav.DefaultFilters);

        return floor.HasValue ? LoweredZ(npc.Z, (int) floor.Value.Z) : null;
    }
}
