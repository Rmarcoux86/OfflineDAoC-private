using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.GS.PacketHandler;

namespace DOL.GS.Expedition;

/// <summary>
/// The unique weapon exchange (owner, 2026-10-24): an NPC in each act that turns a unique weapon into another weapon type
/// the player can use, keeping its name, bonuses, proc and item level, for a fee by tier. It stands in the act zone's
/// town (the tightest cluster of merchants in the zone) when the zone has one, else beside the act camp. The spot is
/// found once per act, in the world, and saved in the plan; the NPC exists only while the player's tracker is attached.
/// </summary>
public static class ExpeditionExchanger
{
    /// <summary>A zone counts as having a town when one merchant has at least this many others within <see cref="TownRadius"/>.</summary>
    public const int TownMinOtherMerchants = 2;
    public const int TownRadius = 2000;

    private const int OffsetFromAnchor = 200;
    private const ushort FallbackModel = 408;
    private const string PendingKey = "EXPEDITION_EXCHANGE_ITEM";

    private static readonly ConcurrentDictionary<string, ExchangerNpc> Spawned = new(StringComparer.Ordinal);

    /// <summary>A Hastener beside each act camp (owner, 2026-10-24: getting around without a speed class).</summary>
    private static readonly ConcurrentDictionary<string, GameHastener> Hasteners = new(StringComparer.Ordinal);

    /// <summary>
    /// A Supplier beside each act camp (owner, 2026-10-25): the realm's poisons, arrows, bolts and throwing weapons, so a
    /// run never has to go back to town for them.
    /// </summary>
    private static readonly ConcurrentDictionary<string, GameMerchant> Suppliers = new(StringComparer.Ordinal);

    /// <summary>The Supplier's stock per realm, built once from the items the world's merchants sell.</summary>
    private static readonly ConcurrentDictionary<eRealm, MerchantTradeItems> SupplyStock = new();

    /// <summary>The gold an exchange costs, by the unique's tier.</summary>
    public static int FeeGold(eUniqueTier tier)
    {
        return tier switch
        {
            eUniqueTier.Early => 2,
            eUniqueTier.Mid => 15,
            _ => 75
        };
    }

    /// <summary>The melee weapon types of a realm, with the hand each is made for (one- and two-handed for Midgard's).</summary>
    public static IReadOnlyList<(eObjectType Type, eInventorySlot Hand, string Label)> RealmWeapons(eRealm realm)
    {
        eInventorySlot one = (eInventorySlot) Slot.RIGHTHAND;
        eInventorySlot two = (eInventorySlot) Slot.TWOHAND;

        return realm switch
        {
            eRealm.Hibernia =>
            [
                (eObjectType.Blades, one, "Blades"), (eObjectType.Blunt, one, "Blunt"), (eObjectType.Piercing, one, "Piercing"),
                (eObjectType.LargeWeapons, two, "Large Weapons"), (eObjectType.CelticSpear, two, "Celtic Spear"),
                (eObjectType.Scythe, two, "Scythe"), (eObjectType.Staff, two, "Staff")
            ],
            eRealm.Midgard =>
            [
                (eObjectType.Sword, one, "Sword"), (eObjectType.Sword, two, "Two-Handed Sword"), (eObjectType.Axe, one, "Axe"),
                (eObjectType.Axe, two, "Two-Handed Axe"), (eObjectType.Hammer, one, "Hammer"), (eObjectType.Hammer, two, "Two-Handed Hammer"),
                (eObjectType.Spear, two, "Spear"), (eObjectType.HandToHand, one, "Hand to Hand"), (eObjectType.Staff, two, "Staff")
            ],
            _ =>
            [
                (eObjectType.SlashingWeapon, one, "Slash"), (eObjectType.CrushingWeapon, one, "Crush"), (eObjectType.ThrustWeapon, one, "Thrust"),
                (eObjectType.TwoHandedWeapon, two, "Two-Handed"), (eObjectType.PolearmWeapon, two, "Polearm"),
                (eObjectType.Flexible, one, "Flexible"), (eObjectType.Staff, two, "Staff")
            ]
        };
    }

