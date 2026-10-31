using System;
using System.Linq;
using System.Reflection;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// Fork (owner 2026-10-25): a run whose character died and was restored with /recover carries on where it stopped.
/// /recover marks the run <see cref="StatusRecovered"/>; the next login resumes it like an active run, takes the player
/// to the act's camp (made safe first) and makes sure the act break's boon choice is still waiting.
/// </summary>
public static class ExpeditionRecovery
{
    private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>A died run brought back by /recover, waiting for its character's next login.</summary>
    public const string StatusRecovered = "recovered";

    /// <summary>
    /// Marks the run of a restored character to resume at its next login. Only a run that ended in death is revived.
    /// Returns true when the run will resume (the character should be hardcore again).
    /// </summary>
    public static bool ReviveRun(string characterId)
    {
        OfflineExpeditionRecord record;

        try
        {
            record = GameServer.Database.FindObjectByKey<OfflineExpeditionRecord>(characterId);
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] Could not load the run of character {characterId} to revive it.", ex);

            return false;
        }

        // A hard expedition has no recovery (ExpeditionHardMode; /recover refuses the character before this).
        if (record == null || record.Status != ExpeditionManager.StatusDead || record.Hard)
        {
            return false;
        }

        record.Status = StatusRecovered;
        record.EndedUtc = string.Empty;
        record.SummaryShown = true;

        try
        {
            GameServer.Database.SaveObject(record);
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] Could not revive the run of {record.CharacterName}.", ex);

            return false;
        }

        return true;
    }

    /// <summary>
    /// Called by the login resume for a run that was <see cref="StatusRecovered"/>, before the act is tracked again:
    /// makes the run active, plans the act again when its zone is no longer an expedition zone, and offers the boon
    /// the act break owes if none is waiting. Saves the run.
    /// </summary>
    public static void PrepareRecoveredRun(GamePlayer player, ExpeditionRun run)
    {
        if (player == null || run == null)
        {
            return;
        }

        run.Status = ExpeditionManager.StatusActive;
        run.EndedUtc = null;
        run.AddLog($"Act {run.Act}: {run.CharacterName} was restored after a death and carries on.");

        ReplanIfZoneRemoved(player, run, true);

        // One boon per act won. A death at the start of an act can come before the choice was made; the offer is
        // saved with the run, so it is normally still waiting. Only when it is gone is it drawn again (same seed, same act).
        bool owed;

        lock (run.Boons)
        {
            owed = run.PendingBoonOffer.Count == 0 && run.Boons.Count < run.Act - 1;
        }

        if (owed)
        {
            ExpeditionBoons.OfferAtActBreak(run, run.Act - 1, out _, ExpeditionBoons.UsesPower(player), ExpeditionBoons.HasPet(player),
                ExpeditionBoons.FightsInMelee(player), ExpeditionBoons.IsRanged(player), ExpeditionHardMode.IsHard(run), ExpeditionBoons.UsesPoisons(player));
        }

        // The login resume then makes the camp safe (ExpeditionArrival) and takes the player there once settled.
        ExpeditionManager.Save(run);
    }

    /// <summary>
    /// Plans the current act again when its zone is no longer one of that act's expedition zones (zones taken out on
    /// 2026-10-25: the dungeons, and Gripklosa Mountains and Lyonesse from act 3). Without <paramref name="force"/> an
    /// act that has begun (any objective progress, or its boss up) keeps its zone. The caller saves the run.
    /// Returns true when the act was planned again.
    /// </summary>
    public static bool ReplanIfZoneRemoved(GamePlayer player, ExpeditionRun run, bool force)
    {
        ActPlan plan = run?.CurrentAct;

        if (player == null || run == null)
        {
            return false;
        }

        if (plan != null && ActGenerator.GetCandidates(run.Act, player.Realm).Any(z => z.ZoneId == plan.ZoneId))
        {
            return false;
        }

        if (!force && plan != null && (plan.BossSpawned || plan.Objectives.Any(o => o.Progress > 0)))
        {
            return false;
        }

        try
        {
            ActPlan replanned = ActGenerator.Generate(run.Act, player.Realm, run.Seed, -1, player.Level);

            if (replanned == null)
            {
                return false;
            }

            run.AddLog($"Act {run.Act}: {plan?.ZoneName ?? "the old zone"} is no longer an act {run.Act} zone; the act moves to {replanned.ZoneName}.");
            run.CurrentAct = replanned;

            return true;
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] Act {run.Act} of {run.CharacterName} could not be planned again.", ex);

            return false;
        }
    }
}
