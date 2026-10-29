using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.GS.Expedition.Gauntlet;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// Hard mode (owner 2026-10-29): a second expedition on a new character, for an account that has beaten the Gauntlet,
/// with everything stronger and the safety nets gone. Every hard-mode rule reads <see cref="IsHard"/> and is listed in
/// <see cref="RulesLines"/>, so a death reads as a mistake and not a surprise.
/// </summary>
public static class ExpeditionHardMode
{
    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>True for a hard-mode run.</summary>
    public static bool IsHard(ExpeditionRun run)
    {
        return run?.Hard == true;
    }

    /// <summary>True while the player is on a hard-mode run.</summary>
    public static bool IsHard(GamePlayer player)
    {
        return IsHard(ExpeditionManager.GetRun(player));
    }

    /// <summary>
    /// True when a run on the player's account won its expedition and then beat the Gauntlet (every round reached; a
    /// /gauntlet testwinner record, with no end time, does not count).
    /// </summary>
    public static bool IsUnlocked(GamePlayer player)
    {
        string account = player?.Client?.Account?.Name;

        if (string.IsNullOrEmpty(account))
        {
            return false;
        }

        try
        {
            return GameServer.Database.SelectObjects<OfflineExpeditionRecord>(DB.Column("AccountName").IsEqualTo(account))
                .Any(record => record.Status == ExpeditionManager.StatusWon && !string.IsNullOrEmpty(record.EndedUtc) &&
                    GauntletSave.Parse(record.GauntletJson).Depth >= GauntletRules.Rounds);
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Could not read the runs of account {account} for the hard-mode unlock.", ex);

            return false;
        }
    }

    /// <summary>The rules of a hard expedition, for /run rules and the first act's arrival.</summary>
    public static List<string> RulesLines()
    {
        return new List<string>
        {
            "A hard expedition. Everything that follows is stronger than you remember:",
            string.Empty,
            "- Opens to an account that has beaten the Gauntlet.",
        };
    }
}