    /// <summary>The weapon types this player can use (the server's own check), leaving out the item's current type and hand.</summary>
    public static List<(eObjectType Type, eInventorySlot Hand, string Label)> OptionsFor(GamePlayer player, DbInventoryItem current)
    {
        return RealmWeapons(player.Realm)
            .Where(option => !(option.Type == (eObjectType) current.Object_Type && (int) option.Hand == current.Item_Type))
            .Where(option => player.HasAbilityToUseItem(new DbItemTemplate { Object_Type = (int) option.Type, Item_Type = (int) option.Hand, Realm = (int) player.Realm }))
            .ToList();
    }

    /// <summary>The unique a weapon item is, or null when it is not an Expedition unique weapon.</summary>
    public static UniqueDefinition UniqueWeaponOf(DbInventoryItem item)
    {
        if (item == null || !ExpeditionLoot.TryGetRarity(item, out eLootRarity rarity) || rarity != eLootRarity.Unique)
        {
            return null;
        }

        UniqueDefinition definition = ExpeditionUniques.FindByName(item.Name);

        return definition?.Slot == eUniqueSlot.Weapon ? definition : null;
    }

    #region Placement

    /// <summary>
    /// The exchanger's spot for the act: beside the zone town's busiest merchant of the player's realm, else beside the
    /// camp. Found once, in the world, and saved in the plan. Returns the merchant whose look the exchanger copies (null
    /// at the camp). Darkness Falls always uses the camp (owner 2026-10-25: act 5 had no exchanger to be found; the
    /// busiest merchants there are the realms' own halls deep in the dungeon).
    /// </summary>
    private static GameMerchant Place(ActPlan plan, eRealm realm)
    {
        Region region = WorldMgr.GetRegion((ushort) plan.RegionId);
        bool campOnly = plan.ZoneId == ActGenerator.DarknessFallsZoneId;
        List<GameMerchant> merchants = campOnly
            ? new List<GameMerchant>()
            : region?.Merchants?
                .Where(m => m != null && m.ObjectState == GameObject.eObjectState.Active && m.CurrentZone?.ID == plan.ZoneId &&
                    (m.Realm == realm || m.Realm == eRealm.None))
                .ToList() ?? new List<GameMerchant>();

        // A Darkness Falls act placed under the old rule (in a merchant hall) is placed again, beside the camp.
        if (campOnly && plan.ExchangerPlaced && plan.ExchangerInTown)
        {
            plan.ExchangerPlaced = false;
        }

        GameMerchant town = null;
        int best = -1;

        foreach (GameMerchant merchant in merchants)
        {
            int others = merchants.Count(m => m != merchant && m.IsWithinRadius(merchant, TownRadius));

            if (others > best)
            {
                best = others;
                town = merchant;
            }
        }

        if (!plan.ExchangerPlaced)
        {
            if (town != null && best >= TownMinOtherMerchants)
            {
                plan.ExchangerInTown = true;
                plan.ExchangerX = town.X + OffsetFromAnchor;
                plan.ExchangerY = town.Y;
                plan.ExchangerZ = town.Z;
            }
            else
            {
                plan.ExchangerInTown = false;
                plan.ExchangerX = plan.CampX + OffsetFromAnchor;
                plan.ExchangerY = plan.CampY;
                plan.ExchangerZ = plan.CampZ;
            }

            plan.ExchangerPlaced = true;
        }

        return plan.ExchangerInTown ? town : merchants.FirstOrDefault();
    }

