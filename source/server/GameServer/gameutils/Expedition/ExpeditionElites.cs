using System.Collections.Generic;

namespace DOL.GS.Expedition;

/// <summary>
/// Elite packs: a seeded camp or two per act whose monsters carry an affix (swift, venomous, shielded...) and a
/// guaranteed Rare drop. Scaffold: generation, runtime affixes and messages are filled in by the elite-pack work;
/// the hooks below are already called by the loot and journal code.
/// </summary>
public static class ExpeditionElites
{
    /// <summary>True when the monster belongs to an elite pack of an active run.</summary>
    public static bool IsElite(GameNPC npc)
    {
        return false;
    }

    /// <summary>Journal lines about the act's elite packs; empty when the act has none.</summary>
    public static IReadOnlyList<string> DescribeElites(ActPlan plan)
    {
        return new List<string>();
    }
}
