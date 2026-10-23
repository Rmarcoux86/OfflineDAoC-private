using DOL.Database;

namespace DOL.GS.Fishing;

/// <summary>
/// Monsters leave anglers alone: a real player standing with the fishing pole in hand is not picked up by
/// proximity aggro. Called from StandardMobBrain.CanAggroTarget and the guard brains' proximity scans, so it runs
/// for every mob and every nearby living on each aggro check and must stay cheap.
/// It never removes anyone from an aggro list: a mob the angler attacks, or one already fighting them, fights as usual.
/// </summary>
public static class FishingAggro
{
    public static bool IsPeacefulAngler(GameLiving living)
    {
        // Companion bots and autonomous gamebots are GameBot, a GameNPC and never a GamePlayer, so only real players pass.
        if (!ServerProperties.Properties.FISHING_POLE_PREVENTS_AGGRO || living is not GamePlayer player)
        {
            return false;
        }

        if (player.ActiveWeaponSlot != eActiveWeaponSlot.TwoHanded)
        {
            return false;
        }

        // Swinging the pole at a mob, or being in a fight at all, makes the angler a fair target again.
        if (player.InCombat || player.attackComponent?.AttackState == true)
        {
            return false;
        }

        DbInventoryItem pole = player.Inventory?.GetItem(eInventorySlot.TwoHandWeapon);

        return pole?.Template?.Id_nb == FishingCatalog.PoleTemplateId;
    }
}
