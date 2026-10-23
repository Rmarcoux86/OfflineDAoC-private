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
    "/run boons",
    "/run boon <1-3>",
    "/run seed",
    "/run abandon",
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

                if (args.Length >= 3)
                {
                    if (!int.TryParse(args[2], out int parsed))
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
                client.Out.SendCustomDialog("Start an expedition? Your Hardcore flag will be ON: death ends the run and deletes this character.", (dialogPlayer, response) =>
                {
                    if (response != 1)
                    {
                        dialogPlayer.Out.SendMessage("Use /run new again if you change your mind.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                        return;
                    }

                    if (!ExpeditionManager.TryStartRun(dialogPlayer, seed, out string error))
                    {
                        dialogPlayer.Out.SendMessage(error, eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    }
                });

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

            default:
            {
                DisplaySyntax(client);

                return;
            }
        }
    }
}