    /// <summary>Called by the objective tracker's tick while the player is in the world: places and spawns the exchanger.</summary>
    public static void Tick(GamePlayer player, ExpeditionRun run, ActPlan plan)
    {
        if (plan == null || player.CurrentRegionID != plan.RegionId)
        {
            return;
        }

        SpawnHastener(player, plan);
        SpawnSupplier(player, plan);

        if (Spawned.TryGetValue(player.ObjectId, out ExchangerNpc existing) && existing.ObjectState == GameObject.eObjectState.Active)
        {
            return;
        }

        bool wasPlaced = plan.ExchangerPlaced && !(plan.ZoneId == ActGenerator.DarknessFallsZoneId && plan.ExchangerInTown);
        GameMerchant look = Place(plan, player.Realm);

        if (!wasPlaced)
        {
            ExpeditionManager.Save(run);
        }

        Region region = WorldMgr.GetRegion((ushort) plan.RegionId);

        if (region == null)
        {
            return;
        }

        ExchangerNpc npc = new()
        {
            CurrentRegion = region,
            X = plan.ExchangerX,
            Y = plan.ExchangerY,
            Z = plan.ExchangerZ,
            Heading = 0,
            Model = look?.Model ?? FallbackModel,
            Size = look?.Size ?? (byte) 50,
            Level = 50,
            Realm = player.Realm,
            Name = "Reforger",
            GuildName = "Unique Exchange",
            Flags = GameNPC.eFlags.PEACE
        };

        if (npc.AddToWorld())
        {
            Spawned[player.ObjectId] = npc;
        }
    }

    /// <summary>A Hastener a little to the other side of the camp from the camp exchanger; it gives speed when spoken to.</summary>
    private static void SpawnHastener(GamePlayer player, ActPlan plan)
    {
        if (Hasteners.TryGetValue(player.ObjectId, out GameHastener existing) && existing.ObjectState == GameObject.eObjectState.Active)
        {
            return;
        }

        Region region = WorldMgr.GetRegion((ushort) plan.RegionId);

        if (region == null)
        {
            return;
        }

        GameMerchant look = region.Merchants?.FirstOrDefault(m => m != null && m.ObjectState == GameObject.eObjectState.Active);
        GameHastener hastener = new()
        {
            CurrentRegion = region,
            X = plan.CampX - OffsetFromAnchor,
            Y = plan.CampY,
            Z = plan.CampZ,
            Heading = 0,
            Model = look?.Model ?? FallbackModel,
            Size = look?.Size ?? (byte) 50,
            Level = 50,
            Realm = player.Realm,
            Name = "Hastener",
            GuildName = "Expedition Camp",
            Flags = GameNPC.eFlags.PEACE
        };

        if (hastener.AddToWorld())
        {
            Hasteners[player.ObjectId] = hastener;
        }
    }

    /// <summary>A Supplier on the far side of the camp from the Hastener; it sells the realm's poisons and ammunition.</summary>
    private static void SpawnSupplier(GamePlayer player, ActPlan plan)
    {
        if (Suppliers.TryGetValue(player.ObjectId, out GameMerchant existing) && existing.ObjectState == GameObject.eObjectState.Active)
        {
            return;
        }

        Region region = WorldMgr.GetRegion((ushort) plan.RegionId);
        MerchantTradeItems stock = SupplyStockFor(player.Realm);

        if (region == null || stock == null)
        {
            return;
        }

        GameMerchant look = region.Merchants?.FirstOrDefault(m => m != null && m.ObjectState == GameObject.eObjectState.Active);
        GameMerchant supplier = new()
        {
            CurrentRegion = region,
            X = plan.CampX,
            Y = plan.CampY + OffsetFromAnchor,
            Z = plan.CampZ,
            Heading = 0,
            Model = look?.Model ?? FallbackModel,
            Size = look?.Size ?? (byte) 50,
            Level = 50,
            Realm = player.Realm,
            Name = "Supplier",
            GuildName = "Poisons and Ammunition",
            Flags = GameNPC.eFlags.PEACE,
            TradeItems = stock
        };

        if (supplier.AddToWorld())
        {
            Suppliers[player.ObjectId] = supplier;
        }
    }

