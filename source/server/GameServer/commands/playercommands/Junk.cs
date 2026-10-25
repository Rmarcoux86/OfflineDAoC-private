using System.Collections.Generic;
using DOL.Database;
using DOL.GS.Expedition;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;

namespace DOL.GS.Commands;

/// <summary>
/// /junk: clears unwanted Expedition drops from the backpack for what a merchant would pay, or sells them as they drop.
/// Only Expedition items (description ending in " | Expedition {rarity}", <see cref="ExpeditionLoot.TryGetRarity"/>)
/// are ever touched, never Unique ones (Epic only with /junk epic), and never equipped items or the vault.
/// </summary>
[CmdAttribute(
    "&junk",
    ePrivLevel.Player,
    "Expedition loot cleanup: destroy or auto-sell unwanted drops.",
    "/junk common",
    "/junk magic",
    "/junk rare",
    "/junk epic",
    "/junk auto off",
    "/junk auto common",
    "/junk auto magic",
    "/junk status")]
public class JunkCommandHandler : AbstractCommandHandler, ICommandHandler
{
    public void OnCommand(GameClient client, string[] args)
    {
        GamePlayer player = client?.Player;

        if (player == null)
        {
            return;
        }

        if (IsSpammingCommand(player, "junk"))
        {
            return;
        }

        string sub = args.Length >= 2 ? args[1].ToLowerInvariant() : string.Empty;

        switch (sub)
        {
            case "common":
            {
                AskToJunk(player, eLootRarity.Common);

                break;
            }
            case "magic":
            {
                AskToJunk(player, eLootRarity.Magic);

                break;
            }
            case "rare":
            {
                AskToJunk(player, eLootRarity.Rare);

                break;
            }
            case "epic":
            {
                AskToJunk(player, eLootRarity.Epic);

                break;
            }
            case "auto":
            {
                SetAuto(client, player, args.Length >= 3 ? args[2].ToLowerInvariant() : string.Empty);

                break;
            }
            case "status":
            {
                ShowStatus(player);

                break;
            }
            default:
            {
                DisplaySyntax(client);

                break;
            }
        }
    }

    /// <summary>The Expedition items in the backpack that are junk at this threshold, and what a merchant would pay for them.</summary>
    private static List<DbInventoryItem> FindJunk(GamePlayer player, eLootRarity threshold, out long total)
    {
        List<DbInventoryItem> junk = new();
        total = 0;

        foreach (DbInventoryItem item in player.Inventory.GetItemRange(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack))
        {
            if (!ExpeditionLoot.TryGetRarity(item, out eLootRarity rarity) || !ExpeditionLoot.IsJunk(rarity, threshold))
            {
                continue;
            }

            junk.Add(item);
            total += ExpeditionLoot.SellValue(item.Price, item.Count, item.PackSize, Properties.ITEM_SELL_RATIO);
        }

        return junk;
    }

    private static string Range(eLootRarity threshold)
    {
        return threshold == eLootRarity.Common ? "[Common]" : $"[{threshold}] and lower";
    }

    private static void AskToJunk(GamePlayer player, eLootRarity threshold)
    {
        List<DbInventoryItem> junk = FindJunk(player, threshold, out long total);

        if (junk.Count == 0)
        {
            player.Out.SendMessage($"Your backpack holds no {Range(threshold)} Expedition items.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

            return;
        }

        string question = $"Destroy {junk.Count} {Range(threshold)} Expedition item{(junk.Count == 1 ? string.Empty : "s")} from your backpack for {Money.GetString(total)}?";
        player.Out.SendCustomDialog(question, (responder, response) => OnConfirm(responder, response, threshold));
    }

    /// <summary>The dialog's answer. The backpack is read again, so an item moved or sold meanwhile is not destroyed or paid.</summary>
    private static void OnConfirm(GamePlayer player, byte response, eLootRarity threshold)
    {
        if (player == null || response != 0x01)
        {
            return;
        }

        List<DbInventoryItem> junk = FindJunk(player, threshold, out _);
        int removed = 0;
        long paid = 0;

        foreach (DbInventoryItem item in junk)
        {
            long value = ExpeditionLoot.SellValue(item.Price, item.Count, item.PackSize, Properties.ITEM_SELL_RATIO);
            DbItemTemplate template = item.Template;
            int count = item.Count;

            if (!player.Inventory.RemoveItem(item))
            {
                continue;
            }

            InventoryLogging.LogInventoryAction(player, "(junk)", eInventoryActionType.Merchant, template, count);
            removed++;
            paid += value;
        }

        if (removed == 0)
        {
            player.Out.SendMessage("Nothing was junked.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

            return;
        }

        player.AddMoney(paid, $"You junk {removed} Expedition item{(removed == 1 ? string.Empty : "s")} and receive {{0}}.");
        InventoryLogging.LogInventoryAction("(junk)", player, eInventoryActionType.Merchant, paid);
    }

    private void SetAuto(GameClient client, GamePlayer player, string value)
    {
        eLootRarity? setting;

        switch (value)
        {
            case "off":
            {
                setting = null;

                break;
            }
            case "common":
            {
                setting = eLootRarity.Common;

                break;
            }
            case "magic":
            {
                setting = eLootRarity.Magic;

                break;
            }
            default:
            {
                DisplaySyntax(client);

                return;
            }
        }

        if (setting.HasValue && !Properties.EXPEDITION_AUTO_JUNK)
        {
            player.Out.SendMessage("Auto-junk is turned off on this server (expedition_auto_junk).", eChatType.CT_System, eChatLoc.CL_SystemWindow);

            return;
        }

        ExpeditionLoot.SetAutoJunk(player, setting);

        string text = setting.HasValue
            ? $"Auto-junk: {Range(setting.Value)} Expedition drops are sold on the spot for the merchant price."
            : "Auto-junk is off: every Expedition drop goes to your backpack.";

        player.Out.SendMessage(text, eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }

    private static void ShowStatus(GamePlayer player)
    {
        eLootRarity? setting = ExpeditionLoot.GetAutoJunk(player);
        string auto = setting.HasValue ? $"{Range(setting.Value)} drops are sold on the spot" : "off";

        if (setting.HasValue && !Properties.EXPEDITION_AUTO_JUNK)
        {
            auto += " (but turned off on this server)";
        }

        player.Out.SendMessage($"Auto-junk: {auto}.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

        foreach (eLootRarity threshold in new[] { eLootRarity.Common, eLootRarity.Magic, eLootRarity.Rare })
        {
            List<DbInventoryItem> junk = FindJunk(player, threshold, out long total);

            player.Out.SendMessage($"/junk {threshold.ToString().ToLowerInvariant()}: {junk.Count} item{(junk.Count == 1 ? string.Empty : "s")}, {Money.GetString(total)}.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }
}
