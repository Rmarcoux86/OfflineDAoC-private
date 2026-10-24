using System;
using System.Collections.Generic;
using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>The Expedition unique catalog: its shape, its power against the item caps, and the per-run selection. No server is needed.</summary>
[TestFixture]
public class UT_ExpeditionUniques
{
    [Test]
    public void Catalog_HasEighteenOrMore_SixPerTier_WithUniqueNames()
    {
        Assert.That(ExpeditionUniques.Catalog.Count, Is.GreaterThanOrEqualTo(18));

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (UniqueDefinition definition in ExpeditionUniques.Catalog)
        {
            Assert.That(names.Add(definition.Name), Is.True, $"duplicate name {definition.Name}");
        }

        foreach (eUniqueTier tier in Enum.GetValues<eUniqueTier>())
        {
            Assert.That(ExpeditionUniques.InTier(tier).Count, Is.GreaterThanOrEqualTo(6), $"{tier}");
        }
    }

    [Test]
    public void Catalog_EntriesAreComplete_AndNamesFitTheClient()
    {
        foreach (UniqueDefinition definition in ExpeditionUniques.Catalog)
        {
            Assert.That(definition.Name, Is.Not.Null.And.Not.Empty);
            Assert.That(definition.Name.Trim(), Is.EqualTo(definition.Name));
            Assert.That(definition.Name.Length, Is.LessThanOrEqualTo(ExpeditionLoot.MaxNameLength), definition.Name);
            Assert.That(definition.Name.StartsWith("["), Is.False, $"{definition.Name}: uniques carry no rarity prefix");
            Assert.That(definition.Lore, Is.Not.Null.And.Not.Empty, definition.Name);
            Assert.That(definition.Lore.Contains(" | Expedition "), Is.False, $"{definition.Name}: the lore must not look like a rarity tag");
        }
    }

    [Test]
    public void Catalog_EachUniqueHasFourToSixDistinctLines()
    {
        foreach (UniqueDefinition definition in ExpeditionUniques.Catalog)
        {
            Assert.That(definition.Lines.Length, Is.InRange(ExpeditionUniques.LinesMin, ExpeditionUniques.LinesMax), definition.Name);

            HashSet<eProperty> properties = new();

            foreach (UniqueLine line in definition.Lines)
            {
                Assert.That(properties.Add(line.Property), Is.True, $"{definition.Name}: {line.Property} twice");
                Assert.That(line.Value, Is.GreaterThan(0), $"{definition.Name}: {line.Property}");
            }
        }
    }

    [Test]
    public void Catalog_ValuesAreStrong_ButWithinTheTierTopLevelCaps()
    {
        foreach (UniqueDefinition definition in ExpeditionUniques.Catalog)
        {
            int capLevel = ExpeditionUniques.TierCapLevel(definition.Tier);

            foreach (UniqueLine line in definition.Lines)
            {
                int cap = ExpeditionUniques.LineCap(line.Property, capLevel);

                Assert.That(cap, Is.GreaterThan(0), $"{definition.Name}: {line.Property} has no cap");
                Assert.That(line.Value, Is.LessThanOrEqualTo(cap), $"{definition.Name}: {line.Property} {line.Value} over the level {capLevel} cap {cap}");
                Assert.That(line.Value, Is.GreaterThanOrEqualTo(cap / 2), $"{definition.Name}: {line.Property} {line.Value} is weak for its tier (cap {cap})");
            }
        }
    }

    [Test]
    public void Catalog_UsesOnlyLinesThatHelpEveryClass_AndHasAFewProcs()
    {
        int procs = 0;

        foreach (UniqueDefinition definition in ExpeditionUniques.Catalog)
        {
            foreach (UniqueLine line in definition.Lines)
            {
                bool universal = line.Property is eProperty.Strength or eProperty.Constitution or eProperty.Dexterity or eProperty.Quickness
                    or eProperty.MaxHealth or eProperty.AllSkills
                    || (line.Property >= eProperty.Resist_First && line.Property <= eProperty.Resist_Last);

                Assert.That(universal, Is.True, $"{definition.Name}: {line.Property}");
            }

            if (definition.Proc != eUniqueProc.None)
            {
                procs++;
                Assert.That(definition.Slot, Is.EqualTo(eUniqueSlot.Weapon).Or.EqualTo(eUniqueSlot.Chest), $"{definition.Name}: the generator only procs weapons and chests");
            }
        }

        Assert.That(procs, Is.InRange(2, 3));
    }

