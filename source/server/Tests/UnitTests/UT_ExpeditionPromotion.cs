using System.Collections.Generic;
using System.Linq;
using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>Expedition class promotion's pure logic: candidate classes from the class scripts, the dialog order and the saved choice. No server is needed.</summary>
[TestFixture]
public class UT_ExpeditionPromotion
{
    private static int[] Ids(params eCharacterClass[] classes)
    {
        return classes.Select(characterClass => (int)characterClass).ToArray();
    }

    private static int[] Candidates(eCharacterClass baseClass, eRealm realm, eRace? race = null, bool sluaghbinder = true)
    {
        return ExpeditionPromotion.FilterCandidates(ExpeditionPromotion.AllPaths(), (int)baseClass, realm, race, null, sluaghbinder)
            .Select(path => path.ClassId)
            .ToArray();
    }

    [Test]
    public void AllPaths_ReadsAdvancedClassesAndTheirBaseFromTheScripts()
    {
        IReadOnlyList<ExpeditionPromotion.PromotionPath> paths = ExpeditionPromotion.AllPaths();

        Assert.That(paths.Count, Is.GreaterThanOrEqualTo(40));
        Assert.That(paths.Select(path => path.ClassId), Is.Unique);
        Assert.That(paths.All(path => !string.IsNullOrWhiteSpace(path.Name)), Is.True);
        Assert.That(paths.All(path => path.BaseClassId != path.ClassId), Is.True);

        Dictionary<int, int> baseOf = paths.ToDictionary(path => path.ClassId, path => path.BaseClassId);

        Assert.That(baseOf[(int)eCharacterClass.Warden], Is.EqualTo((int)eCharacterClass.Naturalist));
        Assert.That(baseOf[(int)eCharacterClass.Hunter], Is.EqualTo((int)eCharacterClass.MidgardRogue));
        Assert.That(baseOf[(int)eCharacterClass.Scout], Is.EqualTo((int)eCharacterClass.AlbionRogue));
        Assert.That(baseOf[(int)eCharacterClass.Necromancer], Is.EqualTo((int)eCharacterClass.Disciple));
        Assert.That(baseOf[(int)eCharacterClass.Sluaghbinder], Is.EqualTo((int)eCharacterClass.Acolyte));
        Assert.That(baseOf.ContainsKey((int)eCharacterClass.Naturalist), Is.False, "a base class is not a promotion path");
    }

    [Test]
    public void Candidates_Albion()
    {
        Assert.That(Candidates(eCharacterClass.Fighter, eRealm.Albion), Is.EqualTo(Ids(eCharacterClass.Paladin, eCharacterClass.Armsman, eCharacterClass.Mercenary, eCharacterClass.Reaver)));
        Assert.That(Candidates(eCharacterClass.Acolyte, eRealm.Albion), Is.EqualTo(Ids(eCharacterClass.Cleric, eCharacterClass.Friar)));
        Assert.That(Candidates(eCharacterClass.AlbionRogue, eRealm.Albion), Is.EqualTo(Ids(eCharacterClass.Scout, eCharacterClass.Minstrel, eCharacterClass.Infiltrator)));
        Assert.That(Candidates(eCharacterClass.Mage, eRealm.Albion), Is.EqualTo(Ids(eCharacterClass.Sorcerer, eCharacterClass.Cabalist)));
        Assert.That(Candidates(eCharacterClass.Elementalist, eRealm.Albion), Is.EqualTo(Ids(eCharacterClass.Theurgist, eCharacterClass.Wizard)));
        Assert.That(Candidates(eCharacterClass.Disciple, eRealm.Albion), Is.EqualTo(Ids(eCharacterClass.Necromancer)));
    }

    [Test]
    public void Candidates_Midgard()
    {
        Assert.That(Candidates(eCharacterClass.Viking, eRealm.Midgard), Is.EqualTo(Ids(eCharacterClass.Thane, eCharacterClass.Warrior, eCharacterClass.Skald, eCharacterClass.Berserker, eCharacterClass.Savage)));
        Assert.That(Candidates(eCharacterClass.Mystic, eRealm.Midgard), Is.EqualTo(Ids(eCharacterClass.Spiritmaster, eCharacterClass.Runemaster, eCharacterClass.Bonedancer)));
        Assert.That(Candidates(eCharacterClass.Seer, eRealm.Midgard), Is.EqualTo(Ids(eCharacterClass.Healer, eCharacterClass.Shaman)));
        Assert.That(Candidates(eCharacterClass.MidgardRogue, eRealm.Midgard), Is.EqualTo(Ids(eCharacterClass.Shadowblade, eCharacterClass.Hunter)));
    }

