using System.Linq;
using DOL.GS.Expedition;
using DOL.GS.Expedition.Gauntlet;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>Hard mode's own rules (the core slice): double realm points, the arrival taunts and the rules text. No server is needed.</summary>
[TestFixture]
public class UT_ExpeditionHardCore
{
    [Test]
    public void RealmPointFactor_DoublesOnlyAHardRun()
    {
        Assert.That(ExpeditionHardMode.RealmPointFactor(null), Is.EqualTo(1.0));
        Assert.That(ExpeditionHardMode.RealmPointFactor(new ExpeditionRun { Hard = false }), Is.EqualTo(1.0));
        Assert.That(ExpeditionHardMode.RealmPointFactor(new ExpeditionRun { Hard = true }), Is.EqualTo(2.0));
    }

    [Test]
    public void RealmPointFactor_StacksWithGlory()
    {
        var run = new ExpeditionRun { Hard = true };
        run.Boons.Add(ExpeditionBoons.Glory);

        double total = ExpeditionBoons.RealmPointFactor(run) * ExpeditionHardMode.RealmPointFactor(run);

        Assert.That(total, Is.EqualTo(2.0 * (1.0 + ExpeditionBoons.GloryRealmPointPercent / 100.0)).Within(1e-9));
    }

    [Test]
    public void ArrivalTaunts_OnePerActInChampionOrder_ShortEnough()
    {
        string[] champions = { "Gwyraan", "Toothmouth", "Ios", "Drunkstains", "Preesh" };

        Assert.That(ExpeditionHardMode.ArrivalTaunts.Length, Is.EqualTo(ActGenerator.ActCount));

        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            string taunt = ExpeditionHardMode.ArrivalTaunt(act);

            Assert.That(taunt, Does.StartWith(champions[act - 1]), $"act {act}");
            Assert.That(taunt, Does.Contain(GauntletRules.GuildName));
            Assert.That(taunt.Length, Is.LessThan(100), $"act {act}");
        }

        Assert.That(ExpeditionHardMode.ArrivalTaunt(0), Is.EqualTo(ExpeditionHardMode.ArrivalTaunt(1)));
        Assert.That(ExpeditionHardMode.ArrivalTaunt(9), Is.EqualTo(ExpeditionHardMode.ArrivalTaunt(5)));
    }

    [Test]
    public void RulesLines_ListTheCoreRules()
    {
        string rules = string.Join("\n", ExpeditionHardMode.RulesLines());

        Assert.That(rules, Does.Contain("/run inherit"));
        Assert.That(rules, Does.Contain("No recovery"));
        Assert.That(rules, Does.Contain("Realm points are doubled"));
        Assert.That(ExpeditionHardMode.RulesLines().All(line => line != null), Is.True);
    }
}
