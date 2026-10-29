using System;

namespace DOL.GS.Expedition.Gauntlet;

/// <summary>
/// The Gauntlet's arena: the Master Level 10 arena, the one that looks like floating in space (owner decision,
/// docs/EXPEDITION-GAUNTLET-PLAN.md). It is used in the shared world region with one player at a time
/// (<see cref="Occupant"/>); an instance can come later if co-op needs it.
/// </summary>
public static class GauntletArena
{
    // The owner's /loc readings in Celestius (2026-10-25), zone-local; Celestius' zone sits at 8192, 8192 in its region,
    // so the world coordinates below are the readings plus 8192. The client's arrival pad (the "exit to Aerus" zone
    // jump at about 23680, 26795) is avoided: standing on it freezes the client on an invalid zone point.
    //   player spawn: Celestius loc=22902,22092,15733 dir=90
    //   champion spawn: Celestius loc=24595,22079,15733 dir=265 (owner 2026-10-25: "boss spawn here"; 23690,23337 was
    //   the client's own entry spot, used by mistake at first)
    // The centre (leash and walk-in target) is halfway between them.

    /// <summary>Celestius, the ML arena floating in space.</summary>
    public const ushort RegionId = 91;

    /// <summary>The arena's zone, for reference.</summary>
    public const int ZoneId = 91;

    /// <summary>Where the player arrives (the owner's player spawn).</summary>
    public const int PlayerX = 22902 + 8192;
    public const int PlayerY = 22092 + 8192;
    public const int PlayerZ = 15733;
    /// <remarks>Owner 2026-10-28: turned 180 degrees (was 90), so the player faces the champion's gate on arrival.</remarks>
    public const ushort PlayerHeading = 270 * 4096 / 360;

    /// <summary>Where the champion appears and walks in from (the owner's champion spawn).</summary>
    public const int GateX = 24595 + 8192;
    public const int GateY = 22079 + 8192;
    public const int GateZ = 15733;
    public const ushort GateHeading = 265 * 4096 / 360;

    /// <summary>The middle of the fight: the champion walks here, and its leash is measured from here.</summary>
    public const int CentreX = (PlayerX + GateX) / 2;
    public const int CentreY = (PlayerY + GateY) / 2;
    public const int CentreZ = (PlayerZ + GateZ) / 2;
    public const ushort CentreHeading = PlayerHeading;

    /// <summary>The release point after a Gauntlet death: the player spawn, in the arena's region.</summary>
    public const int EntranceX = PlayerX;
    public const int EntranceY = PlayerY;
    public const int EntranceZ = PlayerZ;
    public const ushort EntranceHeading = PlayerHeading;

    /// <summary>How far from the centre still counts as inside the arena (2D).</summary>
    public const int Radius = 3000;

    public const string NotSetUpMessage = "The Gauntlet arena is not set up yet.";

    /// <summary>How far each of a two-champion round's champions stands from the gate, to either side (owner 2026-10-29).</summary>
    public const int PairOffset = 150;

    /// <summary>
    /// Where one of a two-champion round's champions appears: <see cref="PairOffset"/> from the gate along the line
    /// perpendicular to the gate-to-player direction, to the left (<paramref name="side"/> -1) or the right (+1).
    /// </summary>
    public static (int X, int Y) PairSpawn(int side)
    {
        double dx = PlayerX - GateX;
        double dy = PlayerY - GateY;
        double length = Math.Sqrt(dx * dx + dy * dy);

        return ((int) Math.Round(GateX - dy / length * PairOffset * side), (int) Math.Round(GateY + dx / length * PairOffset * side));
    }

    private static readonly object Sync = new();
    private static GamePlayer _occupant;

    /// <summary>False while the arena has no region (it is set now; kept so a placeholder can switch it off).</summary>
    public static bool IsConfigured => RegionId != 0;

    /// <summary>The player in the Gauntlet now, or null.</summary>
    public static GamePlayer Occupant
    {
        get
        {
            lock (Sync)
            {
                return _occupant;
            }
        }
    }

    /// <summary>True when the object stands in the arena's region within <see cref="Radius"/> of the centre.</summary>
    public static bool IsInside(GameObject obj)
    {
        if (!IsConfigured || obj == null || obj.CurrentRegionID != RegionId)
        {
            return false;
        }

        long dx = obj.X - CentreX;
        long dy = obj.Y - CentreY;

        return dx * dx + dy * dy <= (long) Radius * Radius;
    }

    /// <summary>
    /// Claims the arena for <paramref name="player"/>. A former occupant who has left the arena or the game no longer
    /// holds it. False when someone else is in the Gauntlet.
    /// </summary>
    public static bool TryClaim(GamePlayer player)
    {
        if (player == null)
        {
            return false;
        }

        lock (Sync)
        {
            GamePlayer current = _occupant;
            bool held = current != null && current != player && current.ObjectState == GameObject.eObjectState.Active &&
                current.CurrentRegionID == RegionId;

            if (held)
            {
                return false;
            }

            _occupant = player;

            return true;
        }
    }

    /// <summary>Frees the arena when <paramref name="player"/> holds it.</summary>
    public static void Release(GamePlayer player)
    {
        lock (Sync)
        {
            if (_occupant == player)
            {
                _occupant = null;
            }
        }
    }
}
