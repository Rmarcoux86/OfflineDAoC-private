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
    "/gauntlet kit (GM: test gear for a fully templated level 50)")]
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
