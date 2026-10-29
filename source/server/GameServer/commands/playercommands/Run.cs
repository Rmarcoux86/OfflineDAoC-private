using System;
using System.Collections.Generic;
using System.Linq;
using DOL.GS.Expedition;
using DOL.GS.PacketHandler;

namespace DOL.GS.Commands;

/// <summary>Expedition mode (<see cref="ExpeditionManager"/>).</summary>
[CmdAttribute(
    "&run",
    ePrivLevel.Player,
    "Expedition mode: a seeded roguelite run from level 1 to the final boss.",
    "/run new [seed]",
    "/run status",
    "/run journal",
    "/run shrines",
    "/run boons",
    "/run boon <1-3> (1-2 on a hard expedition)",
    "/run reroll",
    "/run seed",
    "/run new hard [seed] (a hard expedition, for an account that has beaten the Gauntlet)",
    "/run rules (the rules of a hard expedition)",
    "/run inherit <n> (a hard expedition: take one unique from your Gauntlet winner, once)",
    "/run abandon",
    "/run gauntlet",
    "/run debug")]
public class RunCommandHandler : AbstractCommandHandler, ICommandHandler
{
    public void OnCommand(GameClient client, string[] args)
    {
        GamePlayer player = client.Player;

        if (player == null || IsSpammingCommand(player, "run"))
        {
            return;
        }

        if (args.Length < 2)
        {
            DisplaySyntax(client);

            return;
        }

        ExpeditionRun run = ExpeditionManager.GetRun(player);

        switch (args[1].ToLowerInvariant())
        {
            case "new":
            {
                int? seed = null;
                bool hard = args.Length >= 3 && string.Equals(args[2], "hard", StringComparison.OrdinalIgnoreCase);
                int seedIndex = hard ? 3 : 2;

                if (hard && !ExpeditionHardMode.IsUnlocked(player))
                {
                    client.Out.SendMessage("A hard expedition opens to an account that has beaten the Gauntlet.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                    return;
                }

                if (args.Length > seedIndex)
                {
                    if (!int.TryParse(args[seedIndex], out int parsed))
                    {
                        client.Out.SendMessage("The seed must be a whole number.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                        return;
                    }

                    seed = parsed;
                }

                if (run != null)
                {
                    client.Out.SendMessage("You are already on an expedition. Type /run status, or /run abandon to give it up.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                    return;
                }

                // TryStartRun checks everything again when the answer comes.
                string question = hard
                    ? "Start a HARD expedition? Everything is stronger and the safety nets are gone (/run rules). Your Hardcore flag will be ON: death ends the run and deletes this character."
                    : "Start an expedition? Your Hardcore flag will be ON: death ends the run and deletes this character.";

                client.Out.SendCustomDialog(question, (dialogPlayer, response) =>
                {
                    if (response != 1)
                    {
                        dialogPlayer.Out.SendMessage("Use /run new again if you change your mind.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                        return;
                    }

                    if (!ExpeditionManager.TryStartRun(dialogPlayer, seed, out string error, hard))
                    {
                        dialogPlayer.Out.SendMessage(error, eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    }
                });

                return;
            }

            case "rules":
            {
                client.Out.SendCustomTextWindow("Hard expedition", ExpeditionHardMode.RulesLines());

                return;
            }

            case "inherit":
            {
                if (run == null || !ExpeditionHardMode.IsHard(run))
                {
                    client.Out.SendMessage("Only a hard expedition inherits a unique. Type /run new hard to start one.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                    return;
                }

                // Without a number the offer is shown.
                if (args.Length < 3)
                {
                    List<string> offer = ExpeditionHardMode.InheritOfferLines(player, run);

                    if (offer.Count == 0)
                    {
                        string none = string.IsNullOrEmpty(run.InheritedUnique) ? "Your account's Gauntlet winners carry no unique to inherit." : $"This expedition has already inherited {run.InheritedUnique}.";
                        client.Out.SendMessage(none, eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                        return;
                    }

                    client.Out.SendCustomTextWindow("Inherit a unique", offer);

                    return;
                }

                if (!int.TryParse(args[2], out int pick))
                {
                    client.Out.SendMessage("Type /run inherit followed by the number of the unique, for example /run inherit 2.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                    return;
                }

                client.Out.SendMessage(ExpeditionHardMode.TryInherit(player, run, pick), eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                return;
            }

            case "status":
            {
                if (run == null)
                {
                    client.Out.SendMessage("You are not on an expedition. Type /run new to start one at level 1.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                    return;
                }

                ActPlan plan = run.CurrentAct;
                int done = plan?.Objectives?.Count(objective => objective.Done) ?? 0;
                int total = plan?.Objectives?.Count ?? 0;
                string boss = plan == null ? "unknown" : plan.BossKilled ? "slain" : plan.BossSpawned ? "awake" : "waiting";

                client.Out.SendMessage($"Expedition act {run.Act}/{ActGenerator.ActCount} | levels {plan?.MinLevel}-{plan?.MaxLevel} | {plan?.ZoneName} | objectives {done}/{total} | boss {boss}", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                client.Out.SendMessage(run.LootSummary, eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                return;
            }

            case "journal":
            {
                if (run == null)
                {
                    client.Out.SendMessage("You are not on an expedition. Type /run new to start one at level 1.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                    return;
                }

                client.Out.SendCustomTextWindow("Expedition journal", ExpeditionManager.JournalLines(run).ToList());

                return;
            }

            case "shrines":
            {
                // Fork (owner 2026-10-25): which way each shrine lies from where you stand now, so passing one shows.
                ActPlan plan = run?.CurrentAct;

                if (plan == null)
                {
                    client.Out.SendMessage("You are not on an expedition. Type /run new to start one at level 1.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                    return;
                }

                if (plan.Shrines == null || plan.Shrines.Count == 0)
                {
                    client.Out.SendMessage("This act has no shrines.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                    return;
                }

                foreach (ShrineSpec shrine in plan.Shrines.Where(s => s != null))
                {
                    string name = shrine.Discovered ? ExpeditionBuffs.ShrineName(shrine.Group) : "Undiscovered shrine";

                    if (shrine.Used)
                    {
                        client.Out.SendMessage($"{name}: blessing received.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

                        continue;
                    }

                    if (shrine.RegionId != player.CurrentRegionID)
                    {
                        client.Out.SendMessage($"{name}: in {plan.ZoneName}, not in this region.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

                        continue;
                    }

                    double distance = System.Math.Sqrt((double) (shrine.X - player.X) * (shrine.X - player.X) + (double) (shrine.Y - player.Y) * (shrine.Y - player.Y));
                    string howFar = distance < 1500 ? "very close" : distance < 4000 ? "close" : distance < 10000 ? "some way off" : "far off";
                    string where = distance < 500 ? "right here" : $"{ActGenerator.CompassDirection(player.X, player.Y, shrine.X, shrine.Y)} of you, {howFar} (about {(int) distance} units)";
                    string guarded = shrine.Guarded ? ", guarded" : string.Empty;
                    client.Out.SendMessage($"{name}{guarded}: {where}.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                }

                return;
            }

            case "boons":
            case "boon":
            {
                if (run == null)
                {
                    client.Out.SendMessage("You are not on an expedition. Type /run new to start one at level 1.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                    return;
                }

                // "/run boon 2" (or "/run boons 2") takes a boon; without a number the offer is shown.
                if (args.Length < 3)
                {
                    client.Out.SendCustomTextWindow("Expedition boons", ExpeditionBoons.OfferLines(run));

                    return;
                }

                if (!int.TryParse(args[2], out int choice))
                {
                    client.Out.SendMessage("Type /run boon followed by the number of the boon, for example /run boon 2.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                    return;
                }

                ExpeditionBoons.TryChoose(player, run, choice, out string message);
                client.Out.SendMessage(message, eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                return;
            }

            case "reroll":
            {
                if (run == null)
                {
                    client.Out.SendMessage("You are not on an expedition. Type /run new to start one at level 1.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                    return;
                }

                ExpeditionBoons.TryReroll(player, run, out string rerollMessage);
                client.Out.SendMessage(rerollMessage, eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                return;
            }

            case "debug":
            {
                bool on = !ExpeditionDebug.IsChatOn(player);
                player.TempProperties.SetProperty(ExpeditionDebug.ChatKey, on);

                string attached = run == null ? "no active run" : $"tracker attached: {ObjectiveTracker.IsAttached(player)}";
                client.Out.SendMessage($"Expedition debug {(on ? "ON" : "OFF")} ({attached}). Kills and loot rolls are explained in this window.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                return;
            }

            case "seed":
            {
                if (run == null)
                {
                    client.Out.SendMessage("You are not on an expedition. Type /run new to start one at level 1.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                    return;
                }

                client.Out.SendMessage($"Your expedition seed is {run.Seed}. Start another character with /run new {run.Seed} to play the same expedition.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                return;
            }

            case "abandon":
            {
                if (run == null)
                {
                    client.Out.SendMessage("You are not on an expedition.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                    return;
                }

                client.Out.SendCustomDialog("Abandon your expedition? The run ends here and your Hardcore flag is turned off.", (dialogPlayer, response) =>
                {
                    if (response != 1)
                    {
                        dialogPlayer.Out.SendMessage("Your expedition goes on.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                        return;
                    }

                    ExpeditionManager.Abandon(dialogPlayer);
                });

                return;
            }

            case "gauntlet":
            {
                // The Gauntlet, once the expedition is won (same as /gauntlet start).
                if (!DOL.GS.Expedition.Gauntlet.GauntletManager.TryStart(player, out string error) && !string.IsNullOrEmpty(error))
                {
                    client.Out.SendMessage(error, eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                }

                return;
            }

            default:
            {
                DisplaySyntax(client);

                return;
            }
        }
    }
}
