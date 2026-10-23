using System.Collections.Generic;

namespace DOL.GS.Expedition;

/// <summary>
/// Run boons: build-defining picks offered one-of-three at each act break. Scaffold: the boon catalog, offers,
/// choice and effects are filled in by the boons work; the hooks below are already called by the loot and journal code.
/// </summary>
public static class ExpeditionBoons
{
    /// <summary>Journal lines about the run's boons and a pending offer; empty when the run has none.</summary>
    public static IReadOnlyList<string> DescribeBoons(ExpeditionRun run)
    {
        return new List<string>();
    }

    /// <summary>Lets loot boons raise the rarity floor of a drop event.</summary>
    public static void AdjustLootFloor(GamePlayer player, ExpeditionRun run, ref eLootRarity? minimum)
    {
    }

    /// <summary>Lets loot boons change the per-kill drop chance (percent).</summary>
    public static int AdjustLootChance(GamePlayer player, ExpeditionRun run, int chance)
    {
        return chance;
    }
}
