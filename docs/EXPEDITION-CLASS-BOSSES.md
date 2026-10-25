# Expedition class bosses (looks built, abilities stored)

**Built (2026-10-25):** the named act bosses wear these looks (race body, armor and weapon models) as plain monsters,
in `gameutils/Expedition/ExpeditionBossLooks.cs`. The styles, damage add, dual wield and pet below are still only a
design.

Five named characters that fight like player classes. Designed on 2026-10-24 as act bosses, then set aside by the owner
"until I can think of how to use them". Nothing here is in the game yet. The act bosses are still: Three Point Oh in
act 1, a Warden of the zone in act 2, and named bosses drawn from Drunkstains, Scob Mahob, Ios, Toothmouth and Gwyraan
in acts 3 to 5 (as plain buffed monsters).

## The five

| Name | Class | Race | Weapons | Armor look | Abilities |
|---|---|---|---|---|---|
| Toothmouth | Battle Bard | Firbolg male | Blade + small shield | Plain Celtic leather | Damage add on its melee hits |
| Ios | Valewalker | Celt female | War Scythe | Valewalker epic | Melee only (scythe styles) |
| Preesh | Blademaster | Firbolg male | Two maces (dual wield) | Blademaster epic | Melee only (Celtic Dual and Blunt styles) |
| Drunkstains | Armsman | Half Ogre male | Halberd | Armsman epic | Melee only (Polearm styles) |
| Gwyraan | Nature Druid | Celt male | Staff | Druid epic | A nature pet that fights beside it |

Owner notes from the same day:

- Preesh replaced Scob Mahob in this set.
- Three Point Oh stays a monster (with its ring drop) and is not one of these.
- The approach agreed on: a monster dressed and armed as the class, aggroing and fighting with the normal mob AI, not a
  real player bot (an owned bot can never attack its owner, and ownerless bots would need their travel, gear and death
  handling tamed).

## Models (client objects.csv / monsters.csv IDs)

Race bodies (`Enums/eLivingModel.cs`): Firbolg male 286, Celt male 302, Celt female 310, Half Ogre male 1008.

| Name | Torso / Legs / Arms / Hands / Feet | Helm | Weapon slots |
|---|---|---|---|
| Toothmouth | Celtic Leather 1: 393 / 394 / 395 / 396 / 397 | 438 (Celtic leather helm) | Right: Leaf Short Sword 903. Left: Hib Wood Buckler 1048 (unverified in code). Active slot Standard |
| Ios | Valewalker epic: vest 1003, then 379 / 380 / 381 / 382 (`Harmony50.cs`) | none | Two-hand: War Scythe 932. Active slot TwoHanded |
| Preesh | Blademaster epic 782 / 783 / 784 / 785 / 786 (`Harmony50.cs`) | 838 (Celtic scale helm) | Right and Left: Celtic Mace 914. Active slot Standard, LeftHandSwingChance about 40 |
| Drunkstains | Armsman epic 688 / 689 / 690 / 691 / 692 (`Defenders50.cs`) | 93 (Albion plate helm) | Two-hand: Halberd 67. Active slot TwoHanded |
| Gwyraan | Druid epic 739 / 740 / 741 / 742 / 743 (`Harmony50.cs`) | none | Two-hand: Hib Staff 1173. Active slot TwoHanded |

Gwyraan's pet: monsters.csv 97 "Fellwood" (a tree creature), a plain mob that never drops loot.

Equip pattern (from `scripts/namedmobs/HallOfTheCorrupt/LordGildas.cs`):

```csharp
GameNpcInventoryTemplate template = new();
template.AddNPCEquipment(eInventorySlot.TorsoArmor, 688);
template.AddNPCEquipment(eInventorySlot.TwoHandWeapon, 67);
boss.Inventory = template.CloseTemplate();
boss.SwitchWeapon(eActiveWeaponSlot.TwoHanded);
```

Don't fill a slot twice (AddNPCEquipment keeps the old item half the time), and leave DistanceWeapon empty.

## Combat notes

- Styles: `boss.Styles = SkillBase.GetStyleList(specKey, classId)` filtered to `SpecLevelRequirement <= Level`, with a
  class-0 fallback. Spec keys: Blades, Blunt, Celtic Dual, Scythe, Polearm. Class IDs: Armsman 2, Blademaster 43,
  Druid 47, Bard 48, Valewalker 56. NPC styles skip the weapon-type check; about 20% of swings try a style
  (`gamenpc_chances_to_style`).
- Dual wield needs a real left-hand weapon, the Standard slot and `LeftHandSwingChance > 0`.
- Toothmouth's damage add is best done in code (an extra damage hit in an `ExpeditionBoss.DealDamage` override), since
  an NPC only casts a DamageAdd buff out of combat.
- If spells are ever added: set `Spells` after `Level` (the Level setter lowers stats for each spell an NPC already
  has), avoid cast-time spells on melee bosses (their swings interrupt their own casts), skip bard songs that need an
  instrument, and give heals a custom brain (NPCs only heal out of combat by default).