    [Test]
    public void LineCaps_FollowThePropertyCalculators()
    {
        Assert.That(ExpeditionUniques.LineCap(eProperty.Strength, 20), Is.EqualTo(30));
        Assert.That(ExpeditionUniques.LineCap(eProperty.Constitution, 40), Is.EqualTo(60));
        Assert.That(ExpeditionUniques.LineCap(eProperty.Quickness, 50), Is.EqualTo(75));
        Assert.That(ExpeditionUniques.LineCap(eProperty.Dexterity, 15), Is.EqualTo(22));
        Assert.That(ExpeditionUniques.LineCap(eProperty.MaxHealth, 20), Is.EqualTo(80));
        Assert.That(ExpeditionUniques.LineCap(eProperty.MaxHealth, 50), Is.EqualTo(200));
        Assert.That(ExpeditionUniques.LineCap(eProperty.Resist_Body, 20), Is.EqualTo(11));
        Assert.That(ExpeditionUniques.LineCap(eProperty.Resist_Thrust, 40), Is.EqualTo(21));
        Assert.That(ExpeditionUniques.LineCap(eProperty.Resist_Heat, 50), Is.EqualTo(26));
        Assert.That(ExpeditionUniques.LineCap(eProperty.AllSkills, 20), Is.EqualTo(5));
        Assert.That(ExpeditionUniques.LineCap(eProperty.AllSkills, 40), Is.EqualTo(9));
        Assert.That(ExpeditionUniques.LineCap(eProperty.Skill_Slashing, 50), Is.EqualTo(11));
        Assert.That(ExpeditionUniques.LineCap(eProperty.MaxMana, 50), Is.EqualTo(0));
    }

    [Test]
    public void Tiers_FollowThePlayerLevel()
    {
        Assert.That(ExpeditionUniques.TierFor(1), Is.EqualTo(eUniqueTier.Early));
        Assert.That(ExpeditionUniques.TierFor(19), Is.EqualTo(eUniqueTier.Early));
        Assert.That(ExpeditionUniques.TierFor(20), Is.EqualTo(eUniqueTier.Mid));
        Assert.That(ExpeditionUniques.TierFor(39), Is.EqualTo(eUniqueTier.Mid));
        Assert.That(ExpeditionUniques.TierFor(40), Is.EqualTo(eUniqueTier.Late));
        Assert.That(ExpeditionUniques.TierFor(50), Is.EqualTo(eUniqueTier.Late));

        foreach (eUniqueTier tier in Enum.GetValues<eUniqueTier>())
        {
            Assert.That(ExpeditionUniques.TierFor(ExpeditionUniques.TierMinLevel(tier)), Is.EqualTo(tier));
            Assert.That(ExpeditionUniques.TierCapLevel(tier), Is.GreaterThan(ExpeditionUniques.TierMinLevel(tier)));
        }
    }

    [Test]
    public void Pick_StaysInTheTier_AndDoesNotRepeatUntilTheTierIsExhausted()
    {
        foreach (eUniqueTier tier in Enum.GetValues<eUniqueTier>())
        {
            Random rng = new(7 + (int) tier);
            HashSet<string> found = new();
            int size = ExpeditionUniques.InTier(tier).Count;

            for (int i = 0; i < size; i++)
            {
                int before = found.Count;
                UniqueDefinition picked = ExpeditionUniques.Pick(rng, tier, found);

                Assert.That(picked.Tier, Is.EqualTo(tier));
                Assert.That(found.Count, Is.EqualTo(before + 1), $"{tier}: {picked.Name} came twice before the tier was exhausted");
            }

            UniqueDefinition again = ExpeditionUniques.Pick(rng, tier, found);

            Assert.That(again, Is.Not.Null);
            Assert.That(again.Tier, Is.EqualTo(tier));
            Assert.That(found.Count, Is.EqualTo(size), "once exhausted, repeats are allowed");
        }
    }

    [Test]
    public void Pick_WithoutAFoundList_StillPicksFromTheTier()
    {
        UniqueDefinition picked = ExpeditionUniques.Pick(new Random(3), eUniqueTier.Late, null);

        Assert.That(picked.Tier, Is.EqualTo(eUniqueTier.Late));
    }

