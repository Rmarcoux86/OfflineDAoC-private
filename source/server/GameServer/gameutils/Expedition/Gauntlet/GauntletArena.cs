namespace DOL.GS.Expedition.Gauntlet;

/// <summary>
/// The Gauntlet's arena: the Master Level 10 arena, the one that looks like floating in space (owner decision,
/// docs/EXPEDITION-GAUNTLET-PLAN.md). It is used in the shared world region with one player at a time
/// (<see cref="Occupant"/>); an instance can come later if co-op needs it.
/// </summary>
public static class GauntletArena
{
    // TODO owner /loc: the arena's region, zone and coordinates are not in the repo (teleports live in the runtime
    // database). Until the owner sends /loc from the arena centre and from the gate, RegionId 0 marks the arena as not
    // set up and /gauntlet start refuses with NotSetUpMessage.

    /// <summary>The arena's region. TODO owner /loc (0: not set up).</summary>
    public const ushort RegionId = 0;

    /// <summary>The arena's zone, for reference. TODO owner /loc.</summary>
    public const int ZoneId = 0;

    /// <summary>Where the player is teleported and the champion fights. TODO owner /loc.</summary>
    public const int CentreX = 0;
    public const int CentreY = 0;
    public const int CentreZ = 0;
    public const ushort CentreHeading = 0;

    /// <summary>Where the champion appears and walks in from. TODO owner /loc.</summary>
    public const int GateX = 0;
    public const int GateY = 0;
    public const int GateZ = 0;
    public const ushort GateHeading = 0;

    /// <summary>The release point after a Gauntlet death; it must be in the arena's own region. TODO owner /loc.</summary>
    public const int EntranceX = 0;
    public const int EntranceY = 0;
    public const int EntranceZ = 0;
    public const ushort EntranceHeading = 0;

    /// <summary>How far from the centre still counts as inside the arena (2D).</summary>
    public const int Radius = 3000;

    public const string NotSetUpMessage = "The Gauntlet arena is not set up yet.";

    private static readonly object Sync = new();
    private static GamePlayer _occupant;

    /// <summary>False while the arena coordinates are the placeholders above.</summary>
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
