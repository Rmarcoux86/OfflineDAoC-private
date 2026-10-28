using DOL.GS.Expedition;
using DOL.GS.Expedition.Gauntlet;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>Act 5's dormant shrines (woken by kills at the camp beside them) and Preesh's taunts.</summary>
[TestFixture]
public class UT_ExpeditionDormantShrines
{
    private static ShrineSpec Dormant(int kills = 0) => new()
    {
        RegionId = 249,
        CampMob = "essence shredder",
        CampX = 30000,
        CampY = 30000,
        KillsNeeded = 5,
        Kills = kills,
        Placed = true
    };

    [Test]
    public void KillsNeeded_IsHalfTheCampFromThreeToSix()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionShrines.DormantKillsFor(2), Is.EqualTo(3));
            Assert.That(ExpeditionShrines.DormantKillsFor(8), Is.EqualTo(4));
            Assert.That(ExpeditionShrines.DormantKillsFor(15), Is.EqualTo(6));

            // Only camps that con at least yellow to a level 42 arrival (level 38 or more) host a shrine.
            Assert.That(ExpeditionShrines.IsWorthyCamp(new[] { 37 }), Is.False, "blue at 42");
            Assert.That(ExpeditionShrines.IsWorthyCamp(new[] { 18, 21, 23 }), Is.False, "grey at 42");
            Assert.That(ExpeditionShrines.IsWorthyCamp(new[] { 38 }), Is.True, "yellow at 42");
            Assert.That(ExpeditionShrines.IsWorthyCamp(new[] { 42, 45, 47 }), Is.True, "orange and red too");
            Assert.That(ExpeditionShrines.IsWorthyCamp(new[] { 35, 36, 38 }), Is.True, "its strongest is yellow");
            Assert.That(ExpeditionShrines.IsWorthyCamp(new int[0]), Is.False);
        });
    }

    [Test]
    public void AKillCounts_ForTheCampsMonsterNearTheCamp_EliteIncluded()
    {
        ShrineSpec shrine = Dormant();

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionShrines.CountsFor(shrine, "essence shredder", 249, 30500, 30500), Is.True);
            Assert.That(ExpeditionShrines.CountsFor(shrine, "Swift essence shredder", 249, 30000, 31000), Is.True, "elite");
            Assert.That(ExpeditionShrines.CountsFor(shrine, "naburite drinker", 249, 30000, 30000), Is.False, "another monster");
            Assert.That(ExpeditionShrines.CountsFor(shrine, "essence shredder", 249, 32000, 30000), Is.False, "too far from the camp");
            Assert.That(ExpeditionShrines.CountsFor(shrine, "essence shredder", 248, 30000, 30000), Is.False, "another region");
            Assert.That(ExpeditionShrines.CountsFor(Dormant(5), "essence shredder", 249, 30000, 30000), Is.False, "already awake");
        });
    }

    [Test]
    public void AShrineIsDormantUntilItHasItsKills()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionShrines.IsDormant(Dormant(4)), Is.True);
            Assert.That(ExpeditionShrines.IsDormant(Dormant(5)), Is.False);
            Assert.That(ExpeditionShrines.IsDormant(new ShrineSpec()), Is.False, "an ordinary shrine");
        });
    }

    [Test]
    public void Taunts_FillInTheMonsterAndThePlayer()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GauntletTaunts.Line(0, "essence shredder", "Ryan"), Is.EqualTo("Oh, you think you're a big man killing that essence shredder? Just wait till you find me."));
            Assert.That(GauntletTaunts.Line(3, "x", "Ryan"), Does.Contain("Ryan"));
            Assert.That(GauntletTaunts.Line(GauntletTaunts.Lines.Length, null, null), Is.EqualTo(GauntletTaunts.Line(0, null, null)));
        });

        for (int i = 0; i < GauntletTaunts.Lines.Length; i++)
        {
            Assert.That(() => GauntletTaunts.Line(i, "m", "p"), Throws.Nothing, $"line {i}");
        }
    }

    [Test]
    public void Taunts_ComeFromAShuffledBagOfEveryLine()
    {
        int[] order = GauntletTaunts.Shuffled(new System.Random(7));

        Assert.That(order, Is.EquivalentTo(System.Linq.Enumerable.Range(0, GauntletTaunts.Lines.Length)));
    }

    [Test]
    public void UniqueJewelry_UsesTheArtifactJewelryModels()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionUniques.ModelFor(eUniqueSlot.Necklace), Is.EqualTo(1887));
            Assert.That(ExpeditionUniques.ModelFor(eUniqueSlot.Jewel), Is.EqualTo(1886));
            Assert.That(ExpeditionUniques.ModelFor(eUniqueSlot.Belt), Is.EqualTo(1884));
            Assert.That(ExpeditionUniques.ModelFor(eUniqueSlot.Ring), Is.EqualTo(1888));
            Assert.That(ExpeditionUniques.ModelFor(eUniqueSlot.Bracer), Is.EqualTo(1885));
        });
    }
}
