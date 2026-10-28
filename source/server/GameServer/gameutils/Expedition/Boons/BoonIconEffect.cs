using System.Collections.Generic;

namespace DOL.GS.Expedition;

/// <summary>
/// A boon's icon in the player's buff bar (owner 2026-10-27): permanent (no timer), named after the boon, and its
/// effect text as delve info (DetailDisplayHandler). Display only: the boon's effects are applied by
/// <see cref="BoonEffects"/>. Death and a player cancel end it; BoonEffects puts it back while the boon lasts.
/// </summary>
public sealed class BoonIconEffect : ECSGameAbilityEffect
{
    /// <summary>The buff bar icon per boon, taken from spells of the same feel in the spell table.</summary>
    private static readonly Dictionary<string, ushort> Icons = new()
    {
        [ExpeditionBoons.Fortune] = 8093,
        [ExpeditionBoons.Ironhide] = 533,
        [ExpeditionBoons.Bloodlust] = 3607,
        [ExpeditionBoons.Thornmail] = 57,
        [ExpeditionBoons.Vampiric] = 662,
        [ExpeditionBoons.Rejuvenation] = 8077,
        [ExpeditionBoons.ArcaneFury] = 1537,
        [ExpeditionBoons.Executioner] = 11468,
        [ExpeditionBoons.PhoenixFeather] = 4981,
        [ExpeditionBoons.SoulHarvest] = 651,
        [ExpeditionBoons.Desperation] = 10540,
        [ExpeditionBoons.GlassCannon] = 1568,
        [ExpeditionBoons.Siphon] = 8084,
        [ExpeditionBoons.BountyHunter] = 11463,
        [ExpeditionBoons.Glory] = 2882,
        [ExpeditionBoons.BlessedArms] = 10046,
        [ExpeditionBoons.MarkedPrey] = 2914,
        [ExpeditionBoons.Evasion] = 4580,
        [ExpeditionBoons.TreasureSense] = 8081,
        [ExpeditionBoons.Swiftfoot] = 2922,
        [ExpeditionBoons.SecondSkin] = 11552,
        [ExpeditionBoons.HuntersInstinct] = 3590,
        [ExpeditionBoons.Berserker] = 861,
        [ExpeditionBoons.BeastBond] = 10041
    };

    private const ushort DefaultIcon = 2882;

    public BoonDefinition Boon { get; }

    public BoonIconEffect(in ECSGameEffectInitParams initParams, BoonDefinition boon) : base(initParams)
    {
        EffectType = eEffect.ExpeditionBoon;
        Boon = boon;
    }

    public override ushort Icon => IconFor(Boon.Id);
    public override string Name => Boon.Name;
    public override bool HasPositiveEffect => true;

    public static ushort IconFor(string id)
    {
        return id != null && Icons.TryGetValue(id, out ushort icon) ? icon : DefaultIcon;
    }

    public override long GetRemainingTimeForClient()
    {
        return 0;
    }

    public override void OnStartEffect() { }

    public override void OnStopEffect() { }

    /// <summary>The delve window's lines.</summary>
    public IList<string> DelveInfo => new List<string>
    {
        Boon.Effect,
        " ",
        "Expedition boon: it lasts for the rest of the run."
    };
}
