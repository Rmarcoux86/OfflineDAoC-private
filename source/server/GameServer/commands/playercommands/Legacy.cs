using DOL.GS.Expedition;
using DOL.GS.PacketHandler;

namespace DOL.GS.Commands;

/// <summary>/legacy: the account's Expedition Renown and permanent perks (<see cref="ExpeditionLegacy"/>).</summary>
[CmdAttribute(
    "&legacy",
    ePrivLevel.Player,
    "Expedition Legacy: Renown banked from your runs and the permanent perks it buys.",
    "/legacy",
    "/legacy buy <number>")]
public class LegacyCommandHandler : AbstractCommandHandler, ICommandHandler
{
    public void OnCommand(GameClient client, string[] args)
    {
        GamePlayer player = client?.Player;

        if (player == null)
        {
            return;
        }

        if (IsSpammingCommand(player, "legacy"))
        {
            return;
        }

        if (args.Length >= 2 && args[1].ToLowerInvariant() == "buy")
        {
            if (args.Length < 3 || !int.TryParse(args[2], out int choice))
            {
                client.Out.SendMessage("Type /legacy buy followed by the perk's number, for example /legacy buy 1.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                return;
            }

            ExpeditionLegacy.TryBuy(player, choice, out string message);
            client.Out.SendMessage(message, eChatType.CT_Important, eChatLoc.CL_SystemWindow);

            return;
        }

        client.Out.SendCustomTextWindow("Expedition Legacy", ExpeditionLegacy.DescribeLines(player));
    }
}
