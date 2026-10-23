using DOL.Database;
using DOL.GS.Fishing;
using DOL.GS.PacketHandler;

namespace DOL.GS.Commands
{
    [CmdAttribute(
        "&fish",
        ePrivLevel.Player,
        "Relax and fish. Face open water with a fishing pole and type /fish.",
        "'/fish' - Fishes and casts again after each catch until you move or type /fish stop.",
        "'/fish active' - Like /fish, but you reel in each bite yourself: type /fish within 4 seconds.",
        "'/fish stop' - Reels in your line and stops fishing.",
        "'/fish stats' - Shows your fishing skill, casts, catches and best catch.",
        "'/fish pole' - Gives you a fishing pole if you don't have one.")]
    public class FishCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;

            if (player == null)
            {
                return;
            }

            // A bare /fish during an active-mode bite must never be swallowed by the spam guard.
            if (args.Length < 2 && FishingManager.TryReelIn(player))
            {
                return;
            }

            if (IsSpammingCommand(player, "fish"))
            {
                return;
            }

            string sub = args.Length >= 2 ? args[1].ToLowerInvariant() : string.Empty;

            switch (sub)
            {
                case "":
                {
                    FishingManager.TryStart(player, false);

                    break;
                }
                case "active":
                {
                    FishingManager.TryStart(player, true);

                    break;
                }
                case "stop":
                {
                    if (!FishingManager.IsFishing(player))
                    {
                        player.Out.SendMessage("You aren't fishing.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

                        break;
                    }

                    FishingManager.Stop(player, "You reel in your line and stop fishing.");

                    break;
                }
                case "stats":
                {
                    FishingProgress progress = FishingProgressStore.Load(player);
                    string best = "none yet";

                    if (!string.IsNullOrEmpty(progress.BestCatchName))
                    {
                        best = $"{progress.BestCatchName} ({progress.BestWeightTenths / 10}.{progress.BestWeightTenths % 10} lbs)";
                    }

                    player.Out.SendMessage($"Fishing skill: {progress.Skill} / 100", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Casts: {progress.Casts}   Catches: {progress.Catches}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"Best catch: {best}", eChatType.CT_System, eChatLoc.CL_SystemWindow);

                    break;
                }
                case "pole":
                {
                    // MVP shortcut until a merchant sells poles.
                    if (FishingManager.HasFishingPole(player))
                    {
                        player.Out.SendMessage("You already have a fishing pole.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

                        break;
                    }

                    DbItemTemplate template = GameServer.Database.FindObjectByKey<DbItemTemplate>(FishingCatalog.PoleTemplateId);

                    if (template == null)
                    {
                        player.Out.SendMessage("Fishing poles aren't available right now.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

                        break;
                    }

                    DbInventoryItem pole = GameInventoryItem.Create(template);

                    if (!player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, pole))
                    {
                        player.Out.SendMessage("You need a free backpack slot for the fishing pole.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

                        break;
                    }

                    InventoryLogging.LogInventoryAction("(fishing)", player, eInventoryActionType.Other, pole.Template, pole.Count);
                    player.Out.SendMessage("You receive a fishing pole. Face open water and type /fish to cast.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

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
}
