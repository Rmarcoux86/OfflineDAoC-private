using System;
using System.Collections.Generic;

namespace DOL.GS;

/// <summary>
/// Respawn times (in seconds) that replace the database value for a monster by name. Updates ship the server code
/// but not the database, so a change to a spawn's timer lives here. Applied in GameNPC.LoadFromDatabase.
/// </summary>
public static class MobRespawnOverrides
{
    private static readonly Dictionary<string, int> Seconds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Darkness Falls (owner, 2026-10-26): 2 minutes in the database.
        ["naburite drinker"] = 180
    };

    /// <summary>The respawn time in seconds for a monster with this name and database value.</summary>
    public static int For(string name, int databaseSeconds)
    {
        if (databaseSeconds > 0 && name != null && Seconds.TryGetValue(name, out int seconds))
        {
            return seconds;
        }

        return databaseSeconds;
    }
}