    [Test]
    public void ProcSpells_UseTheGeneratorsIdsAndBrackets()
    {
        Assert.That(ExpeditionUniques.ProcSpellFor(eUniqueProc.None, 30), Is.EqualTo(0));
        Assert.That(ExpeditionUniques.ProcSpellFor(eUniqueProc.Lifetap, 1), Is.EqualTo(8010));
        Assert.That(ExpeditionUniques.ProcSpellFor(eUniqueProc.Lifetap, 14), Is.EqualTo(8011));
        Assert.That(ExpeditionUniques.ProcSpellFor(eUniqueProc.Lifetap, 19), Is.EqualTo(8012));
        Assert.That(ExpeditionUniques.ProcSpellFor(eUniqueProc.Damage, 20), Is.EqualTo(8023));
        Assert.That(ExpeditionUniques.ProcSpellFor(eUniqueProc.Damage, 39), Is.EqualTo(8026));
        Assert.That(ExpeditionUniques.ProcSpellFor(eUniqueProc.Heal, 40), Is.EqualTo(8037));
        Assert.That(ExpeditionUniques.ProcSpellFor(eUniqueProc.Heal, 50), Is.EqualTo(8037));
        Assert.That(ExpeditionUniques.ProcSpellFor(eUniqueProc.Absorb, 25), Is.EqualTo(8044));
    }

    [Test]
    public void Slots_MapToTheGeneratorsSlotsAndModels()
    {
        Assert.That(ExpeditionUniques.InventorySlotFor(eUniqueSlot.Necklace), Is.EqualTo(eInventorySlot.Neck));
        Assert.That(ExpeditionUniques.InventorySlotFor(eUniqueSlot.Cloak), Is.EqualTo(eInventorySlot.Cloak));
        Assert.That(ExpeditionUniques.InventorySlotFor(eUniqueSlot.Jewel), Is.EqualTo(eInventorySlot.Jewelry));
        Assert.That(ExpeditionUniques.InventorySlotFor(eUniqueSlot.Belt), Is.EqualTo(eInventorySlot.Waist));
        Assert.That(ExpeditionUniques.InventorySlotFor(eUniqueSlot.Ring), Is.EqualTo(eInventorySlot.LeftRing));
        Assert.That(ExpeditionUniques.InventorySlotFor(eUniqueSlot.Bracer), Is.EqualTo(eInventorySlot.LeftBracer));
        Assert.That(ExpeditionUniques.InventorySlotFor(eUniqueSlot.Chest), Is.EqualTo(eInventorySlot.TorsoArmor));
        Assert.That(ExpeditionUniques.InventorySlotFor(eUniqueSlot.Helm), Is.EqualTo(eInventorySlot.HeadArmor));

        foreach (eUniqueSlot slot in Enum.GetValues<eUniqueSlot>())
        {
            Assert.That(ExpeditionUniques.ModelFor(slot) > 0, Is.EqualTo(ExpeditionUniques.IsJewelry(slot)), $"{slot}");
        }

        Assert.That(ExpeditionUniques.IsMeleeWeapon(eObjectType.SlashingWeapon), Is.True);
        Assert.That(ExpeditionUniques.IsMeleeWeapon(eObjectType.Staff), Is.True);
        Assert.That(ExpeditionUniques.IsMeleeWeapon(eObjectType.Longbow), Is.False);
        Assert.That(ExpeditionUniques.IsMeleeWeapon(eObjectType.Shield), Is.False);
        Assert.That(ExpeditionUniques.IsMeleeWeapon(eObjectType.Instrument), Is.False);
    }

    [Test]
    public void Exchanger_FeesRiseByTier_AndEveryRealmHasWeapons()
    {
        Assert.That(ExpeditionExchanger.FeeGold(eUniqueTier.Early), Is.LessThan(ExpeditionExchanger.FeeGold(eUniqueTier.Mid)));
        Assert.That(ExpeditionExchanger.FeeGold(eUniqueTier.Mid), Is.LessThan(ExpeditionExchanger.FeeGold(eUniqueTier.Late)));

        foreach (eRealm realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
        {
            var options = ExpeditionExchanger.RealmWeapons(realm);

            Assert.That(options.Count, Is.GreaterThanOrEqualTo(6), realm.ToString());
            Assert.That(new HashSet<string>(System.Linq.Enumerable.Select(options, o => o.Label)).Count, Is.EqualTo(options.Count), $"{realm}: labels are unique");
        }
    }

    [Test]
    public void FindByName_FindsEveryUnique()
    {
        foreach (UniqueDefinition definition in ExpeditionUniques.Catalog)
        {
            Assert.That(ExpeditionUniques.FindByName(definition.Name), Is.SameAs(definition));
        }

        Assert.That(ExpeditionUniques.FindByName("No Such Blade"), Is.Null);
    }

    [Test]
    public void ThreePointOhRing_HasEveryAttributeOnce()
    {
        Assert.That(ExpeditionUniques.ThreePointOhRingStats.Length, Is.EqualTo(8));
        Assert.That(new HashSet<DOL.GS.eProperty>(ExpeditionUniques.ThreePointOhRingStats).Count, Is.EqualTo(8));
        Assert.That(ExpeditionUniques.ThreePointOhRingName, Is.EqualTo("Three Point Oh's Ring"));
    }
}