    [Test]
    public void Candidates_Hibernia()
    {
        Assert.That(Candidates(eCharacterClass.Guardian, eRealm.Hibernia), Is.EqualTo(Ids(eCharacterClass.Blademaster, eCharacterClass.Hero, eCharacterClass.Champion)));
        Assert.That(Candidates(eCharacterClass.Naturalist, eRealm.Hibernia), Is.EqualTo(Ids(eCharacterClass.Warden, eCharacterClass.Druid, eCharacterClass.Bard)));
        Assert.That(Candidates(eCharacterClass.Stalker, eRealm.Hibernia), Is.EqualTo(Ids(eCharacterClass.Nightshade, eCharacterClass.Ranger)));
        Assert.That(Candidates(eCharacterClass.Magician, eRealm.Hibernia), Is.EqualTo(Ids(eCharacterClass.Eldritch, eCharacterClass.Enchanter, eCharacterClass.Mentalist)));
        Assert.That(Candidates(eCharacterClass.Forester, eRealm.Hibernia), Is.EqualTo(Ids(eCharacterClass.Animist, eCharacterClass.Valewalker)));
    }

    [Test]
    public void Candidates_LeaveOutPost165ClassesAndOtherRealms()
    {
        int[] post165 = Ids(eCharacterClass.Heretic, eCharacterClass.MaulerAlb, eCharacterClass.MaulerMid, eCharacterClass.MaulerHib, eCharacterClass.Valkyrie, eCharacterClass.Warlock, eCharacterClass.Bainshee, eCharacterClass.Vampiir);

        foreach (eRealm realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
        {
            foreach (int baseClass in ExpeditionPromotion.AllPaths().Select(path => path.BaseClassId).Distinct())
            {
                int[] candidates = Candidates((eCharacterClass)baseClass, realm);

                Assert.That(candidates.Intersect(post165), Is.Empty, $"{realm} {(eCharacterClass)baseClass}");
                Assert.That(candidates.All(id => GlobalConstants.STARTING_CLASSES_DICT[realm].Contains((eCharacterClass)id)), Is.True, $"{realm} {(eCharacterClass)baseClass}");
            }
        }

        Assert.That(Candidates(eCharacterClass.Guardian, eRealm.Albion), Is.Empty);
        Assert.That(Candidates(eCharacterClass.Fighter, eRealm.Hibernia), Is.Empty);
        Assert.That(Candidates(eCharacterClass.Viking, eRealm.None), Is.Empty);
    }

    [Test]
    public void Candidates_Sluaghbinder_OnlyHiberniaAndOnlyWhenEnabled()
    {
        Assert.That(Candidates(eCharacterClass.Acolyte, eRealm.Hibernia, sluaghbinder: true), Is.EqualTo(Ids(eCharacterClass.Sluaghbinder)));
        Assert.That(Candidates(eCharacterClass.Acolyte, eRealm.Hibernia, sluaghbinder: false), Is.Empty);
        Assert.That(Candidates(eCharacterClass.Acolyte, eRealm.Albion, sluaghbinder: true), Does.Not.Contain((int)eCharacterClass.Sluaghbinder));
    }

    [Test]
    public void Candidates_FollowTheClassRaceTable()
    {
        Assert.That(Candidates(eCharacterClass.Guardian, eRealm.Hibernia, eRace.Lurikeen), Is.EqualTo(Ids(eCharacterClass.Hero, eCharacterClass.Champion)));
        Assert.That(Candidates(eCharacterClass.Acolyte, eRealm.Albion, eRace.Briton), Is.EqualTo(Ids(eCharacterClass.Cleric, eCharacterClass.Friar)));
        Assert.That(Candidates(eCharacterClass.Acolyte, eRealm.Albion, eRace.Highlander), Is.EqualTo(Ids(eCharacterClass.Cleric)));
        Assert.That(Candidates(eCharacterClass.Acolyte, eRealm.Hibernia, eRace.Elf), Is.Empty);
        Assert.That(Candidates(eCharacterClass.Acolyte, eRealm.Hibernia, eRace.Celt), Is.EqualTo(Ids(eCharacterClass.Sluaghbinder)));
    }

    [Test]
    public void FilterCandidates_OrdersByClassIdAndHandlesNoInput()
    {
        List<ExpeditionPromotion.PromotionPath> paths = new()
        {
            new((int)eCharacterClass.Bard, "Bard", (int)eCharacterClass.Naturalist, new[] { eRace.Celt }),
            new((int)eCharacterClass.Warden, "Warden", (int)eCharacterClass.Naturalist, new[] { eRace.Celt }),
            new((int)eCharacterClass.Druid, "Druid", (int)eCharacterClass.Naturalist, new[] { eRace.Firbolg })
        };

        int[] all = ExpeditionPromotion.FilterCandidates(paths, (int)eCharacterClass.Naturalist, eRealm.Hibernia, null, null, true).Select(path => path.ClassId).ToArray();
        int[] celt = ExpeditionPromotion.FilterCandidates(paths, (int)eCharacterClass.Naturalist, eRealm.Hibernia, eRace.Celt, eGender.Female, true).Select(path => path.ClassId).ToArray();

        Assert.That(all, Is.EqualTo(Ids(eCharacterClass.Warden, eCharacterClass.Druid, eCharacterClass.Bard)));
        Assert.That(celt, Is.EqualTo(Ids(eCharacterClass.Warden, eCharacterClass.Bard)));
        Assert.That(ExpeditionPromotion.FilterCandidates(null, (int)eCharacterClass.Naturalist, eRealm.Hibernia, null, null, true), Is.Empty);
    }

    [Test]
    public void NextIndex_WalksTheChainAndLoopsBack()
    {
        Assert.That(ExpeditionPromotion.NextIndex(0, 3), Is.EqualTo(1));
        Assert.That(ExpeditionPromotion.NextIndex(1, 3), Is.EqualTo(2));
        Assert.That(ExpeditionPromotion.NextIndex(2, 3), Is.EqualTo(0));
        Assert.That(ExpeditionPromotion.NextIndex(0, 1), Is.EqualTo(0));
        Assert.That(ExpeditionPromotion.NextIndex(5, 0), Is.EqualTo(0));

        int index = 0;
        List<int> visited = new();

        for (int step = 0; step < 6; step++)
        {
            visited.Add(index);
            index = ExpeditionPromotion.NextIndex(index, 3);
        }

        Assert.That(visited, Is.EqualTo(new[] { 0, 1, 2, 0, 1, 2 }));
    }

    [Test]
    public void Choice_RoundTripsEveryPath()
    {
        foreach (ExpeditionPromotion.PromotionPath path in ExpeditionPromotion.AllPaths())
        {
            string saved = ExpeditionPromotion.FormatChoice(path.ClassId);

            Assert.That(ExpeditionPromotion.TryParseChoice(saved, out int classId), Is.True, saved);
            Assert.That(classId, Is.EqualTo(path.ClassId));
        }

        Assert.That(ExpeditionPromotion.FormatChoice((int)eCharacterClass.Warden), Is.EqualTo("46"));
        Assert.That(ExpeditionPromotion.TryParseChoice(" 46 ", out int trimmed), Is.True);
        Assert.That(trimmed, Is.EqualTo(46));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("Warden")]
    [TestCase("0")]
    [TestCase("-46")]
    [TestCase("+46")]
    [TestCase("256")]
    [TestCase("4.6")]
    public void Choice_RejectsAnythingElse(string value)
    {
        Assert.That(ExpeditionPromotion.TryParseChoice(value, out int classId), Is.False);
        Assert.That(classId, Is.EqualTo(0));
    }

    [Test]
    public void WelcomeText_UsesTheTrainerLineOrAGenericOne()
    {
        Assert.That(ExpeditionPromotion.WelcomeText((int)eCharacterClass.Warden, "Warden", "Aine"), Does.StartWith("Good then! Welcome to the ways of the Warden!"));
        Assert.That(ExpeditionPromotion.WelcomeText((int)eCharacterClass.Druid, "Druid", "Aine"), Does.Contain("suits you, Aine.").And.Not.Contain("{0}"));
        Assert.That(ExpeditionPromotion.WelcomeText((int)eCharacterClass.Infiltrator, "Infiltrator", "Aine"), Is.EqualTo("Welcome to the ways of the Infiltrator!"));
        Assert.That(ExpeditionPromotion.GiftTemplate((int)eCharacterClass.Warden), Is.EqualTo("warden_item"));
        Assert.That(ExpeditionPromotion.GiftTemplate((int)eCharacterClass.Armsman), Is.Null);
    }

    [Test]
    public void WelcomeText_FormatsForEveryPath()
    {
        foreach (ExpeditionPromotion.PromotionPath path in ExpeditionPromotion.AllPaths())
        {
            string text = ExpeditionPromotion.WelcomeText(path.ClassId, path.Name, "Aine");

            Assert.That(text, Is.Not.Empty, path.Name);
            Assert.That(text, Does.Not.Contain("{0}"), path.Name);
        }
    }
}
