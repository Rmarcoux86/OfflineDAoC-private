using DOL.GS.Expedition.Gauntlet;
using DOL.GS.PacketHandler;

namespace DOL.GS.Commands;

/// <summary>The Gauntlet after a won expedition (<see cref="GauntletManager"/>). Prototype; a Keeper NPC comes later.</summary>
[CmdAttribute(
    "&gauntlet",
    ePrivLevel.Player,
    "The Gauntlet: round after round of champions, for a character that has won its expedition.",
    "/gauntlet start",
    "/gauntlet leave",
    "/gauntlet status",
    "/gauntlet reset [round] (back to round 1; GMs and test winners may choose 1 to 5, or 6 after a hard run)",
    "/gauntlet kit (GM: test gear for a fully templated level 50)",
    "/gauntlet testwinner [hard] (GM: mark your target, or yourself, as an expedition winner for testing; hard: as a hard run's winner, with the sixth round)")]
public class GauntletCommandHandler : AbstractCommandHandler, ICommandHandler
{
    public void OnCommand(GameClient client, string[] args)
    {
        GamePlayer player = client.Player;

        if (player == null || IsSpammingCommand(player, "gauntlet"))
        {
            return;
        }

        if (args.Length < 2)
        {
            DisplaySyntax(client);

            return;
        }

        switch (args[1].ToLowerInvariant())
        {
            case "start":
            {
                if (!GauntletManager.TryStart(player, out string error))
                {
                    client.Out.SendMessage(error, eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                }

                break;
            }
            case "leave":
            {
                if (!GauntletManager.TryLeave(player, out string error) && !string.IsNullOrEmpty(error))
                {
                    client.Out.SendMessage(error, eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                }

                break;
            }
            case "status":
            {
                client.Out.SendCustomTextWindow("The Gauntlet", GauntletManager.StatusLines(player));

                break;
            }
            case "testwinner":
            {
                // GMs only (owner 2026-10-28: testing the Gauntlet as an ordinary player).
                if (client.Account.PrivLevel <= (uint) ePrivLevel.Player)
                {
                    DisplaySyntax(client);

                    break;
                }

                GamePlayer target = player.TargetObject as GamePlayer ?? player;
                bool hard = args.Length >= 3 && args[2].ToLowerInvariant() == "hard";
                client.Out.SendMessage(GauntletTestKit.MarkWinner(target, hard), eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                break;
            }
            case "reset":
            {
                int round = 1;

                if (args.Length >= 3 && !int.TryParse(args[2], out round))
                {
                    DisplaySyntax(client);

                    break;
                }

                client.Out.SendMessage(GauntletManager.Reset(player, round), eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                break;
            }
            case "kit":
            {
                // GMs only (owner 2026-10-28: testing the champions as a templated level 50 without boons).
                if (client.Account.PrivLevel <= (uint) ePrivLevel.Player)
                {
                    DisplaySyntax(client);

                    break;
                }

                client.Out.SendMessage(GauntletTestKit.Give(player), eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                break;
            }
            default:
            {
                DisplaySyntax(client);

                break;
            }
        }
    }
}