    /// <summary>
    /// The realm's poisons (page 1), arrows and bolts (page 2) and throwing weapons (page 3): every item of those kinds
    /// that some merchant in the world sells, for this realm or any realm, cheapest level first. A kind the realm has
    /// none of is left out. Null when nothing is found.
    /// </summary>
    private static MerchantTradeItems SupplyStockFor(eRealm realm)
    {
        if (SupplyStock.TryGetValue(realm, out MerchantTradeItems cached))
        {
            return cached;
        }

        MerchantTradeItems stock = null;

        try
        {
            HashSet<string> sold = new(GameServer.Database.SelectAllObjects<DbMerchantItem>().Select(m => m.ItemTemplateID), StringComparer.OrdinalIgnoreCase);
            eObjectType[][] pages =
            [
                [eObjectType.Poison],
                [eObjectType.Arrow, eObjectType.Bolt],
                [eObjectType.Thrown]
            ];
            int page = 0;

            foreach (eObjectType[] kinds in pages)
            {
                List<DbItemTemplate> items = [];

                foreach (eObjectType kind in kinds)
                {
                    items.AddRange(GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Object_Type").IsEqualTo((int) kind))
                        .Where(t => sold.Contains(t.Id_nb) && (t.Realm == 0 || t.Realm == (int) realm)));
                }

                List<DbItemTemplate> ordered = items
                    .GroupBy(t => t.Id_nb, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(t => t.Object_Type)
                    .ThenBy(t => t.Level)
                    .ThenBy(t => t.Name, StringComparer.Ordinal)
                    .Take(MerchantTradeItems.MAX_ITEM_IN_TRADEWINDOWS)
                    .ToList();

                if (ordered.Count == 0)
                {
                    continue;
                }

                stock ??= new MerchantTradeItems(null);

                for (int slot = 0; slot < ordered.Count; slot++)
                {
                    stock.AddTradeItem(page, (eMerchantWindowSlot) slot, ordered[slot]);
                }

                page++;
            }
        }
        catch (Exception)
        {
            stock = null;
        }

        if (stock != null)
        {
            SupplyStock[realm] = stock;
        }

        return stock;
    }

    /// <summary>Takes the player's exchanger out of the world (the tracker detached).</summary>
    public static void Despawn(GamePlayer player)
    {
        if (player != null && Hasteners.TryRemove(player.ObjectId ?? string.Empty, out GameHastener hastener))
        {
            try
            {
                hastener.Delete();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        if (player != null && Suppliers.TryRemove(player.ObjectId ?? string.Empty, out GameMerchant supplier))
        {
            try
            {
                supplier.Delete();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        if (player == null || !Spawned.TryRemove(player.ObjectId ?? string.Empty, out ExchangerNpc npc))
        {
            return;
        }

        try
        {
            npc.Delete();
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    /// <summary>The journal line: where the exchanger stands, once placed.</summary>
    public static string Describe(ActPlan plan)
    {
        if (plan == null || !plan.ExchangerPlaced)
        {
            return null;
        }

        string loc = ActGenerator.DescribeLoc(plan.RegionId, plan.ExchangerX, plan.ExchangerY);
        string where = plan.ExchangerInTown ? "in the zone's town" : "beside the camp";

        return $"Reforger (unique weapon exchange): {where}{(loc == null ? string.Empty : $" ({loc})")}";
    }

    #endregion

    #region The exchange

    private sealed class ExchangerNpc : GameNPC
    {
        public override bool Interact(GamePlayer player)
        {
            if (!base.Interact(player))
            {
                return false;
            }

            TurnTo(player, 10000);
            SayTo(player, eChatLoc.CL_PopupWindow, "Bring me a unique weapon you cannot make good use of, and I will reforge it as another weapon you can wield. Its name, its magic and its strength stay as they are. Hand it to me to see what I can make of it.");

            return true;
        }

        public override bool ReceiveItem(GameLiving source, DbInventoryItem item)
        {
            if (source is not GamePlayer player || item == null)
            {
                return false;
            }

            if (!IsWithinRadius(player, WorldMgr.INTERACT_DISTANCE))
            {
                player.Out.SendMessage($"You are too far away to give anything to {GetName(0, false)}.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

                return false;
            }

            UniqueDefinition unique = UniqueWeaponOf(item);

            if (unique == null)
            {
                SayTo(player, eChatLoc.CL_PopupWindow, "I only reforge unique weapons.");

                return false;
            }

            List<(eObjectType Type, eInventorySlot Hand, string Label)> options = OptionsFor(player, item);

            if (options.Count == 0)
            {
                SayTo(player, eChatLoc.CL_PopupWindow, "There is no other weapon you could wield that I could make of it.");

                return false;
            }

            player.TempProperties.SetProperty(PendingKey, item);
            string choices = string.Join(", ", options.Select(option => $"[{option.Label}]"));
            SayTo(player, eChatLoc.CL_PopupWindow, $"{item.Name}, a fine piece. For {FeeGold(unique.Tier)} gold I can make it into: {choices}.");

            return true;
        }

        public override bool WhisperReceive(GameLiving source, string text)
        {
            if (!base.WhisperReceive(source, text) || source is not GamePlayer player)
            {
                return false;
            }

            DbInventoryItem item = player.TempProperties.GetProperty<DbInventoryItem>(PendingKey, null);

            if (item == null)
            {
                return false;
            }

            (eObjectType Type, eInventorySlot Hand, string Label) choice = OptionsFor(player, item)
                .FirstOrDefault(option => string.Equals(option.Label, text, StringComparison.OrdinalIgnoreCase));

            if (choice.Label == null)
            {
                return false;
            }

            TurnTo(player, 10000);
            Exchange(this, player, item, choice);

            return true;
        }
    }

    private static void Exchange(GameNPC npc, GamePlayer player, DbInventoryItem item, (eObjectType Type, eInventorySlot Hand, string Label) choice)
    {
        UniqueDefinition unique = UniqueWeaponOf(item);

        // The item must still be in the backpack (not traded, sold, dropped or equipped since it was handed over).
        if (unique == null || item.SlotPosition < (int) eInventorySlot.FirstBackpack || item.SlotPosition > (int) eInventorySlot.LastBackpack
            || player.Inventory.GetItem((eInventorySlot) item.SlotPosition) != item)
        {
            npc.SayTo(player, eChatLoc.CL_PopupWindow, "Keep the weapon in your pack, not in your hands, and hand it to me again.");
            player.TempProperties.RemoveProperty(PendingKey);

            return;
        }

        long fee = Money.GetMoney(0, 0, FeeGold(unique.Tier), 0, 0);

        if (player.GetCurrentMoney() < fee)
        {
            npc.SayTo(player, eChatLoc.CL_PopupWindow, $"That will be {Money.GetString(fee)}, and you have less than that.");

            return;
        }

        GeneratedUniqueItem reforged = ExpeditionUniques.Build(player, unique, (choice.Type, choice.Hand), (byte) Math.Max(1, item.Level));
        DbInventoryItem replacement = GameInventoryItem.Create(reforged);
        replacement.IsROG = true;
        replacement.Creator = item.Creator;
        eInventorySlot slot = (eInventorySlot) item.SlotPosition;

        if (!player.Inventory.RemoveItem(item))
        {
            npc.SayTo(player, eChatLoc.CL_PopupWindow, "Something is wrong with that weapon; I cannot take it.");

            return;
        }

        if (!player.Inventory.AddItem(slot, replacement))
        {
            // Should not happen (the slot was just freed); give the original back.
            player.Inventory.AddItem(slot, item);
            npc.SayTo(player, eChatLoc.CL_PopupWindow, "Your pack is in a muddle; I could not hand it back reforged.");

            return;
        }

        player.RemoveMoney(fee);
        player.Out.SendUpdateMoney();
        InventoryLogging.LogInventoryAction(player, "(expedition reforge)", eInventoryActionType.Merchant, fee);
        player.TempProperties.RemoveProperty(PendingKey);
        npc.SayTo(player, eChatLoc.CL_PopupWindow, $"There: {replacement.Name}, now a {choice.Label} weapon. Use it well.");
        player.Out.SendMessage($"Your {replacement.Name} is reforged as a {choice.Label} weapon for {Money.GetString(fee)}.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        ExpeditionManager.GetRun(player)?.AddLog($"Act {ExpeditionManager.GetRun(player)?.Act}: reforged {replacement.Name} as a {choice.Label} weapon.");
    }

    #endregion
}
