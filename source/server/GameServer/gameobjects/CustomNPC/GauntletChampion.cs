using System;
using System.Collections.Generic;
using System.Linq;
using DOL.AI;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.Effects;
using DOL.GS.PropertyCalc;
using DOL.GS.RealmAbilities;
using DOL.GS.Styles;

namespace DOL.GS.Expedition.Gauntlet;

/// <summary>
/// A Gauntlet champion (docs/EXPEDITION-GAUNTLET-PLAN.md, Approach A): an ownerless level-50 GameBot of the champion's
/// class, with realm None so the challenger may attack it and it may attack the challenger without any server-rule
/// change. It is a throwaway: no owner, no database row, no BotManager or autonomous registration, no corpse and no
/// respawn. It buffs where it appears and waits there, buffs kept up, until its challenger
/// targets it and bows (/bow) or attacks it; then it fights only the challenger and the challenger's pets, within <see cref="GauntletRules.LeashRadius"/> of where it appeared. Its death is reported to
/// <see cref="GauntletManager.OnChampionDied"/>.
/// </summary>
public sealed class GauntletChampion : GameBot
{
    public const byte SpawnLevel = 50;

    /// <summary>The Shields style whose short stun the champion purges only half the time (owner 2026-10-27).</summary>
    public const string NumbStyleName = "Numb";

    /// <summary>A stun counts as Numb's when it starts this soon after a Numb hit.</summary>
    private const long NumbWindowMs = 3_000;

    /// <summary>Toothmouth's champion-only realm ability (owner 2026-10-27; Bards do not have it in ClassXRealmAbility_Atlas).</summary>
    public const string SpeedOfSoundKey = "AtlasOF_SpeedOfSound";

    /// <summary>AtlasOF_SpeedOfSound's duration (its m_duration).</summary>
    private const int SpeedOfSoundMs = 30_000;

    /// <summary>Preesh's parry chain stun (owner 2026-10-28): when it lands he uses Triple Wield.</summary>
    public const string TripleWieldTriggerStyle = "Devastating Blow";

    // Triple Wield and Flurry as TripleWieldAbilityHandler and FlurryAbilityHandler run them (both take a GamePlayer only).
    private const int TripleWieldMs = 30_000;
    private const long TripleWieldReuseMs = 7 * 60 * 1000;
    private const long FlurryReuseMs = 2 * 60 * 1000;
    private const int FlurryRange = 135;
    private long _nextTripleWieldAt;
    private long _nextFlurryAt;

    /// <summary>The player this champion fights; null once it has been removed.</summary>
    public GamePlayer Challenger { get; private set; }

    public ChampionDefinition Definition { get; }

    /// <summary>Its signature (styles and positions), as data; <see cref="ChampionSignature.None"/> for a champion without one yet.</summary>
    public ChampionSignature Signature { get; }

    /// <summary>The styles the brain wants first right now (its signature step); null for the signature's own priority.</summary>
    internal IReadOnlyList<string> CurrentPriority { get; set; }

    /// <summary>
    /// The weapon slot the brain holds (Drunkstains: shield between polearm swings; Toothmouth: his mace and shield, never
    /// his flute). While set, every melee switch request (BotBrain's own weapon choice and its instrument for a song
    /// included) goes to this slot; null leaves the bot's choice alone.
    /// </summary>
    internal eActiveWeaponSlot? HeldSlot { get; set; }

    /// <summary>Gwyraan's DoT (Degeneration), cast only by his brain (kept out of <see cref="GameNPC.Spells"/>).</summary>
    internal Spell SignatureDot { get; private set; }

    /// <summary>Gwyraan's best damage add, kept up on himself.</summary>
    internal Spell SignatureBuff { get; private set; }

    /// <summary>Gwyraan's pet (Call Greater Nature Spirit), summoned before the fight.</summary>
    internal Spell SignaturePet { get; private set; }

    /// <summary>Gwyraan's instant root (Bursting Undergrowth), cast only by his brain.</summary>
    internal Spell SignatureRoot { get; private set; }

    /// <summary>Gwyraan's instant heal (Bith's Benison), kept out of the bot's own heals.</summary>
    internal Spell SignatureInstantHeal { get; private set; }

    /// <summary>The heal a champion casts on itself after a retreat (Gwyraan: Major Restoration; Toothmouth: his best).</summary>
    internal Spell SignatureHeal { get; private set; }

    /// <summary>Toothmouth's instant mezzes, best first, cast only by his brain.</summary>
    internal IReadOnlyList<Spell> SignatureMezzes { get; private set; } = Array.Empty<Spell>();

    /// <summary>Toothmouth's instant shout (Concussive Roar), woven between his swings by his brain.</summary>
    internal Spell SignatureShout { get; private set; }

    /// <summary>Ios's instant snare (Veil Path), cast only by her brain.</summary>
    internal Spell SignatureSnare { get; private set; }

    /// <summary>Ios's lifedrain (Cycle of Death, or her best), cast only by her brain.</summary>
    internal Spell SignatureDrain { get; private set; }

    /// <summary>The buffs it casts before the fight, in order (<see cref="ChampionSpells.PrefightBuffs"/>).</summary>
    internal IReadOnlyList<Spell> PrefightBuffs { get; private set; } = Array.Empty<Spell>();

    /// <summary>When it last took a Numb hit (GameLoop time); 0 for never.</summary>
    internal long LastNumbTick { get; private set; }

    /// <summary>The stun spells Numb's hits proc (StyleProcInfo), to tell its stun from any other.</summary>
    private readonly HashSet<int> _numbStunSpellIds = new();

    /// <summary>The Numb stun the purge coin was last tossed for, and its result: one toss per stun.</summary>
    private ECSGameEffect _numbStunTossed;

    private bool _numbStunPurges;

    /// <summary>The Gauntlet round this champion was spawned for.</summary>
    public int Round { get; }

    /// <summary>When its minimum buffing time ends (GameLoop time).</summary>
    internal long BuffUntilTick { get; set; }

    /// <summary>
    /// When it stops waiting for its buffs and is ready whatever is left (GameLoop time): a safety net against a buff
    /// that can never land; 0 for none.
    /// </summary>
    internal long PrefightDeadlineTick { get; set; }

    /// <summary>True once it has finished buffing and waits to be challenged (it stays where it appeared).</summary>
    internal bool AtCentre { get; set; }

    /// <summary>
    /// True once it fights (owner 2026-10-25): when its challenger targets it and bows (GauntletManager.OnBow), or as
    /// soon as the challenger's side attacks it. Until then it never starts a fight.
    /// </summary>
    public bool Engaged { get; internal set; }

    /// <summary>
    /// True for a hard run's act boss (ExpeditionHardBosses, owner 2026-10-29: "make the act bosses the champion versions
    /// from the original gauntlet"): it fights from the start like a boss, with no bow and no arena, and its death is the
    /// act boss's (ObjectiveTracker.OnBossDied: the boss loot and realm points). It is never part of a Gauntlet session.
    /// </summary>
    public bool ActBoss { get; }

    /// <param name="level">Its level: <see cref="SpawnLevel"/> in the Gauntlet, the act's top level + 2 for an act boss.</param>
    /// <param name="actBoss">A hard run's act boss (<see cref="ActBoss"/>).</param>
    public GauntletChampion(GamePlayer challenger, ChampionDefinition definition, int round, RoundTier tier, byte? level = null, bool actBoss = false)
        : base((byte) definition.ClassId, definition.Name, (byte) definition.RaceId, (byte) definition.Gender, level ?? SpawnLevel, eSpecType.None, BuildFor(definition))
    {
        ActBoss = actBoss;
        Challenger = challenger;
        Definition = definition;
        Round = round;
        Signature = SignatureFor(definition?.Name);
        eCharacterClass characterClass = (eCharacterClass) definition.ClassId;

        // Drunkstains fights with a polearm and with a one-hander and shield (owner 2026-10-25). EquipBot draws a
        // TwoHandHybrid Armsman's one-hander, polearm and shield from the item table (GameBot.SetWeapons, SetShield) and
        // leaves a slot empty when the table has nothing that fits; an empty slot gets a generated item from the
        // generator bots use for a missing weapon (BotEquipment.SetWeaponROG, GeneratedUniqueItem).
        if (Signature.ShieldStun && Inventory != null)
        {
            if (Inventory.GetItem(eInventorySlot.TwoHandWeapon) == null)
            {
                BotEquipment.SetWeaponROG(this, Realm, characterClass, Level, eObjectType.PolearmWeapon, eInventorySlot.TwoHandWeapon, eDamageType.Slash);
            }

            if (Inventory.GetItem(eInventorySlot.RightHandWeapon) == null)
            {
                BotEquipment.SetWeaponROG(this, Realm, characterClass, Level, eObjectType.SlashingWeapon, eInventorySlot.RightHandWeapon, eDamageType.Slash);
            }

            if (Inventory.GetItem(eInventorySlot.LeftHandWeapon) == null)
            {
                // A shield's Type_Damage is its size (BotEquipment.SetShield); 3 is a large shield.
                BotEquipment.SetWeaponROG(this, Realm, characterClass, Level, eObjectType.Shield, eInventorySlot.LeftHandWeapon, (eDamageType) 3);
            }

            SwitchWeapon(eActiveWeaponSlot.TwoHanded);
        }

        if (Signature.DrainKite)
        {
            // Ios (owner 2026-10-27): her snare and lifedrain are her brain's alone (the ranged opener, the kite at 40%).
            SignatureSnare = ChampionSpells.Named(Spells, "Veil Path") ?? ChampionSpells.Best(Spells, eSpellType.SpeedDecrease, Level, true);
            SignatureDrain = ChampionSpells.Named(Spells, "Cycle of Death") ?? ChampionSpells.Best(Spells, eSpellType.Lifedrain, Level);
            Spells = Spells.Where(spell => spell != SignatureSnare && spell != SignatureDrain).ToList();
        }

        if (Signature.RangedDot)
        {
            // Gwyraan (owner 2026-10-27): a mace and a shield (Druids have Weaponry: Blunt and Shield 1 from
            // NaturalistCareer), his pet, his DoT, root and heals. The pet summon, the attack spells and the instant heal
            // are his brain's alone; the bot keeps its other heals and buffs.
            EquipBluntAndShield(characterClass);
            SignatureDot = ChampionSpells.Named(Spells, "Degeneration") ?? ChampionSpells.Best(Spells, eSpellType.DamageOverTime, Level);
            SignatureBuff = ChampionSpells.Best(Spells, eSpellType.DamageAdd, Level);
            SignaturePet = ChampionSpells.Named(Spells, "Call Greater Nature Spirit") ?? ChampionSpells.Best(Spells, eSpellType.SummonDruidPet, Level);
            SignatureRoot = ChampionSpells.Named(Spells, "Bursting Undergrowth") ?? ChampionSpells.BestInstantRoot(Spells, Level);
            SignatureHeal = ChampionSpells.Named(Spells, "Major Restoration") ?? ChampionSpells.BestSelfHeal(Spells, Level);
            SignatureInstantHeal = ChampionSpells.Named(Spells, "Bith's Benison") ?? ChampionSpells.BestInstantHeal(Spells, Level);
            Spells = ChampionSpells.WithoutPetsAndAttacks(Spells).Where(spell => spell != SignatureInstantHeal).ToList();
        }

        if (Signature.MezRetreat)
        {
            // Toothmouth (owner 2026-10-27): a mace and a shield (Bards have Weaponry: Blunt and Shield 1 from
            // NaturalistCareer). His instant mezzes and his shout are his brain's alone.
            EquipBluntAndShield(characterClass);
            SignatureMezzes = Spells.Where(spell => spell != null && spell.SpellType == eSpellType.Mesmerize && spell.CastTime <= 0 && spell.Level <= Level)
                .OrderByDescending(spell => spell.Level)
                .ToList();
            SignatureShout = ChampionSpells.Named(Spells, "Concussive Roar") ?? ChampionSpells.Best(Spells, eSpellType.DirectDamage, Level, true);
            SignatureHeal = ChampionSpells.BestSelfHeal(Spells, Level);
            Spells = Spells.Where(spell => spell.SpellType != eSpellType.Mesmerize && spell != SignatureShout).ToList();

            // Speed of Sound, a champion-only exception for a Bard (owner 2026-10-27), fired by his brain (TrySpeedOfSound).
            if (SkillBase.GetAbility(SpeedOfSoundKey, 1) is RealmAbility speedOfSound)
            {
                AddAbility(speedOfSound, false);
            }
        }

        PrefightBuffs = ChampionSpells.PrefightBuffs(Spells, Level);

        // Gear is picked by realm in the base constructor; only now does the champion leave its realm.
        _homeRealm = Realm;
        Realm = eRealm.None;

        // Escalation (GauntletEscalation): the realm rank, its realm abilities (owner 2026-10-27: Purge first, then
        // Mastery of Pain, Parrying, Blocking, Ignore Pain...; AutonomousBotRealmAbilities.GrantChampion), and the
        // open-ended stat bonus from round 21.
        RealmLevel = Math.Max(0, (tier.RealmRank - 1) * 10);
        AutonomousBotRealmAbilities.GrantChampion(this, AutonomousBotRealmAbilities.Points(Level, RealmLevel));

        // An early round's champion hits for less (GauntletEscalation.WeakenedPercent): melee (AttackComponent's damage type
        // modifier) and spells (SpellHandler's effectiveness); its health is cut in MaxHealth.
        // A hard run's act boss is never weakened (owner 2026-10-29: hard mode).
        int weakened = actBoss ? 0 : GauntletEscalation.WeakenedPercent(round);
        AbilityBonus[eProperty.MeleeDamage] -= weakened;
        AbilityBonus[eProperty.SpellDamage] -= weakened;

        if (tier.StatBonusPercent > 0)
        {
            for (eStat stat = eStat._First; stat <= eStat._Last; stat++)
            {
                int bonus = (int) Math.Round(GetBaseStat(stat) * tier.StatBonusPercent / 100.0);

                if (bonus > 0)
                {
                    ChangeBaseStat(stat, (short) Math.Min(bonus, short.MaxValue));
                }
            }

            Health = MaxHealth;
        }

        ApplyLooks();
        RollGear(round);
        StripProcs();
        SetGearQuality();

        // Its last name is its realm rank's title in its class's home realm (owner 2026-10-25: "Ios Raven Ardent").
        Name = $"{definition.Name} {RankTitle(definition, tier.RealmRank)}";

        // Its elite affix (owner 2026-10-28): the effect, and the affix before its name ("Swift Ios Raven Ardent").
        if (!string.IsNullOrEmpty(definition.Affix))
        {
            // Preesh keeps Swift's faster swings (owner 2026-10-28); the others get an affix's run speed only.
            ExpeditionElites.ApplyChampionAffix(this, definition.Affix, definition.Name == GauntletChampions.FinalChampion);
        }

        RefreshItemBonuses();
        Health = MaxHealth;

        if (actBoss)
        {
            // An act boss fights at once, as a boss does: no bow. Its leash is the Gauntlet's, from where it appears;
            // ExpeditionHardMonsters.OnBossSpawned doubles it (the hard run's rule).
            Engaged = true;
            TetherRange = GauntletRules.LeashRadius;
        }
        else
        {
            // The leash is measured from SpawnPoint: where it appears (GameNPC.AddToWorld sets it).
            TetherRange = GauntletRules.LeashRadius;
        }

        GauntletChampionBrain brain = new() { IsHealer = (Brain as BotBrain)?.IsHealer ?? false };
        SetOwnBrain(brain);
    }

    /// <summary>
    /// A one-handed mace in the right hand and a small shield in the left (Gwyraan, Toothmouth; owner 2026-10-27).
    /// Their BotSpec asks SetWeapons for a Blunt one-hander and SetShield for their Shield 1 (small) shield; whatever the
    /// item table left out, or filled with another type, is replaced by a generated item.
    /// </summary>
    private void EquipBluntAndShield(eCharacterClass characterClass)
    {
        if (Inventory == null)
        {
            return;
        }

        DbInventoryItem right = Inventory.GetItem(eInventorySlot.RightHandWeapon);

        if (right != null && right.Object_Type != (int) eObjectType.Blunt)
        {
            Inventory.RemoveItem(right);
            right = null;
        }

        if (right == null)
        {
            BotEquipment.SetWeaponROG(this, Realm, characterClass, Level, eObjectType.Blunt, eInventorySlot.RightHandWeapon, eDamageType.Crush);
        }

        DbInventoryItem left = Inventory.GetItem(eInventorySlot.LeftHandWeapon);

        if (left != null && left.Object_Type != (int) eObjectType.Shield)
        {
            Inventory.RemoveItem(left);
            left = null;
        }

        if (left == null)
        {
            // A shield's Type_Damage is its size (BotEquipment.SetShield); 1 is a small shield, all a Shield 1 class may use.
            BotEquipment.SetWeaponROG(this, Realm, characterClass, Level, eObjectType.Shield, eInventorySlot.LeftHandWeapon, (eDamageType) 1);
        }

        SwitchWeapon(eActiveWeaponSlot.Standard);
    }

    /// <summary>
    /// Styles this champion uses first whenever one can be used, in order (BotMeleeStylePolicy.Select, at every swing).
    /// The brain sets them for its current signature step; otherwise they are the signature's own priority. These may be
    /// positional: Select lets the champion's own priority styles through BotMeleeStylePolicy.IsEligible's positional
    /// exclusion, and StyleProcessor.CanUseStyle still decides whether the position is right.
    /// </summary>
    public IReadOnlyList<string> PriorityStyles => CurrentPriority ?? Signature?.Priority ?? Array.Empty<string>();

    /// <summary>
    /// The signatures, by Style.Name in the server's style table (owner 2026-10-25, revised 2026-10-27).
    /// Ios (Scythe): behind her challenger for Damaging Grasp (back positional, 8 s stun), then from the side Stunning
    /// Blade (side positional), Winter's Scythe, Blizzard Blade; while the challenger is immune to stuns the fire chain,
    /// Arboreal Fire (after her evade), Foxfire, Flaming Scythe, Conflagration.
    /// Preesh (Blunt, Celtic Dual): after his parry Stunning Blow, then Devastating Blow (10 s stun); while the challenger
    /// is stunned, from the side Ice Storm (side positional), Tempest, Supernova; while the challenger is immune to stuns,
    /// after his parry Tornado, Meteor Shower, Twin Star.
    /// Drunkstains (Polearm, Shields): Slam or Numb from the shield whenever the challenger can be stunned; the polearm
    /// only Defender's Rage (after the challenger's styled hit) and otherwise Crippling Blow.
    /// Toothmouth (Blunt): behind a mezzed challenger Back Crush, then Crushing Blow.
    /// Every champion's filler is <see cref="ChampionSignature.Filler"/> or its best anytime style (ChampionStyles); Preesh's
    /// is the Hurricane, Solar Flare chain (owner 2026-10-28, <see cref="ChampionSignature.FillerFollowup"/>).
    /// </summary>
    public static ChampionSignature SignatureFor(string name)
    {
        return name switch
        {
            "Ios" => new ChampionSignature([], "Damaging Grasp", ["Stunning Blade", "Winter's Scythe", "Blizzard Blade"],
                ["Conflagration", "Flaming Scythe", "Foxfire", "Arboreal Fire"], DrainKite: true, BackStunAnywhere: true),
            "Preesh" => new ChampionSignature(["Devastating Blow", "Stunning Blow"], null, ["Ice Storm", "Tempest", "Supernova"],
                ["Twin Star", "Meteor Shower", "Tornado"], Filler: "Hurricane", BladeWork: true, FillerFollowup: "Solar Flare"),
            "Drunkstains" => new ChampionSignature(["Defender's Rage"], null, [], [], Filler: "Crippling Blow", ShieldStun: true),
            "Gwyraan" => new ChampionSignature([], null, [], [], RangedDot: true),
            "Toothmouth" => new ChampionSignature(["Crushing Blow"], "Back Crush", [], [], MezRetreat: true),
            _ => ChampionSignature.None
        };
    }

    /// <summary>A champion's fixed build, or null for its class's usual one.</summary>
    private static BotSpec BuildFor(ChampionDefinition definition)
    {
        return definition?.Name switch
        {
            "Ios" => new IosBotSpec(),
            "Preesh" => new PreeshBotSpec(),
            "Drunkstains" => new DrunkstainsBotSpec(),
            "Gwyraan" => new GwyraanBotSpec(),
            "Toothmouth" => new ToothmouthBotSpec(),
            _ => null
        };
    }

    /// <summary>
    /// A Stalwart champion (Toothmouth, owner 2026-10-28; it replaced his double health) has triple health: every reading
    /// of MaxHealth (regeneration, heals, HealthPercent) sees the larger pool.
    /// </summary>
    /// <para>An early round's champion has less of it (<see cref="GauntletEscalation.WeakenedPercent"/>).</para>
    public override int MaxHealth
    {
        get
        {
            int health = Definition?.Affix == ExpeditionElites.Stalwart ? base.MaxHealth * ExpeditionElites.StalwartHealthMultiplier : base.MaxHealth;

            // A hard run's act boss (owner 2026-10-29): the act boss's health pool (ExpeditionHardBosses.HealthMultiplierFor) in
            // place of Stalwart's triple, never weakened.
            if (ActBoss)
            {
                return base.MaxHealth * ExpeditionHardBosses.HealthMultiplierFor(Round);
            }

            return health * (100 - GauntletEscalation.WeakenedPercent(Round)) / 100;
        }
    }

    /// <summary>A melee switch while the brain holds a slot goes to the held slot (see <see cref="HeldSlot"/>).</summary>
    public override void SwitchWeapon(eActiveWeaponSlot slot)
    {
        eActiveWeaponSlot? held = HeldSlot;

        if (held.HasValue && slot is eActiveWeaponSlot.Standard or eActiveWeaponSlot.TwoHanded)
        {
            slot = held.Value;
        }

        base.SwitchWeapon(slot);
    }

    /// <summary>The realm rank title for a champion: its class's home realm, its gender, rank 1 to 14.</summary>
    public static string RankTitle(ChampionDefinition definition, int realmRank)
    {
        int realm = 3;

        foreach (KeyValuePair<eRealm, List<eCharacterClass>> entry in GlobalConstants.STARTING_CLASSES_DICT)
        {
            if (entry.Value.Contains((eCharacterClass) definition.ClassId))
            {
                realm = (int) entry.Key;

                break;
            }
        }

        int gender = definition.Gender == (int) eGender.Female ? 1 : 0;
        int rank = Math.Clamp(realmRank, 1, GauntletEscalation.MaxRealmRank);

        return GlobalConstants.REALM_RANK_NAMES[realm - 1, gender, rank - 1];
    }

    /// <summary>The champion's class's own realm (its gear is that realm's); its Realm is None so it can fight anyone.</summary>
    private readonly eRealm _homeRealm;

    /// <summary>
    /// The champion's gear, scaled across the five fights (owner 2026-10-26, from the drop rates a player finishing act 5
    /// has seen): fights 1-2 all Rare, 3-4 each piece Rare or Epic at random, 5 (Preesh) all Epic. Every armor and
    /// jewelry piece is rolled again as an Expedition item of that rarity, type and slot for the champion's class and
    /// realm, keeping the piece's look. Weapons and shields stay the class's own.
    /// </summary>
    public static eLootRarity GearRarityFor(int round, Random rng)
    {
        if (round <= 2)
        {
            return eLootRarity.Rare;
        }

        if (round <= 4)
        {
            return rng.Next(2) == 0 ? eLootRarity.Rare : eLootRarity.Epic;
        }

        return eLootRarity.Epic;
    }

    private void RollGear(int round)
    {
        if (Inventory == null)
        {
            return;
        }

        foreach (DbInventoryItem item in Inventory.EquippedItems.ToList())
        {
            if (item == null)
            {
                continue;
            }

            eInventorySlot slot = (eInventorySlot) item.SlotPosition;

            if (slot is eInventorySlot.RightHandWeapon or eInventorySlot.LeftHandWeapon or eInventorySlot.TwoHandWeapon or eInventorySlot.DistanceWeapon)
            {
                continue;
            }

            GeneratedUniqueItem rolled = ExpeditionLoot.RollFor(_homeRealm, (eCharacterClass) CharacterClass.ID, Level, (eObjectType) item.Object_Type, (eInventorySlot) item.Item_Type, GearRarityFor(round, Random.Shared));

            if (rolled == null)
            {
                continue;
            }

            rolled.Model = item.Model;
            rolled.Color = item.Color;
            rolled.Effect = item.Effect;
            rolled.Extension = item.Extension;
            item.Template = rolled;
        }
    }

    /// <summary>The quality of every piece a champion wears (owner 2026-10-28: a full template is 99% gear).</summary>
    public const int GearQuality = 99;

    /// <summary>
    /// Every equipped piece, weapons and shields too, at <see cref="GearQuality"/>, each on its own copy of its template;
    /// the copies are the gear it was issued (<see cref="HasAbilityToUseItem"/>).
    /// </summary>
    private void SetGearQuality()
    {
        if (Inventory == null)
        {
            return;
        }

        foreach (DbInventoryItem item in Inventory.EquippedItems.ToList())
        {
            if (item == null || item.Template == null)
            {
                continue;
            }

            DbItemTemplate own = new(item.Template) { Quality = GearQuality };
            item.Template = own;
            _issuedGear.Add(own);
        }
    }

    /// <summary>The templates of the gear it was issued (<see cref="SetGearQuality"/>), compared by reference.</summary>
    private readonly HashSet<DbItemTemplate> _issuedGear = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Its issued gear is always usable. Its Realm is None, and ServerRules.CheckAbilityToUseItem refuses an item of
    /// another realm (and, with allow_cross_realm_items, finds no weapon ability for realm None), so every weapon of its
    /// home realm counted as unusable: BotMeleeStylePolicy.Select gave no style, and BotBrain's weapon repair
    /// (GameBot.EnsureBotWeaponReady) replaced the main weapon with a starter one and stowed the other in the backpack,
    /// which took Drunkstains's one-hander, and with it his shield and his shield stuns (owner 2026-10-28: "he just felt broken").
    /// </summary>
    public override bool HasAbilityToUseItem(DbItemTemplate item)
    {
        return item != null && _issuedGear.Contains(item) || base.HasAbilityToUseItem(item);
    }

    /// <summary>No procs on any of the champion's gear (owner 2026-10-27). A shared template is copied before it changes.</summary>
    private void StripProcs()
    {
        if (Inventory == null)
        {
            return;
        }

        foreach (DbInventoryItem item in Inventory.EquippedItems.ToList())
        {
            if (item == null || (item.ProcSpellID == 0 && item.ProcSpellID1 == 0))
            {
                continue;
            }

            item.Template = new DbItemTemplate(item.Template) { ProcSpellID = 0, ProcSpellID1 = 0 };
        }
    }

    /// <summary>Stats at their item cap (level x 1.5), resists at theirs (level / 2 + 1) and hits at theirs (level x 4).</summary>
    public static (int Stat, int Resist, int Hits) TemplateCaps(int level)
    {
        return ((int) (level * 1.5), level / 2 + 1, level * 4);
    }

    /// <summary>
    /// A full template without Trials of Atlantis (owner 2026-10-27: "template them, but no ToA bonuses"): whatever the
    /// rolled gear gives, strength, constitution, dexterity, quickness and the class's casting stat are at their item cap,
    /// every resist at its cap and hits at theirs; no ToA percentage bonus at all.
    /// </summary>
    public override void RefreshItemBonuses()
    {
        base.RefreshItemBonuses();

        foreach (eProperty toa in new[] { eProperty.MeleeDamage, eProperty.StyleDamage, eProperty.MeleeSpeed, eProperty.SpellDamage, eProperty.CastingSpeed, eProperty.SpellDuration })
        {
            ItemBonus[toa] = 0;
        }

        (int statCap, int resistCap, int hitsCap) = TemplateCaps(Level);
        var stats = new List<eProperty> { eProperty.Strength, eProperty.Constitution, eProperty.Dexterity, eProperty.Quickness };

        if (CharacterClass != null && CharacterClass.ManaStat != eStat.UNDEFINED)
        {
            stats.Add((eProperty) CharacterClass.ManaStat);
        }

        foreach (eProperty stat in stats)
        {
            ItemBonus[stat] = Math.Max(ItemBonus[stat], statCap);
        }

        for (eProperty resist = eProperty.Resist_First; resist <= eProperty.Resist_Last; resist++)
        {
            ItemBonus[resist] = Math.Max(ItemBonus[resist], resistCap);
        }

        ItemBonus[eProperty.MaxHealth] = Math.Max(ItemBonus[eProperty.MaxHealth], hitsCap);
    }

    /// <summary>Every champion is of the SmuG guild (GameBot.AddToWorld would show its own bot guild otherwise).</summary>
    public override string GuildName
    {
        get { return GauntletRules.GuildName; }
        set { base.GuildName = GauntletRules.GuildName; }
    }

    /// <summary>
    /// An attack by the challenger's side starts the fight, bow or no bow. A styled Numb hit is noted with the stun spells
    /// it procs (GameLiving.OnAttackedByEnemy runs for every attack received), so the purge can tell Numb's stun apart.
    /// </summary>
    public override void OnAttackedByEnemy(AttackData ad)
    {
        if (!Engaged && ad != null && IsChallengerSide(ad.Attacker))
        {
            Engaged = true;

            // A hard winner's two-champion round: its partner joins the fight too.
            GauntletManager.OnChampionEngaged(this);

            // The challenger was frozen until the fight began (GauntletManager.AwaitingBow).
            Challenger?.Out.SendUpdateMaxSpeed();
        }

        if (ad != null && ad.Target == this && ad.AttackResult == eAttackResult.HitStyle && ad.Style?.Name == NumbStyleName)
        {
            LastNumbTick = GameLoop.GameLoopTime;

            foreach (StyleProcInfo proc in ad.Style.Procs)
            {
                if (proc.Spell != null && proc.Spell.SpellType == eSpellType.StyleStun)
                {
                    _numbStunSpellIds.Add(proc.Spell.ID);
                }
            }
        }

        base.OnAttackedByEnemy(ad);
    }

    /// <summary>
    /// The purge rule (owner 2026-10-27, AutonomousBotRealmAbilities.Wanted): any mez, or any stun but Numb's, is
    /// purged at once; Numb's stun (Shields, about 2 s) only on a coin toss made once for that stun. A stun is Numb's when
    /// its spell is one Numb procs and a Numb hit came just before it.
    /// </summary>
    internal bool WantsPurge()
    {
        bool numbStun = false;

        if (IsStunned && !IsMezzed)
        {
            ECSGameEffect stun = EffectListService.GetEffectOnTarget(this, eEffect.Stun);
            int spellId = stun?.SpellHandler?.Spell?.ID ?? 0;
            numbStun = spellId != 0 && _numbStunSpellIds.Contains(spellId) && GameLoop.GameLoopTime - LastNumbTick <= NumbWindowMs;

            if (numbStun && stun != _numbStunTossed)
            {
                _numbStunTossed = stun;
                _numbStunPurges = Random.Shared.Next(2) == 0;
            }
        }

        return ChampionTactics.PurgeWanted(IsMezzed, IsStunned, numbStun, _numbStunPurges);
    }

    /// <summary>True while its Speed of Sound runs.</summary>
    internal bool HasSpeedOfSound => effectListComponent.ContainsEffectForEffectType(eEffect.SpeedOfSound);

    /// <summary>True when it has Speed of Sound and its timer is ready.</summary>
    internal bool SpeedOfSoundReady => GetAbility(SpeedOfSoundKey) is TimedRealmAbility ability && GetSkillDisabledDuration(ability) <= 0;

    /// <summary>
    /// Fires Toothmouth's Speed of Sound. AtlasOF_SpeedOfSound.Execute works on a GamePlayer only (it casts its user to
    /// one), so the champion starts the ability's timer and its effect itself; the effect's speed is applied by the
    /// brain (MaxSpeedCalculator never reads it for an NPC). False when it is not ready.
    /// </summary>
    internal bool TrySpeedOfSound()
    {
        if (GetAbility(SpeedOfSoundKey) is not TimedRealmAbility ability || GetSkillDisabledDuration(ability) > 0 || IsIncapacitated || HasSpeedOfSound)
        {
            return false;
        }

        ability.DisableSkill(this);
        ECSGameEffectFactory.Create(new(this, SpeedOfSoundMs, 1), static (in i) => new SpeedOfSoundECSEffect(i));

        return true;
    }

    /// <summary>
    /// Preesh's Triple Wield (owner 2026-10-28: when his parry stun lands), as TripleWieldAbilityHandler starts it: a 30 s
    /// effect (its extra body damage on hits is GameLiving's, for any attacker), then 7 minutes before the next. False
    /// when it is not ready.
    /// </summary>
    internal bool TryTripleWield()
    {
        long now = GameLoop.GameLoopTime;

        if (now < _nextTripleWieldAt || IsIncapacitated || !IsAlive || EffectListService.GetAbilityEffectOnTarget(this, eEffect.TripleWield) != null)
        {
            return false;
        }

        ECSGameEffectFactory.Create(new(this, TripleWieldMs, 1), static (in i) => new TripleWieldECSGameEffect(i));
        _nextTripleWieldAt = now + TripleWieldReuseMs;

        return true;
    }

    /// <summary>
    /// Preesh's Flurry on cooldown (owner 2026-10-28), as FlurryAbilityHandler deals it: slash damage of the target's
    /// maximum health / 100 x his Celtic Dual / 4.5 less its slash resists, within 135 units and in front of him; then
    /// 2 minutes before the next. False when it is not ready or the target is out of reach.
    /// </summary>
    internal bool TryFlurry(GamePlayer target)
    {
        long now = GameLoop.GameLoopTime;

        if (target == null || !target.IsAlive || now < _nextFlurryAt || IsIncapacitated || !IsAlive
            || !IsWithinRadius(target, FlurryRange) || !IsObjectInFront(target, 180))
        {
            return false;
        }

        int damage = (int) (target.MaxHealth / 100 * GetModifiedSpecLevel(Specs.Celtic_Dual) / 4.5);
        int resistModifier = (int) (damage * (double) target.GetResist(eDamageType.Slash) * -0.01);
        resistModifier += (int) ((damage + (double) resistModifier) * target.SpecBuffBonusCategory[eProperty.Resist_Slash] * -0.01);
        damage = Math.Max(0, damage + resistModifier);

        target.TakeDamage(this, eDamageType.Slash, damage, 0);

        foreach (GamePlayer viewer in target.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
        {
            viewer.Out.SendSpellEffectAnimation(this, target, 7103, 0, false, 0x01);
        }

        target.Out.SendMessage($"{GetName(0, true)} flurries you for {damage} damage!", PacketHandler.eChatType.CT_Damaged, PacketHandler.eChatLoc.CL_SystemWindow);
        _nextFlurryAt = now + FlurryReuseMs;

        return true;
    }

    /// <summary>Champions drop nothing (owner 2026-10-28).</summary>
    public override bool CanDropLoot => false;

    /// <summary>No corpse: GameNPC.ProcessDeath deletes the body, and RespawnInterval -1 keeps it gone.</summary>
    protected override bool RetainCorpseForResurrection => false;

    /// <summary>
    /// A champion kill gives the normal kill rewards (experience, realm points): GameNPC.RewardStatus denies every NPC with
    /// an IControlledBrain, and BotBrain is one, so it is allowed here.
    /// </summary>
    public override RewardEligibility RewardStatus => RewardEligibility.Eligible;

    public override void ProcessDeath(GameObject killer)
    {
        // An act boss's credit is decided before the base call, which clears the attacker list (as ExpeditionBoss does).
        GamePlayer owner = ActBoss ? Challenger : null;
        bool credited = owner != null && ObjectiveTracker.IsBossKillCredited(owner, killer, this);

        // Its pet (Gwyraan's) dies with it.
        ReleasePet();
        base.ProcessDeath(killer);

        // Only a real death counts as a kill; Remove() deletes without coming here.
        if (Health <= 0)
        {
            GauntletManager.OnChampionDied(this);
        }

        if (owner == null)
        {
            return;
        }

        try
        {
            ObjectiveTracker.OnBossDied(this, owner.ObjectId, credited);
        }
        catch (Exception ex)
        {
            ActBossLog.Error($"[EXPEDITION] Act boss champion death handling failed for {owner.Name}.", ex);
        }
    }

    private static readonly DOL.Logging.Logger ActBossLog = DOL.Logging.LoggerManager.Create(typeof(GauntletChampion));

    /// <summary>Takes the champion (and its pet) out of the world for good, without counting a kill.</summary>
    public void Remove()
    {
        Challenger = null;
        ReleasePet();

        if (ObjectState != eObjectState.Deleted)
        {
            Delete();
        }
    }

    /// <summary>Releases its pet through the summon's normal release (GameBot.CommandNpcRelease), which deletes it.</summary>
    private void ReleasePet()
    {
        if (ControlledBrain != null)
        {
            CommandNpcRelease();
        }
    }

    /// <summary>
    /// True for the challenger and anything the challenger controls (pets). An act boss also takes on whoever fights for
    /// the run's player (its group, companion bots and their pets: ObjectiveTracker.IsCreditedTo).
    /// </summary>
    public bool IsChallengerSide(GameLiving living)
    {
        GamePlayer challenger = Challenger;

        if (living == null || challenger == null)
        {
            return false;
        }

        if (living == challenger)
        {
            return true;
        }

        if (ActBoss && ObjectiveTracker.IsCreditedTo(challenger, living))
        {
            return true;
        }

        return living is GameNPC { Brain: IControlledBrain controlled } && controlled.GetPlayerOwner() == challenger;
    }

    /// <summary>
    /// The champion's look (ExpeditionBossLooks) on its real, generated gear. ExpeditionBossLooks.Apply replaces an
    /// NPC's inventory with a model-only template; a GameBot's inventory holds the stat items its combat reads (weapon
    /// type for styles, armor factor, bonuses), so the template's models are copied onto the real items and the real
    /// inventory is put back. The body model and size Apply sets are kept.
    /// </summary>
    private void ApplyLooks()
    {
        IGameInventory realInventory = Inventory;
        eActiveWeaponSlot realSlot = ActiveWeaponSlot;

        if (!ExpeditionBossLooks.Apply(this, Definition?.Name ?? Name))
        {
            Inventory = realInventory;

            return;
        }

        IGameInventory looks = Inventory;
        Inventory = realInventory;

        // The hooded cloak (ExpeditionBossLooks): a bot's generated gear has no cloak, so it gets a plain one to wear.
        if (realInventory != null && realInventory.GetItem(eInventorySlot.Cloak) == null)
        {
            DbItemTemplate cloak = new()
            {
                Id_nb = "gauntlet_champion_cloak",
                Name = "Champion's Cloak",
                Level = Level,
                Item_Type = (int) eInventorySlot.Cloak,
                Object_Type = (int) eObjectType.Magical,
                Model = ExpeditionBossLooks.CloakModel
            };
            realInventory.AddItem(eInventorySlot.Cloak, GameInventoryItem.Create(cloak));
        }

        if (realInventory != null && looks != null)
        {
            foreach (DbInventoryItem look in looks.VisibleItems.ToList())
            {
                DbInventoryItem item = realInventory.GetItem((eInventorySlot) look.SlotPosition);

                if (item != null && look.Model > 0)
                {
                    // Its own copy of the template: the generated item may share a cached template with other items.
                    DbItemTemplate own = new(item.Template);
                    own.Model = look.Model;
                    item.Template = own;
                }
            }
        }

        // Apply switched to the template's slot; return to the slot the real gear was equipped for. GameBot's
        // SwitchWeapon refreshes the item bonuses.
        SwitchWeapon(realSlot);
    }
}

/// <summary>
/// The champion's brain: the bot's own combat AI (styles, spells, heals) with these changes. It buffs before it can be
/// challenged and waits; it fights only its challenger's side and keeps the challenger on its aggro list (BotBrain's
/// AGGRO state drops out after 6 s without combat); and it is leashed to where it appeared (BotBrain ignores
/// TetherRange). In the fight it plays its <see cref="ChampionSignature"/>: the positional steps (behind, side),
/// Drunkstains's shield/polearm swap and shield stuns, and the manoeuvres that own the think while they run (Ios's
/// ranged opener and kite, Gwyraan's root escape, Toothmouth's mez and retreats).
/// </summary>
public sealed class GauntletChampionBrain : BotBrain
{
    /// <summary>The aggro the challenger is topped up to every think.</summary>
    private const long ChallengerAggro = 10_000;

    /// <summary>While waiting, a champion moved further than twice this from its spot walks back.</summary>
    private const int CentreReachedDistance = 150;

    /// <summary>How often a champion hunting a stealthed challenger re-plots its walk.</summary>
    private const long HuntRepathMs = 3_000;

    /// <summary>How far from the challenger a behind or side spot is: inside melee range (~128), outside the 32 units
    /// where GameObject.IsObjectInFront is always true.</summary>
    private const int SpotDistance = 70;

    /// <summary>Close enough to a spot to stop and face the challenger.</summary>
    private const int ArrivedDistance = 25;

    /// <summary>The behind attempt starts only this close to the challenger.</summary>
    private const int BehindStartRange = 500;

    /// <summary>A challenger whose facing is within this many degrees of the champion gets the run through.</summary>
    private const float RunThroughMaxAngle = 30;

    /// <summary>The run through stops this far past the challenger (inside IsObjectInFront's 32 always-in-front units).</summary>
    private const int RunThroughStopPast = 20;

    /// <summary>
    /// While a run through waits for its moment the brain thinks every loop tick: NpcService schedules the next think at
    /// GameLoopTime + ThinkInterval and ShouldTick's half-tick tolerance makes 1 ms the next tick.
    /// </summary>
    private const int RunThroughThinkMs = 1;

    /// <summary>The think pace while a manoeuvre (Ios's opener or kite, Gwyraan's escape, Toothmouth's mez) runs.</summary>
    private const int ManoeuvreThinkMs = 200;

    /// <summary>Meanwhile the bot's own fight (BotBrain.AttackMostWanted) runs at its usual pace.</summary>
    private const long PressureThinkMs = 500;

    /// <summary>The wait after a landed back stun before the next attempt (owner 2026-10-25: "e.g. every 20 s").</summary>
    private const long BehindRetryMs = 20_000;

    /// <summary>How long the side chain may take.</summary>
    private const long SideStepMs = 12_000;

    /// <summary>A stun with less than this left is not worth walking to the side for.</summary>
    private const long MinStunForSideMs = 2_000;

    /// <summary>Drunkstains takes up the polearm this long before a swing is due, on top of a think interval.</summary>
    private const long SwapMarginMs = 150;

    /// <summary>Gwyraan's DoT is for a challenger beyond melee reach: the attack range plus this margin.</summary>
    private const int DotMeleeMargin = 50;

    /// <summary>A signature cast (DoT, self-buff) is tried again no sooner than this after the last one ended.</summary>
    private const long SpellRetryMs = 4_000;

    /// <summary>An instant mez or root is given this long to show on the challenger before it counts as resisted.</summary>
    private const long LandWindowMs = 1_500;

    /// <summary>Toothmouth retreats this share of the leash radius past his spot (owner: "~80% of the leash").</summary>
    private const double RetreatLeashShare = 0.8;

    /// <summary>Ios and Gwyraan stop running this far from the challenger to cast (inside their 1,500 spell range).</summary>
    private const int FleeDistance = 900;

    /// <summary>Ios's ranged opener walks in to this far inside her lifedrain's range.</summary>
    private const int OpenerRangeMargin = 100;

    /// <summary>A retreat or kite is given up after this long.</summary>
    private const long ManoeuvreMaxMs = 20_000;

    /// <summary>Ios's kite fails when the challenger stays in melee range this long while she runs.</summary>
    private const long KiteCaughtMs = 4_000;

    /// <summary>A champion slower than this (rooted, or nearly) cannot run away.</summary>
    private const int MinRunSpeed = 50;

    /// <summary>A pre-fight buff that was cast this often without showing is skipped.</summary>
    private const int PrefightMaxAttempts = 2;

    private enum SignatureStep
    {
        None,
        Behind,
        Side
    }

    private enum KitePhase
    {
        None,
        Run,
        Drain
    }

    private enum EscapePhase
    {
        None,
        Rooting,
        Run,
        Heal,
        Dot
    }

    private enum MezPurpose
    {
        None,
        Positional,
        Retreat
    }

    private long _nextHuntTick;

    // An act boss out of a fight (ThinkActBossIdle).
    private bool _actBossIdle;
    private SignatureStep _step;
    private long _stepUntil;
    private long _nextBehindTick;
    private bool _sideDone;
    private bool _behindRunThrough;
    private long _runSwingTick;
    private long _nextPressureTick;
    private int _thinkIntervalBeforeRun;
    private double _speedFactor = 1;
    private short _speedBase;
    private long _nextDotTick;
    private long _nextBuffTick;
    private readonly Dictionary<(int SpellId, GameLiving Target), int> _prefightAttempts = new();

    // Ios.
    private bool _rangedOpenerDone;
    private bool _openerSnareTried;

    // The opener's last logged state (GAUNTLET_DRAIN, logged once per change) and when its current wait began.
    private string _openerState;
    private long _openerWaitSince;

    /// <summary>How long the opener may wait on a cast (or a stop) that does not come before she fights in melee instead.</summary>
    private const int OpenerStuckMs = 4000;
    private long _drainCastTick;

    // Ios's drain casts in a row that did not start (owner 2026-10-28: one refused cast ended her opener or kite).
    private int _drainFailures;
    private const int MaxDrainFailures = 3;
    private static readonly DOL.Logging.Logger DrainLog = DOL.Logging.LoggerManager.Create(typeof(GauntletChampionBrain));
    private bool _kiteUsed;
    private KitePhase _kitePhase;
    private long _kiteStartTick;
    private long _kiteCaughtSince;

    // Gwyraan.
    private bool _dotOpenerDone;
    private EscapePhase _escapePhase;
    private long _escapeTick;
    private bool _escapeHealStarted;

    // Toothmouth (and the retreat point Ios and Gwyraan run to).
    private MezPurpose _mezPurpose;
    private long _mezCastTick;
    private bool _positionalMez;

    private bool _retreating;
    private bool _retreatBySpeedOfSound;
    private long _retreatStartTick;
    private Point3D _retreatPoint;

    // Drunkstains.
    private string _shieldStunChoice;
    private bool _shieldStunOpenerDone;
    private AttackData _lastShieldStunAttack;

    // The styled hit that last triggered Preesh's Triple Wield, so one landing counts once.
    private AttackData _lastTripleWieldTrigger;

    public override void Think()
    {
        if (Body is not GauntletChampion champion || !champion.IsAlive)
        {
            base.Think();

            return;
        }

        GamePlayer challenger = champion.Challenger;
        long now = GameLoop.GameLoopTime;

        if (!champion.Engaged)
        {
            ThinkBeforeFight(champion, challenger, now);

            return;
        }

        // Purge, Ignore Pain, Second Wind as the fight calls for them (rate-limited inside).
        AutonomousBotRealmAbilities.UseActives(champion);

        if (champion.IsOutOfTetherRange)
        {
            // Leash: drop the fight and go back to its spot.
            ResetFight(champion);
            ClearAggroList();
            champion.StopAttack();
            champion.TargetObject = null;
            FSM.SetCurrentState(eFSMStateType.IDLE);

            if (!champion.IsReturningToSpawnPoint)
            {
                champion.ReturnToSpawnPoint(champion.MaxSpeed);
            }

            return;
        }

        if (champion.IsReturningToSpawnPoint)
        {
            return;
        }

        bool challengerHere = challenger != null && challenger.IsAlive &&
            challenger.ObjectState == GameObject.eObjectState.Active && challenger.CurrentRegion == champion.CurrentRegion;

        // An act boss (owner 2026-10-29) notices the run's player as a boss does: within the boss's aggro range (not through
        // stealth), or once the player is on its aggro list (an attack, or the fight going on).
        if (champion.ActBoss)
        {
            challengerHere = challengerHere && (GetBaseAggroAmount(challenger) > 0 ||
                !challenger.IsStealthed && champion.IsWithinRadius(challenger, ExpeditionBoss.BossAggroRange));

            if (ThinkActBossIdle(champion, challengerHere))
            {
                return;
            }
        }

        // A manoeuvre (Ios's ranged opener or kite, Gwyraan's escape, Toothmouth's mez or retreat) owns the think while it
        // runs: no BotBrain fight moves the champion or hits the challenger meanwhile.
        if (ThinkManoeuvre(champion, challengerHere ? challenger : null, now))
        {
            // A bot in combat thinks about every 1.5 s (BotBrain sets it again on its next own think), so a stop, the snare
            // and the drain took a think each (owner 2026-10-28: Ios "runs up, stops, does nothing for 3 seconds").
            ThinkInterval = ManoeuvreThinkMs;

            return;
        }

        // Confusion and similar effects can add others directly; only the challenger's side stays.
        foreach (GameLiving living in AggroList.Keys.ToList())
        {
            if (!champion.IsChallengerSide(living))
            {
                RemoveFromAggroList(living);
            }
        }

        if (challengerHere)
        {
            if (challenger.IsStealthed && GetBaseAggroAmount(challenger) <= 0)
            {
                // It cannot see a hidden challenger, so it hunts where they are; GamePlayer.UncoverStealthAction's
                // normal rules reveal them once it is close.
                if (now >= _nextHuntTick && !champion.ActBoss)
                {
                    _nextHuntTick = now + HuntRepathMs;
                    champion.PathTo(new Point3D(challenger.X, challenger.Y, challenger.Z), (short) (champion.MaxSpeed / 2));
                }
            }
            else
            {
                long current = GetBaseAggroAmount(challenger);

                if (current < ChallengerAggro)
                {
                    AddToAggroList(challenger, ChallengerAggro - current);
                }

                if (FSM.GetCurrentState()?.StateType != eFSMStateType.AGGRO)
                {
                    FSM.SetCurrentState(eFSMStateType.AGGRO);
                }
            }
        }

        base.Think();
    }

    /// <summary>
    /// An act boss out of a fight (nobody on its aggro list, the run's player not noticed): it ends whatever manoeuvre was
    /// running, keeps its pre-fight buffs (and Gwyraan his pet) up as a waiting champion does, and walks back to where it
    /// appeared. True while this owns the think.
    /// </summary>
    private bool ThinkActBossIdle(GauntletChampion champion, bool challengerNoticed)
    {
        if (challengerNoticed || HasAggro)
        {
            _actBossIdle = false;

            return false;
        }

        if (!_actBossIdle)
        {
            _actBossIdle = true;
            ResetFight(champion);
            champion.StopAttack();
            champion.TargetObject = null;
        }

        if (PrefightStep(champion))
        {
            return true;
        }

        base.Think();

        if (!champion.IsMoving && !champion.IsWithinRadius(champion.SpawnPoint, CentreReachedDistance * 2))
        {
            champion.WalkTo(champion.SpawnPoint, champion.MaxSpeed);
        }

        return true;
    }

    /// <summary>
    /// Before the bow (owner 2026-10-27): it buffs where it appeared, and only once every pre-fight buff it can cast is
    /// up (Gwyraan's pet summoned and buffed too), and at least <see cref="GauntletRules.ChampionBuffSeconds"/> have
    /// passed, does it announce that it awaits the challenge (GauntletManager.OnChampionReady; /bow is refused until
    /// then). Then it waits there, buffs kept up, facing the challenger.
    /// </summary>
    private void ThinkBeforeFight(GauntletChampion champion, GamePlayer challenger, long now)
    {
        bool casting = PrefightStep(champion);

        if (!champion.AtCentre)
        {
            bool busy = casting || champion.IsCasting || champion.castingComponent?.HasPendingSkillRequests == true;

            if (ChampionTactics.PrefightDone(now, champion.BuffUntilTick, champion.PrefightDeadlineTick, busy, NextPrefightCast(champion).Spell != null))
            {
                // It stays where it appeared (owner 2026-10-25: no walk to the middle) and waits there.
                champion.StopMoving();
                champion.AtCentre = true;
                GauntletManager.OnChampionReady(champion);

                return;
            }

            if (!busy)
            {
                // AddToAggroList refuses everyone until the fight starts, so the idle state's own turn pulls nothing.
                base.Think();
            }

            return;
        }

        // Waiting to be challenged: buffs kept up, facing the challenger, back to its spot if anything moved it.
        if (casting)
        {
            return;
        }

        base.Think();

        if (!champion.IsMoving && !champion.IsWithinRadius(champion.SpawnPoint, CentreReachedDistance * 2))
        {
            champion.WalkTo(champion.SpawnPoint, champion.MaxSpeed);
        }
        else if (!champion.IsMoving && challenger != null && challenger.CurrentRegion == champion.CurrentRegion)
        {
            champion.TurnTo(challenger);
        }
    }

    /// <summary>
    /// One pre-fight cast: Gwyraan's pet first, then each pre-fight buff missing on the champion, or on its pet when the
    /// buff can land on others. True while a cast is on its way (this one or an earlier one).
    /// </summary>
    private bool PrefightStep(GauntletChampion champion)
    {
        if (champion.IsCasting || champion.castingComponent?.HasPendingSkillRequests == true)
        {
            return true;
        }

        (Spell spell, GameLiving target) = NextPrefightCast(champion);

        if (spell == null)
        {
            return false;
        }

        if (spell != champion.SignaturePet)
        {
            // Every buff missing on the champion and its pet lands at once, without casting (owner 2026-10-28: "need to
            // speed up gwyraan's buff phase"). An effect shows from the next tick, so this pass skips what it applied.
            HashSet<(int, GameLiving)> applied = new();

            while (spell != null && spell != champion.SignaturePet)
            {
                (int, GameLiving) buffKey = (spell.ID, target);
                applied.Add(buffKey);
                _prefightAttempts[buffKey] = Attempts(spell, target) + 1;
                ScriptMgr.CreateSpellHandler(champion, spell, m_mobSpellLine)?.StartSpell(target);
                (spell, target) = NextPrefightCast(champion, applied);
            }

            return false;
        }

        (int, GameLiving) key = (spell.ID, target);
        _prefightAttempts[key] = Attempts(spell, target) + 1;
        GameObject previousTarget = champion.TargetObject;
        champion.TargetObject = target;

        if (champion.CastSpell(spell, m_mobSpellLine))
        {
            if (spell == champion.SignaturePet)
            {
                // The pet stays out of everything until the fight: it follows and only defends on order.
                _prefightAttempts.Remove(key);
            }

            return true;
        }

        // Refused (power, range, concentration...): not tried again.
        _prefightAttempts[key] = PrefightMaxAttempts;
        champion.TargetObject = previousTarget;

        return false;
    }

    /// <summary>The next pre-fight cast and its target, or (null, null) when every castable one is up.</summary>
    /// <param name="skip">Casts already made this think (their effects show from the next tick).</param>
    private (Spell Spell, GameLiving Target) NextPrefightCast(GauntletChampion champion, HashSet<(int, GameLiving)> skip = null)
    {
        GameNPC pet = champion.ControlledBrain?.Body;
        bool petUp = pet != null && pet.IsAlive && pet.ObjectState == GameObject.eObjectState.Active;

        if (champion.SignaturePet != null && !petUp && Castable(champion, champion.SignaturePet) &&
            Attempts(champion.SignaturePet, champion) < PrefightMaxAttempts)
        {
            return (champion.SignaturePet, champion);
        }

        foreach (Spell spell in champion.PrefightBuffs)
        {
            if (!Castable(champion, spell) || !champion.CanAffordConcentration(spell))
            {
                continue;
            }

            if (!LivingHasEffect(champion, spell) && Attempts(spell, champion) < PrefightMaxAttempts && skip?.Contains((spell.ID, champion)) != true)
            {
                return (spell, champion);
            }

            if (petUp && spell.Target is eSpellTarget.REALM or eSpellTarget.GROUP or eSpellTarget.PET && champion.IsWithinRadius(pet, Math.Max(spell.Range, 500)) &&
                !LivingHasEffect(pet, spell) && Attempts(spell, pet) < PrefightMaxAttempts && skip?.Contains((spell.ID, pet)) != true)
            {
                return (spell, pet);
            }
        }

        return (null, null);
    }

    /// <summary>How often this pre-fight cast was started on this target.</summary>
    private int Attempts(Spell spell, GameLiving target)
    {
        return _prefightAttempts.TryGetValue((spell.ID, target), out int attempts) ? attempts : 0;
    }

    /// <summary>A spell the champion has the power for and whose timer is ready, and nothing else is being cast.</summary>
    private static bool Castable(GauntletChampion champion, Spell spell)
    {
        return spell != null && champion.Mana >= champion.PowerCost(spell) && champion.GetSkillDisabledDuration(spell) <= 0 &&
            !champion.IsSilenced && !champion.IsIncapacitated;
    }

    /// <summary>Casts a spell on a target when <see cref="Castable"/> and nothing else is being cast.</summary>
    private bool TryCastOn(GauntletChampion champion, Spell spell, GameLiving target)
    {
        if (target == null || !Castable(champion, spell) || champion.IsCasting || champion.castingComponent?.HasPendingSkillRequests == true)
        {
            return false;
        }

        GameObject previousTarget = champion.TargetObject;
        champion.TargetObject = target;

        if (champion.CastSpell(spell, m_mobSpellLine))
        {
            return true;
        }

        champion.TargetObject = previousTarget;

        return false;
    }

    /// <summary>The leash or a lost challenger: every step and manoeuvre ends, the pet comes back.</summary>
    private void ResetFight(GauntletChampion champion)
    {
        _step = SignatureStep.None;
        EndRunThrough();
        champion.CurrentPriority = null;
        champion.HeldSlot = null;
        _kitePhase = KitePhase.None;
        _escapePhase = EscapePhase.None;
        _retreating = false;
        _mezCastTick = 0;
        _mezPurpose = MezPurpose.None;
        _positionalMez = false;
        ApplySpeed(champion, 1);
        PetStandDown(champion);
    }

    public override void AddToAggroList(GameLiving living, long aggroAmount)
    {
        if (Body is not GauntletChampion champion)
        {
            base.AddToAggroList(living, aggroAmount);

            return;
        }

        if (!champion.IsChallengerSide(living))
        {
            return;
        }

        // Before the bow (or an attack, GauntletChampion.OnAttackedByEnemy) it never takes anyone on.
        if (!champion.Engaged)
        {
            return;
        }

        base.AddToAggroList(living, aggroAmount);
    }

    /// <summary>
    /// The fight (BotBrain's AGGRO state): the signature step first. A behind or side step moves the champion to its
    /// spot instead of the bot's melee follow; the styles are picked at each swing by BotMeleeStylePolicy.Select from
    /// <see cref="GauntletChampion.PriorityStyles"/>, so a positional lands at the first swing StyleProcessor accepts.
    /// Out of melee range it sprints to close (owner 2026-10-27: a caster or archer challenger is run down).
    /// </summary>
    public override void AttackMostWanted()
    {
        if (Body is not GauntletChampion champion || champion.Signature == null)
        {
            base.AttackMostWanted();

            return;
        }

        long now = GameLoop.GameLoopTime;
        GamePlayer challenger = champion.Challenger;
        bool challengerHere = challenger != null && challenger.IsAlive &&
            challenger.ObjectState == GameObject.eObjectState.Active && challenger.CurrentRegion == champion.CurrentRegion;
        UpdateStep(champion, challengerHere ? challenger : null, now);

        if (champion.Signature.MezRetreat)
        {
            // Toothmouth fights with his mace (owner 2026-10-28). His flute (his pre-fight songs, or BotBrain equipping it for
            // a song) has no swing speed; with the slot held, a switch to it stays on the mace.
            champion.HeldSlot = eActiveWeaponSlot.Standard;

            if (champion.ActiveWeaponSlot != eActiveWeaponSlot.Standard)
            {
                champion.SwitchWeapon(eActiveWeaponSlot.Standard);
            }
        }

        if (challengerHere)
        {
            InstantSpells(champion, challenger);

            if (champion.Signature.RangedDot)
            {
                PetAttack(champion, challenger);
            }
        }

        bool inMelee = challengerHere && champion.IsWithinRadius(challenger, champion.attackComponent.AttackRange);

        if (_step == SignatureStep.Behind && !champion.Signature.MezRetreat)
        {
            // Ios's back stun (owner 2026-10-27): a big speed boost until it lands or the attempt is dropped.
            ApplySpeed(champion, ChampionTactics.BackStunSpeedFactor(true));
        }
        else
        {
            // No sprint otherwise (owner 2026-10-28): only kiting away sprints (KiteSprintFactor).
            ApplySpeed(champion, 1);
        }

        if (_step == SignatureStep.Behind && _behindRunThrough && !champion.IsIncapacitated)
        {
            RunThrough(champion, challenger, now);

            return;
        }

        if (_step != SignatureStep.None && !champion.IsIncapacitated)
        {
            MoveForStep(champion, challenger);

            return;
        }

        base.AttackMostWanted();

        if (champion.Signature.ShieldStun)
        {
            HoldShieldOrPolearm(champion, challengerHere ? challenger : null, now);
        }
    }

    /// <summary>
    /// The instant spells a champion weaves into its melee (owner 2026-10-27): Toothmouth's shout between his swings
    /// whenever it is ready (never on a mezzed challenger: it would break his own mez), and Ios's instant snare on a
    /// casting challenger (any harmful spell interrupts a cast).
    /// </summary>
    private void InstantSpells(GauntletChampion champion, GamePlayer challenger)
    {
        if (champion.IsCasting || champion.castingComponent?.HasPendingSkillRequests == true)
        {
            return;
        }

        Spell shout = champion.SignatureShout;

        if (shout != null && !challenger.IsMezzed && champion.IsWithinRadius(challenger, shout.Range))
        {
            TryCastOn(champion, shout, challenger);

            return;
        }

        Spell snare = champion.SignatureSnare;

        if (snare != null && _rangedOpenerDone && challenger.IsCasting && champion.IsWithinRadius(challenger, snare.Range) &&
            !challenger.effectListComponent.ContainsEffectForEffectType(eEffect.SnareImmunity))
        {
            TryCastOn(champion, snare, challenger);
        }
    }

    /// <summary>True when the challenger is immune to stuns: the same test as CCSpellHandler.CheckSpellResist, which
    /// StyleStun (a StunSpellHandler) goes through: the StunImmunity effect a stun leaves on a player when it ends
    /// (StyleStun.OnEffectExpires: five times the stun), or the StunImmunity ability.</summary>
    private static bool StunImmune(GameLiving living)
    {
        return living.effectListComponent.ContainsEffectForEffectType(eEffect.StunImmunity) || living.HasAbility(Abilities.StunImmunity);
    }

    /// <summary>
    /// The signature step and the styles it wants first.
    /// Side: while the challenger is stunned (Ios's Damaging Grasp, Preesh's Devastating Blow, or any other stun) it
    /// stands at their side for the side chain; a stunned player cannot turn, so the side stays the side.
    /// Behind (Ios, owner 2026-10-27): once the challenger is within reach and not stunned, immune or mezzed, she goes for
    /// Damaging Grasp and keeps going (ChampionTactics.NextBackStunMove) until it lands: a run through timed to her swing
    /// against a challenger who faces her (<see cref="RunThrough"/>), otherwise the walk to the spot behind them, again
    /// after a miss or a turn. Then not again for 20 s.
    /// Behind (Toothmouth): while his positional mez holds, he walks behind the challenger for Back Crush.
    /// Otherwise its priority (ChampionTactics.BasePriority: the immunity window has its own).
    /// </summary>
    private void UpdateStep(GauntletChampion champion, GamePlayer challenger, long now)
    {
        ChampionSignature signature = champion.Signature;

        if (challenger == null)
        {
            _step = SignatureStep.None;
            EndRunThrough();
            champion.CurrentPriority = null;

            return;
        }

        bool stunned = challenger.IsStunned;
        bool stunImmune = StunImmune(challenger);
        AttackData last = champion.attackComponent.attackAction.LastAttackData;
        string lastStyle = last != null && last.AttackResult == eAttackResult.HitStyle ? last.Style?.Name : null;
        int chainIndex = lastStyle == null ? -1 : Array.IndexOf(signature.SideChain, lastStyle);

        // Preesh (owner 2026-10-28): Triple Wield once his parry chain's stun lands, and Flurry whenever it is ready.
        if (signature.BladeWork)
        {
            if (last != null && last != _lastTripleWieldTrigger && lastStyle == GauntletChampion.TripleWieldTriggerStyle && stunned)
            {
                _lastTripleWieldTrigger = last;
                champion.TryTripleWield();
            }

            champion.TryFlurry(challenger);
        }
        float championAngle = GauntletPositioning.AngleFrom(challenger.X, challenger.Y, challenger.Heading, champion.X, champion.Y);
        bool facesChampion = championAngle <= RunThroughMaxAngle || championAngle >= 360 - RunThroughMaxAngle;
        bool championBehind = GauntletPositioning.Classify(championAngle) == TargetArc.Back;

        if (!stunned)
        {
            _sideDone = false;
        }

        if (_step == SignatureStep.Side)
        {
            // A follow-up stays usable after its opener landed (StyleProcessor's Offensive check), stun or no stun.
            bool followUpPending = chainIndex >= 0 && chainIndex < signature.SideChain.Length - 1;
            bool chainDone = chainIndex == signature.SideChain.Length - 1;

            if (chainDone || now >= _stepUntil || !stunned && !followUpPending)
            {
                _step = SignatureStep.None;
                _sideDone = stunned;
            }
        }
        else if (_step == SignatureStep.Behind && signature.MezRetreat)
        {
            if (!challenger.IsMezzed || lastStyle == signature.BehindOpener)
            {
                _step = SignatureStep.None;
                _positionalMez = false;
            }
        }
        else if (_step == SignatureStep.Behind)
        {
            // A timed run is over two ticks after its swing; then the next move is chosen afresh.
            bool runPending = _runSwingTick > 0 && now <= _runSwingTick + (long) (2 * GameLoop.TickDuration);
            BackStunMove move = ChampionTactics.NextBackStunMove(lastStyle == signature.BehindOpener, stunned, stunImmune, challenger.IsMezzed,
                facesChampion, championBehind);

            if (move is BackStunMove.Landed or BackStunMove.Drop)
            {
                _step = SignatureStep.None;
                _nextBehindTick = move == BackStunMove.Landed ? now + BehindRetryMs : now;
            }
            else if (!runPending)
            {
                if (_runSwingTick > 0)
                {
                    EndRunThrough();
                }

                _behindRunThrough = move == BackStunMove.RunThrough;
            }
        }

        if (_step == SignatureStep.None)
        {
            long stunLeft = EffectListService.GetEffectOnTarget(challenger, eEffect.Stun)?.GetRemainingTimeForClient() ?? 0;

            if (signature.SideChain.Length > 0 && stunned && !_sideDone && stunLeft >= MinStunForSideMs)
            {
                _step = SignatureStep.Side;
                _stepUntil = now + SideStepMs;
            }
            else if (signature.MezRetreat)
            {
                if (_positionalMez && challenger.IsMezzed && signature.BehindOpener != null)
                {
                    _step = SignatureStep.Behind;
                    _behindRunThrough = false;
                }
                else if (!challenger.IsMezzed)
                {
                    // The mez broke before he got behind: the next ready mez may set up another.
                    _positionalMez = false;
                }
            }
            else if (signature.BehindOpener != null && !stunned && !stunImmune && !challenger.IsMezzed && now >= _nextBehindTick &&
                champion.IsWithinRadius(challenger, BehindStartRange))
            {
                // Owner 2026-10-25: no run for the back stun while the challenger is immune to stuns; and none on a
                // mezzed challenger (it would break the mez).
                _step = SignatureStep.Behind;
                _behindRunThrough = facesChampion && !championBehind;
                _runSwingTick = 0;
            }
        }

        if (_step != SignatureStep.Behind || !_behindRunThrough)
        {
            EndRunThrough();
        }

        IReadOnlyList<string> basePriority = ChampionTactics.BasePriority(signature, stunImmune);
        champion.CurrentPriority = _step switch
        {
            SignatureStep.Side => Enumerable.Reverse(signature.SideChain).Concat(basePriority).ToArray(),
            SignatureStep.Behind => new[] { signature.BehindOpener }.Concat(basePriority).ToArray(),
            _ => basePriority.ToArray()
        };
    }

    /// <summary>
    /// Moves to the step's spot (re-plotted every think as the challenger moves or turns): a straight walk, so from in
    /// front it runs through the challenger to their back. At the spot it stops and faces the challenger, as
    /// StyleProcessor's positional check also needs the champion to face its target. Toothmouth holds his swings until
    /// he is behind his mezzed challenger, so no swing from the front breaks the mez.
    /// </summary>
    private void MoveForStep(GauntletChampion champion, GamePlayer challenger)
    {
        bool holdSwings = champion.Signature.MezRetreat && _step == SignatureStep.Behind;
        champion.TargetObject = challenger;

        if (!holdSwings && !champion.attackComponent.AttackState)
        {
            // The attack starts at the attack component's next tick, with a melee follow; the next think walks again.
            champion.StartAttack(challenger);

            return;
        }

        ChampionSpot spot = _step == SignatureStep.Behind
            ? ChampionSpot.Behind
            : GauntletPositioning.NearestSide(challenger.X, challenger.Y, challenger.Heading, champion.X, champion.Y, SpotDistance);
        (int x, int y) = GauntletPositioning.SpotFor(challenger.X, challenger.Y, challenger.Heading, spot, SpotDistance);
        Point3D point = new(x, y, challenger.Z);
        champion.StopFollowing();

        if (!champion.IsWithinRadius(point, ArrivedDistance, true))
        {
            if (holdSwings && champion.attackComponent.AttackState)
            {
                champion.StopAttack();
            }

            champion.WalkTo(point, champion.MaxSpeed);

            return;
        }

        if (champion.IsMoving)
        {
            champion.StopMoving();
        }

        champion.TurnTo(challenger);

        if (holdSwings && !champion.attackComponent.AttackState)
        {
            champion.StartAttack(challenger);
        }
    }

    /// <summary>
    /// Sets the champion's speed to this factor of its own base speed (MaxSpeedBase, which MaxSpeedCalculator scales by
    /// the NPC's speed buffs and snares); 1 puts the base back.
    /// </summary>
    /// <summary>
    /// A champion kiting away (Ios's kite, Gwyraan's escape, Toothmouth's retreat) sprints like a player: MaxSpeedCalculator's
    /// sprint factor, applied to MaxSpeedBase since the calculator never sprints an NPC (owner 2026-10-28: only then).
    /// </summary>
    private const double KiteSprintFactor = 1.3;

    private void ApplySpeed(GauntletChampion champion, double factor)
    {
        if (Math.Abs(factor - _speedFactor) < 0.001)
        {
            return;
        }

        if (Math.Abs(_speedFactor - 1) < 0.001)
        {
            _speedBase = champion.MaxSpeedBase;
        }

        champion.MaxSpeedBase = Math.Abs(factor - 1) < 0.001 ? _speedBase : (short) Math.Round(_speedBase * factor);
        _speedFactor = factor;
    }

    /// <summary>
    /// Gwyraan's spells in the fight (owner 2026-10-27): his attack-spell turn (BotBrain.AttackMostWanted, and
    /// NpcAttackAction.PrepareMeleeAttack before every swing) is only his DoT, and never a crowd control. Heals and buffs
    /// stay the class's own (Defensive).
    /// </summary>
    public override bool CheckSpells(eCheckSpellType type)
    {
        if (Body is not GauntletChampion { Signature.RangedDot: true } champion || type == eCheckSpellType.Defensive)
        {
            return base.CheckSpells(type);
        }

        return type == eCheckSpellType.Offensive && CastSignatureDot(champion, GameLoop.GameLoopTime);
    }

    /// <summary>
    /// His DoT on the challenger: first as his opener wherever they stand (owner 2026-10-27: "Degeneration then melee"),
    /// then whenever they are beyond melee reach and do not carry it. A cast that ended (landed or interrupted) waits
    /// <see cref="SpellRetryMs"/> before the next try. Before the DoT, a missing damage add goes back up.
    /// </summary>
    private bool CastSignatureDot(GauntletChampion champion, long now)
    {
        if (champion.IsCasting || champion.castingComponent?.HasPendingSkillRequests == true)
        {
            _nextDotTick = now + SpellRetryMs;

            return true;
        }

        GamePlayer challenger = champion.Challenger;
        Spell dot = champion.SignatureDot;

        if (dot == null || challenger == null || !challenger.IsAlive || challenger.CurrentRegion != champion.CurrentRegion)
        {
            return false;
        }

        bool opener = !_dotOpenerDone && champion.IsWithinRadius(challenger, dot.Range);

        if (!opener && champion.IsWithinRadius(challenger, champion.attackComponent.AttackRange + DotMeleeMargin))
        {
            return false;
        }

        if (MaintainSignatureBuff(champion, now))
        {
            return true;
        }

        if (now < _nextDotTick || LivingHasEffect(challenger, dot))
        {
            _dotOpenerDone = _dotOpenerDone || LivingHasEffect(challenger, dot);

            return false;
        }

        _nextDotTick = now + SpellRetryMs;

        if (TryCastOn(champion, dot, challenger))
        {
            _dotOpenerDone = true;

            return true;
        }

        return false;
    }

    /// <summary>Casts the champion's signature self-buff (Gwyraan's best damage add) when it is missing.</summary>
    private bool MaintainSignatureBuff(GauntletChampion champion, long now)
    {
        Spell buff = champion.SignatureBuff;

        if (buff == null || now < _nextBuffTick || champion.IsCasting || champion.castingComponent?.HasPendingSkillRequests == true ||
            LivingHasEffect(champion, buff))
        {
            return false;
        }

        _nextBuffTick = now + SpellRetryMs;

        return TryCastOn(champion, buff, champion);
    }

    private bool ThinkManoeuvre(GauntletChampion champion, GamePlayer challenger, long now)
    {
        ChampionSignature signature = champion.Signature;

        if (signature == null)
        {
            return false;
        }

        if (signature.DrainKite)
        {
            return ThinkIos(champion, challenger, now);
        }

        if (signature.RangedDot)
        {
            return ThinkGwyraan(champion, challenger, now);
        }

        if (signature.MezRetreat)
        {
            return ThinkToothmouth(champion, challenger, now);
        }

        return false;
    }

    #region Ios

    /// <summary>
    /// Ios (owner 2026-10-27): her ranged opener, then her melee (the back stun, side chain and fire chain in
    /// <see cref="UpdateStep"/>); once, at 40% health, the kite (ChampionTactics.IosLowHealth): she snares the
    /// challenger, runs off and drains them, or, when she cannot (rooted, out of power), uses Ignore Pain and fights on.
    /// </summary>
    private bool ThinkIos(GauntletChampion champion, GamePlayer challenger, long now)
    {
        if (challenger == null)
        {
            _rangedOpenerDone = true;
            EndKite(champion);

            return false;
        }

        if (!_rangedOpenerDone)
        {
            if (ThinkRangedOpener(champion, challenger, now))
            {
                return true;
            }

            _rangedOpenerDone = true;
            _drainCastTick = 0;
            _drainFailures = 0;
        }

        if (_kitePhase != KitePhase.None)
        {
            if (ThinkKite(champion, challenger, now))
            {
                return true;
            }

            EndKite(champion);

            return false;
        }

        bool canKite = champion.SignatureDrain != null && Castable(champion, champion.SignatureDrain) && champion.MaxSpeed >= MinRunSpeed;
        LowHealthAction action = ChampionTactics.IosLowHealth(champion.HealthPercent, _kiteUsed, canKite);

        if (action == LowHealthAction.None)
        {
            return false;
        }

        _kiteUsed = true;

        if (action == LowHealthAction.IgnorePain)
        {
            AutonomousBotRealmAbilities.TryUse(champion, AutonomousBotRealmAbilities.IgnorePain, AutonomousBotRealmAbilities.IgnorePainTank);

            return false;
        }

        // The kite: snare (an instant, when the challenger can be snared), then run.
        _kitePhase = KitePhase.Run;
        _kiteStartTick = now;
        _kiteCaughtSince = 0;
        _drainCastTick = 0;
        _drainFailures = 0;
        champion.StopAttack();
        champion.StopFollowing();
        _retreatPoint = RetreatPointFrom(champion, challenger);

        if (!challenger.effectListComponent.ContainsEffectForEffectType(eEffect.SnareImmunity) && champion.IsWithinRadius(challenger, champion.SignatureSnare?.Range ?? 0))
        {
            TryCastOn(champion, champion.SignatureSnare, challenger);
        }

        return true;
    }

    /// <summary>
    /// The ranged opener (owner 2026-10-27): Veil Path, then Cycle of Death again and again, until the challenger is in
    /// melee range or her cast is interrupted (or she cannot cast: out of power); out of range she first walks in. False when
    /// it is over.
    /// </summary>
    private bool ThinkRangedOpener(GauntletChampion champion, GamePlayer challenger, long now)
    {
        Spell drain = champion.SignatureDrain;

        if (drain == null)
        {
            OpenerState(champion, null, "over: she has no drain spell");

            return false;
        }

        if (champion.IsWithinRadius(challenger, champion.attackComponent.AttackRange + DotMeleeMargin))
        {
            OpenerState(champion, drain, "over: the challenger is in melee range");

            return false;
        }

        if (champion.IsCasting || champion.castingComponent?.HasPendingSkillRequests == true)
        {
            string casting = champion.castingComponent?.SpellHandler?.Spell?.Name ?? "a pending request";

            if (OpenerWaitedTooLong(champion, drain, $"waiting on {casting}", now))
            {
                return false;
            }

            return true;
        }

        if (DrainInterrupted(champion, drain, now))
        {
            return false;
        }

        champion.StopAttack();
        champion.StopFollowing();

        if (!champion.IsWithinRadius(challenger, drain.Range - OpenerRangeMargin))
        {
            // Out of her spell range (the challenger bows from about 1,700 away): she walks in until she can cast.
            OpenerState(champion, drain, "walking into range");
            _openerWaitSince = 0;
            ApplySpeed(champion, 1);
            champion.PathTo(new Point3D(challenger.X, challenger.Y, challenger.Z), champion.MaxSpeed);

            return true;
        }

        ApplySpeed(champion, 1);

        // Stopped before she casts: a cast asked for while still moving can fail to start.
        if (champion.IsMoving)
        {
            if (OpenerWaitedTooLong(champion, drain, "stopping", now))
            {
                return false;
            }

            champion.StopMoving();
            champion.TurnTo(challenger);

            return true;
        }

        _openerWaitSince = 0;
        champion.TurnTo(challenger);

        if (!_openerSnareTried)
        {
            _openerSnareTried = true;

            if (!challenger.effectListComponent.ContainsEffectForEffectType(eEffect.SnareImmunity) &&
                champion.IsWithinRadius(challenger, champion.SignatureSnare?.Range ?? 0) &&
                TryCastOn(champion, champion.SignatureSnare, challenger))
            {
                OpenerState(champion, drain, $"snare {champion.SignatureSnare.Name} cast");

                return true;
            }
        }

        OpenerState(champion, drain, "casting the drain");

        return CastDrain(champion, challenger, drain, now) != true;
    }

    /// <summary>Logs the opener's state when it changes (GAUNTLET_DRAIN).</summary>
    private void OpenerState(GauntletChampion champion, Spell drain, string state)
    {
        if (state == _openerState)
        {
            return;
        }

        _openerState = state;
        LogDrain(champion, drain, $"opener: {state}");
    }

    /// <summary>
    /// True when the opener has waited on the same thing (a cast that does not end, a stop that does not happen) for
    /// <see cref="OpenerStuckMs"/>: she gives it up and fights in melee rather than stand there.
    /// </summary>
    private bool OpenerWaitedTooLong(GauntletChampion champion, Spell drain, string state, long now)
    {
        OpenerState(champion, drain, state);

        if (_openerWaitSince == 0)
        {
            _openerWaitSince = now;
        }

        if (now - _openerWaitSince < OpenerStuckMs)
        {
            return false;
        }

        LogDrain(champion, drain, $"opener: given up after {now - _openerWaitSince} ms {state} " +
            $"(casting {champion.IsCasting}, pending {champion.castingComponent?.HasPendingSkillRequests}, moving {champion.IsMoving}, speed {champion.CurrentSpeed})");
        _openerWaitSince = 0;

        return true;
    }

    /// <summary>
    /// Her last drain ended early: she is being interrupted (GameLiving.IsBeingInterruptedByOther) or the cast stopped
    /// in less than half its casting time.
    /// </summary>
    private bool DrainInterrupted(GauntletChampion champion, Spell drain, long now)
    {
        if (_drainCastTick == 0)
        {
            return false;
        }

        long ranFor = now - _drainCastTick;
        _drainCastTick = 0;

        if (ranFor >= drain.CastTime / 2)
        {
            _drainFailures = 0;

            return false;
        }

        if (champion.IsBeingInterruptedByOther)
        {
            LogDrain(champion, drain, "interrupted");

            return true;
        }

        // It never started (or was refused at once): another try, up to MaxDrainFailures in a row.
        _drainFailures++;
        LogDrain(champion, drain, $"stopped after {ranFor} ms without an interrupt ({_drainFailures}/{MaxDrainFailures})");

        return _drainFailures >= MaxDrainFailures;
    }

    /// <summary>
    /// One drain cast at the challenger, standing still: a champion that was moving stops and casts on the next think.
    /// Null when the cast was requested or will be tried again; true when she should give up (too many refusals).
    /// </summary>
    private bool? CastDrain(GauntletChampion champion, GamePlayer challenger, Spell drain, long now)
    {
        champion.TurnTo(challenger);

        if (champion.IsMoving)
        {
            champion.StopMoving();

            return null;
        }

        if (TryCastOn(champion, drain, challenger))
        {
            _drainCastTick = now;
            LogDrain(champion, drain, "cast");

            return null;
        }

        _drainFailures++;
        string why = !Castable(champion, drain)
            ? $"not castable (power {champion.Mana}/{champion.PowerCost(drain)}, disabled {champion.GetSkillDisabledDuration(drain)} ms, silenced {champion.IsSilenced}, incapacitated {champion.IsIncapacitated})"
            : $"refused by CastSpell (casting {champion.IsCasting}, pending {champion.castingComponent?.HasPendingSkillRequests})";
        LogDrain(champion, drain, $"{why} ({_drainFailures}/{MaxDrainFailures})");

        return _drainFailures >= MaxDrainFailures;
    }

    private static void LogDrain(GauntletChampion champion, Spell drain, string what)
    {
        if (DrainLog.IsInfoEnabled)
        {
            DrainLog.Info($"GAUNTLET_DRAIN {champion.Name} {drain?.Name}: {what}");
        }
    }

    /// <summary>The kite: run until far enough, then drain while the challenger stays out of melee range.</summary>
    private bool ThinkKite(GauntletChampion champion, GamePlayer challenger, long now)
    {
        Spell drain = champion.SignatureDrain;

        if (champion.IsCasting || champion.castingComponent?.HasPendingSkillRequests == true)
        {
            return true;
        }

        bool caught = champion.IsWithinRadius(challenger, champion.attackComponent.AttackRange + DotMeleeMargin);

        if (_kitePhase == KitePhase.Run)
        {
            if (now - _kiteStartTick > ManoeuvreMaxMs || champion.MaxSpeed < MinRunSpeed)
            {
                return false;
            }

            if (caught)
            {
                _kiteCaughtSince = _kiteCaughtSince == 0 ? now : _kiteCaughtSince;

                if (now - _kiteCaughtSince > KiteCaughtMs)
                {
                    // A challenger she cannot snare keeps up: the kite fails.
                    return false;
                }
            }
            else
            {
                _kiteCaughtSince = 0;
            }

            if (!champion.IsWithinRadius(challenger, FleeDistance) || champion.IsWithinRadius(_retreatPoint, ArrivedDistance * 2, true))
            {
                _kitePhase = KitePhase.Drain;
            }
            else
            {
                // Kiting away: it may sprint (owner 2026-10-28).
                ApplySpeed(champion, KiteSprintFactor);

                if (!champion.IsMoving)
                {
                    champion.PathTo(_retreatPoint, champion.MaxSpeed);
                }

                return true;
            }
        }

        // Drain: from where she stopped, while the challenger is out of melee range and her casts go through.
        if (caught || DrainInterrupted(champion, drain, now))
        {
            return false;
        }

        ApplySpeed(champion, 1);

        if (!champion.IsWithinRadius(challenger, drain.Range))
        {
            LogDrain(champion, drain, "challenger out of range: the kite ends");

            return false;
        }

        return CastDrain(champion, challenger, drain, now) != true;
    }

    /// <summary>The kite is over: she fights on, after Ignore Pain when she is still at 40% or less.</summary>
    private void EndKite(GauntletChampion champion)
    {
        if (_kitePhase == KitePhase.None)
        {
            return;
        }

        _kitePhase = KitePhase.None;
        _drainCastTick = 0;
        _drainFailures = 0;
        ApplySpeed(champion, 1);

        if (champion.HealthPercent <= ChampionTactics.IosKitePercent)
        {
            AutonomousBotRealmAbilities.TryUse(champion, AutonomousBotRealmAbilities.IgnorePain, AutonomousBotRealmAbilities.IgnorePainTank);
        }
    }

    #endregion

    #region Gwyraan

    /// <summary>
    /// Gwyraan below half health (owner 2026-10-27, ChampionTactics.GwyraanLowHealth): whenever his instant root is
    /// ready and the challenger can be rooted, the escape (root, pet off, run, Major Restoration, Degeneration, pet and
    /// melee back in); otherwise his instant heal when it is ready.
    /// </summary>
    private bool ThinkGwyraan(GauntletChampion champion, GamePlayer challenger, long now)
    {
        if (challenger == null)
        {
            EndEscape(champion, null);

            return false;
        }

        if (_escapePhase != EscapePhase.None)
        {
            if (ThinkEscape(champion, challenger, now))
            {
                return true;
            }

            EndEscape(champion, challenger);

            return false;
        }

        Spell root = champion.SignatureRoot;
        bool rootReady = root != null && Castable(champion, root) && champion.IsWithinRadius(challenger, root.Range);
        bool rootImmune = challenger.effectListComponent.ContainsEffectForEffectType(eEffect.SnareImmunity);
        bool instantHealReady = champion.SignatureInstantHeal != null && Castable(champion, champion.SignatureInstantHeal);
        LowHealthAction action = ChampionTactics.GwyraanLowHealth(champion.HealthPercent, rootReady, rootImmune, instantHealReady);

        if (action == LowHealthAction.InstantHeal)
        {
            return TryCastOn(champion, champion.SignatureInstantHeal, champion);
        }

        if (action != LowHealthAction.RootEscape)
        {
            return false;
        }

        champion.StopAttack();

        if (champion.IsCasting)
        {
            champion.StopCurrentSpellcast();
        }

        if (!TryCastOn(champion, root, challenger))
        {
            return false;
        }

        _escapePhase = EscapePhase.Rooting;
        _escapeTick = now;
        _escapeHealStarted = false;

        // The pet leaves the challenger so its hits do not break the root (Passive: it disengages and follows him).
        PetStandDown(champion);

        return true;
    }

    private bool ThinkEscape(GauntletChampion champion, GamePlayer challenger, long now)
    {
        if (champion.IsCasting || champion.castingComponent?.HasPendingSkillRequests == true)
        {
            return true;
        }

        bool rooted = challenger.effectListComponent.ContainsEffectForEffectType(eEffect.MovementSpeedDebuff);
        bool caught = champion.IsWithinRadius(challenger, champion.attackComponent.AttackRange + DotMeleeMargin);

        switch (_escapePhase)
        {
            case EscapePhase.Rooting:
            {
                if (rooted)
                {
                    _escapePhase = EscapePhase.Run;
                    _escapeTick = now;
                    _retreatPoint = RetreatPointFrom(champion, challenger);
                    champion.StopFollowing();

                    return true;
                }

                // Resisted: back to the fight.
                return now - _escapeTick < LandWindowMs;
            }
            case EscapePhase.Run:
            {
                if (!rooted && caught || now - _escapeTick > ManoeuvreMaxMs || champion.MaxSpeed < MinRunSpeed)
                {
                    return false;
                }

                if (!champion.IsWithinRadius(challenger, FleeDistance) || champion.IsWithinRadius(_retreatPoint, ArrivedDistance * 2, true))
                {
                    ApplySpeed(champion, 1);

                    if (champion.IsMoving)
                    {
                        champion.StopMoving();
                    }

                    _escapePhase = EscapePhase.Heal;

                    return true;
                }

                // Kiting away: it may sprint (owner 2026-10-28).
                ApplySpeed(champion, KiteSprintFactor);

                if (!champion.IsMoving)
                {
                    champion.PathTo(_retreatPoint, champion.MaxSpeed);
                }

                return true;
            }
            case EscapePhase.Heal:
            {
                if (!_escapeHealStarted && champion.Health < champion.MaxHealth && TryCastOn(champion, champion.SignatureHeal, champion))
                {
                    _escapeHealStarted = true;

                    return true;
                }

                _escapePhase = EscapePhase.Dot;

                return true;
            }
            case EscapePhase.Dot:
            {
                Spell dot = champion.SignatureDot;

                if (dot != null && champion.IsWithinRadius(challenger, dot.Range))
                {
                    champion.TurnTo(challenger);
                    TryCastOn(champion, dot, challenger);
                }

                _escapePhase = EscapePhase.None;

                return true;
            }
            default:
            {
                return false;
            }
        }
    }

    /// <summary>The escape is over: the pet goes back in on the challenger and he closes to melee (the bot's fight).</summary>
    private void EndEscape(GauntletChampion champion, GamePlayer challenger)
    {
        bool wasEscaping = _escapePhase != EscapePhase.None;
        _escapePhase = EscapePhase.None;
        _escapeHealStarted = false;
        ApplySpeed(champion, 1);

        if (challenger != null)
        {
            PetAttack(champion, challenger);
        }
        else if (wasEscaping)
        {
            PetStandDown(champion);
        }
    }

    /// <summary>
    /// Gwyraan's pet on the challenger. BotBrain never orders a Druid's pet to attack (AutonomousPetSupport
    /// .IsDefensiveOnlyPetClass), so his brain does; ControlledMobBrain.Attack ignores a repeated order.
    /// </summary>
    private static void PetAttack(GauntletChampion champion, GamePlayer challenger)
    {
        if (champion.ControlledBrain is ControlledMobBrain petBrain && petBrain.Body?.IsAlive == true && petBrain.OrderedAttackTarget != challenger)
        {
            petBrain.Attack(challenger);
        }
    }

    /// <summary>The pet stops fighting and follows (ControlledMobBrain's Passive state disengages and follows the owner).</summary>
    private static void PetStandDown(GauntletChampion champion)
    {
        if (champion.ControlledBrain is ControlledMobBrain petBrain && petBrain.Body?.IsAlive == true)
        {
            petBrain.SetAggressionState(eAggressionState.Passive);
        }
    }

    #endregion

    #region Toothmouth

    /// <summary>
    /// Toothmouth (owner 2026-10-27, replacing the once-per-fight rule). Below half health
    /// (ChampionTactics.ToothmouthLowHealth): whenever an instant mez is ready and lands, he retreats and heals; with no
    /// mez, Speed of Sound and the same retreat. Above half health he never mezzes (owner 2026-10-28: the mez is
    /// saved for the escape). His shout is woven into his melee
    /// (<see cref="InstantSpells"/>). True while this owns the think.
    /// </summary>
    private bool ThinkToothmouth(GauntletChampion champion, GamePlayer challenger, long now)
    {
        if (challenger == null)
        {
            _retreating = false;
            _mezCastTick = 0;
            _positionalMez = false;
            ApplySpeed(champion, 1);

            return false;
        }

        if (_retreating)
        {
            if (ThinkRetreat(champion, challenger, now))
            {
                return true;
            }

            _retreating = false;
            ApplySpeed(champion, 1);

            return false;
        }

        if (_mezCastTick > 0)
        {
            if (challenger.IsMezzed)
            {
                _mezCastTick = 0;

                if (_mezPurpose == MezPurpose.Retreat)
                {
                    StartRetreat(champion, challenger, now, false);

                    return true;
                }

                // The positional: UpdateStep walks him behind the mezzed challenger.
                _positionalMez = true;

                return false;
            }

            if (now - _mezCastTick < LandWindowMs)
            {
                // The instant cast is on its way; hold the swings so nothing breaks a mez that lands.
                return true;
            }

            // Resisted.
            _mezCastTick = 0;

            return false;
        }

        if (_positionalMez || champion.IsCasting || champion.castingComponent?.HasPendingSkillRequests == true)
        {
            return false;
        }

        Spell mez = champion.SignatureMezzes.FirstOrDefault(spell => Castable(champion, spell) && champion.IsWithinRadius(challenger, spell.Range));
        bool mezImmune = challenger.effectListComponent.ContainsEffectForEffectType(eEffect.MezImmunity);
        LowHealthAction action = ChampionTactics.ToothmouthLowHealth(champion.HealthPercent, mez != null, mezImmune, challenger.IsMezzed,
            champion.SpeedOfSoundReady);

        if (action == LowHealthAction.MezRetreat)
        {
            return CastMez(champion, challenger, mez, MezPurpose.Retreat, now);
        }

        if (action == LowHealthAction.SpeedOfSoundRetreat)
        {
            if (!champion.TrySpeedOfSound())
            {
                return false;
            }

            StartRetreat(champion, challenger, now, true);

            return true;
        }

        // No mez for his positional (owner 2026-10-28: "He needs to save it for his escape to heal").
        return false;
    }

    private bool CastMez(GauntletChampion champion, GamePlayer challenger, Spell mez, MezPurpose purpose, long now)
    {
        champion.StopAttack();

        if (champion.IsCasting)
        {
            champion.StopCurrentSpellcast();
        }

        if (!TryCastOn(champion, mez, challenger))
        {
            return false;
        }

        _mezCastTick = now;
        _mezPurpose = purpose;

        return true;
    }

    private void StartRetreat(GauntletChampion champion, GamePlayer challenger, long now, bool bySpeedOfSound)
    {
        _retreating = true;
        _retreatBySpeedOfSound = bySpeedOfSound;
        _retreatStartTick = now;
        _positionalMez = false;
        _step = SignatureStep.None;
        _retreatPoint = RetreatPointFrom(champion, challenger);
        champion.StopAttack();
        champion.StopFollowing();
        champion.TargetObject = null;
    }

    /// <summary>
    /// The retreat: he runs (sprinting, or at Speed of Sound's speed while it lasts) to a point past his spot away from
    /// the challenger, within the leash, and heals himself with his best heal until he is full. A mez retreat ends when
    /// the mez does; a Speed of Sound retreat when the challenger catches him or after <see cref="ManoeuvreMaxMs"/>.
    /// </summary>
    private bool ThinkRetreat(GauntletChampion champion, GamePlayer challenger, long now)
    {
        bool full = champion.Health >= champion.MaxHealth;
        bool caught = champion.IsWithinRadius(challenger, champion.attackComponent.AttackRange);
        bool over = _retreatBySpeedOfSound ? caught && !champion.IsCasting || now - _retreatStartTick > ManoeuvreMaxMs : !challenger.IsMezzed;

        if (over || full && !champion.IsCasting)
        {
            return false;
        }

        if (champion.attackComponent.AttackState)
        {
            champion.StopAttack();
        }

        if (!champion.IsWithinRadius(_retreatPoint, ArrivedDistance * 2, true))
        {
            ApplySpeed(champion, champion.HasSpeedOfSound ? MaxSpeedCalculator.SPEED4 : KiteSprintFactor);

            if (!champion.IsMoving)
            {
                champion.PathTo(_retreatPoint, champion.MaxSpeed);
            }

            return true;
        }

        ApplySpeed(champion, 1);

        if (champion.IsMoving)
        {
            champion.StopMoving();
        }

        Spell heal = champion.SignatureHeal;

        if (heal != null && !full)
        {
            TryCastOn(champion, heal, champion);
        }

        return true;
    }

    #endregion

    /// <summary>
    /// A point past the champion's spot away from the challenger, within the leash (80% of it). The spot is where it
    /// appeared (the arena's gate, or an act boss's place in its zone); an act boss with no leash of its own runs as far
    /// as the Gauntlet's leash would let it.
    /// </summary>
    private static Point3D RetreatPointFrom(GauntletChampion champion, GamePlayer challenger)
    {
        int leash = champion.TetherRange > 0 ? champion.TetherRange : GauntletRules.LeashRadius;
        (int x, int y) = GauntletPositioning.RetreatPoint(champion.SpawnPoint.X, champion.SpawnPoint.Y, challenger.X, challenger.Y,
            (int) (leash * RetreatLeashShare));

        return new Point3D(x, y, champion.SpawnPoint.Z);
    }

    /// <summary>
    /// Ios's run through a challenger who faces her (owner 2026-10-25: get the back stun off a stuck challenger). The
    /// server knows a player's heading only from their client's packets (ClientService.BeginTick, the start of a loop
    /// tick), so right after she passes them their heading still points where she came from. A swing fires on the first
    /// loop tick within half a tick of AttackAction.NextTick (GameServiceUtils.ShouldTick), in AttackService, which runs
    /// before MovementService: it sees her where the tick before moved her. She keeps fighting until the tick from which
    /// a walk, at up to her (boosted) speed, puts her half a tick's travel past the challenger on the tick before that
    /// swing (GauntletPositioning.RunThroughSpeed), then walks straight through them and stops 20 units past, so the swing
    /// checks Damaging Grasp from behind (within 32 units she counts as facing them). It fails only when the
    /// challenger's client sends a heading in that last tick (UT_ExpeditionGauntletSignatures measures the rates); then
    /// <see cref="UpdateStep"/> picks her next move.
    /// </summary>
    private void RunThrough(GauntletChampion champion, GamePlayer challenger, long now)
    {
        if (_runSwingTick > 0)
        {
            // Running: the walk was timed when it started.
            return;
        }

        if (_thinkIntervalBeforeRun == 0)
        {
            _thinkIntervalBeforeRun = ThinkInterval;
            ThinkInterval = RunThroughThinkMs;
        }

        long swingDue = champion.attackComponent.attackAction.NextTick;
        double dx = challenger.X - champion.X;
        double dy = challenger.Y - champion.Y;

        // Her top speed already carries the back-stun boost (AttackMostWanted).
        double topSpeed = champion.MaxSpeed;

        // The timing assumes both stand still at the start (a moving challenger moves the line; a following champion
        // moves once more before the walk starts).
        double speed = champion.attackComponent.AttackState && !champion.IsMoving && !challenger.IsMoving && !champion.IsCasting
            ? GauntletPositioning.RunThroughSpeed(now, swingDue, GameLoop.TickDuration, Math.Sqrt(dx * dx + dy * dy), topSpeed)
            : 0;

        if (speed <= 0)
        {
            if (now >= _nextPressureTick)
            {
                _nextPressureTick = now + PressureThinkMs;
                base.AttackMostWanted();
            }

            return;
        }

        // No BotBrain fight this tick: its StartAttack would order a melee follow over the walk.
        (int x, int y) = GauntletPositioning.PointBeyond(champion.X, champion.Y, challenger.X, challenger.Y, RunThroughStopPast);
        _runSwingTick = swingDue;
        champion.TargetObject = challenger;
        champion.StopFollowing();
        champion.WalkTo(new Point3D(x, y, challenger.Z), (short) Math.Min(champion.MaxSpeed, Math.Ceiling(speed)));
    }

    /// <summary>Back to the usual think pace once no run through is pending.</summary>
    private void EndRunThrough()
    {
        _runSwingTick = 0;

        if (_thinkIntervalBeforeRun > 0)
        {
            ThinkInterval = _thinkIntervalBeforeRun;
            _thinkIntervalBeforeRun = 0;
        }
    }

    /// <summary>
    /// Drunkstains (owner 2026-10-27): the one-hander and shield (Standard) between swings so he can block, the polearm
    /// (TwoHanded) for the swing. The next swing's time does not depend on the slot: AttackAction adds the interval of the
    /// weapon that last swung (FinalizeMeleeAttack, AttackSpeed(_weapon)) to _nextMeleeTick, and GameNPC.SwitchWeapon's
    /// StopAttack and RequestStartAttack leave _nextMeleeTick alone (OnStopAttack only resets the ranged tick). So the
    /// polearm is taken up when the swing is due within a think interval (plus a margin), and the shield again at the next
    /// think after it. Whenever the challenger can be stunned (not stunned, not immune) his swing is a shield stun
    /// instead, from the shield: Slam first, then Slam or Numb at even odds (ChampionTactics.ShieldStun), chosen once per
    /// stun swung. No shield style after a block any more.
    /// </summary>
    private void HoldShieldOrPolearm(GauntletChampion champion, GamePlayer challenger, long now)
    {
        IGameInventory inventory = champion.Inventory;
        DbInventoryItem shield = inventory?.GetItem(eInventorySlot.LeftHandWeapon);
        bool kit = inventory?.GetItem(eInventorySlot.TwoHandWeapon) != null && inventory.GetItem(eInventorySlot.RightHandWeapon) != null &&
            shield?.Object_Type == (int) eObjectType.Shield;

        if (!kit || challenger == null || !champion.attackComponent.AttackState || champion.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
        {
            champion.HeldSlot = null;

            return;
        }

        AttackData last = champion.attackComponent.attackAction.LastAttackData;

        if (_shieldStunChoice != null && last != null && last != _lastShieldStunAttack && last.Style?.Name == _shieldStunChoice)
        {
            // Swung: the next shield stun is chosen afresh.
            _lastShieldStunAttack = last;
            _shieldStunChoice = null;
            _shieldStunOpenerDone = true;
        }

        bool stunImmune = StunImmune(challenger);

        if (_shieldStunChoice == null)
        {
            _shieldStunChoice = ChampionTactics.ShieldStun(stunImmune, challenger.IsStunned, _shieldStunOpenerDone, Random.Shared.Next(2) == 0);

            // A stun he does not have (his Shields spec too low) would be waited for forever: the other one instead.
            if (_shieldStunChoice != null && champion.Styles?.Any(style => style.Name == _shieldStunChoice) != true)
            {
                _shieldStunChoice = _shieldStunChoice == ChampionTactics.Slam ? ChampionTactics.Numb : ChampionTactics.Slam;
            }
        }

        string stunName = !stunImmune && !challenger.IsStunned ? _shieldStunChoice : null;
        Style stunStyle = stunName == null ? null : champion.Styles?.FirstOrDefault(style => style.Name == stunName);
        bool stunNow = stunStyle != null && stunStyle.Level <= champion.Level && StyleProcessor.CheckEnduranceCost(champion, shield, stunStyle) &&
            champion.CheckStyleStun(stunStyle) && StyleProcessor.CanUseStyle(last, champion, stunStyle, shield);

        if (stunNow)
        {
            champion.CurrentPriority = new[] { stunName }.Concat(champion.PriorityStyles).ToArray();
        }

        // NpcService thinks again at now + ThinkInterval (read after this think), so a swing due before then is the polearm's.
        long untilSwing = champion.attackComponent.attackAction.NextTick - now;
        eActiveWeaponSlot wanted = ChampionTactics.ShieldOrPolearm(stunNow, untilSwing, ThinkInterval + SwapMarginMs);

        if (champion.HeldSlot != wanted || champion.ActiveWeaponSlot != wanted)
        {
            champion.HeldSlot = wanted;
            champion.SwitchWeapon(wanted);
        }
    }
}

/// <summary>
/// A champion's signature as data (owner 2026-10-25, revised 2026-10-27), by Style.Name in the server's style table.
/// </summary>
/// <param name="Priority">Styles used first whenever one can be used, in order.</param>
/// <param name="BehindOpener">A back positional the champion goes behind its challenger to land; null for none.</param>
/// <param name="SideChain">A side positional and its follow-ups, in chain order, swung from the challenger's side while the
/// challenger is stunned.</param>
/// <param name="ImmunePriority">Styles used first instead of <paramref name="Priority"/> while the challenger is immune to
/// stuns, follow-ups first; empty to keep <paramref name="Priority"/>.</param>
/// <param name="Filler">The style swung when no priority style can be; null for the champion's best anytime style
/// (<see cref="ChampionStyles.BestFiller"/>).</param>
/// <param name="FillerFollowup">A follow-up swung right after the filler lands (Preesh: Solar Flare after Hurricane); null for none.</param>
/// <param name="BackStunAnywhere">Its back positional (BehindOpener) lands from any side (StyleProcessor; owner 2026-10-28: Ios).</param>
/// <param name="ShieldStun">Drunkstains: the shield held between polearm swings, and Slam or Numb from it whenever the
/// challenger can be stunned.</param>
/// <param name="RangedDot">Gwyraan: his pet, his DoT as opener and on a challenger beyond melee reach, and his root escape.</param>
/// <param name="MezRetreat">Toothmouth: his instant mezzes (positional set-up, retreat and heal), shouts, Speed of Sound.</param>
/// <param name="DrainKite">Ios: her ranged opener (snare, lifedrains) and her kite at 40% health.</param>
public sealed record ChampionSignature(string[] Priority, string BehindOpener, string[] SideChain, string[] ImmunePriority, string Filler = null,
    bool ShieldStun = false, bool RangedDot = false, bool MezRetreat = false, bool DrainKite = false, bool BladeWork = false,
    string FillerFollowup = null, bool BackStunAnywhere = false)
{
    public static readonly ChampionSignature None = new([], null, [], []);
}

/// <summary>What a champion's back-stun attempt does next (<see cref="ChampionTactics.NextBackStunMove"/>).</summary>
public enum BackStunMove
{
    /// <summary>The back stun landed (or the challenger is stunned): the attempt is over.</summary>
    Landed,

    /// <summary>The challenger became immune to stuns (or is mezzed): the attempt is dropped.</summary>
    Drop,

    /// <summary>A timed run through a challenger who faces the champion.</summary>
    RunThrough,

    /// <summary>To (or stay at) the spot behind the challenger.</summary>
    MoveBehind
}

/// <summary>What a champion does about its low health (<see cref="ChampionTactics"/>).</summary>
public enum LowHealthAction
{
    None,
    Kite,
    IgnorePain,
    RootEscape,
    InstantHeal,
    MezRetreat,
    SpeedOfSoundRetreat
}

/// <summary>The champions' decisions as pure functions of the fight's state (owner 2026-10-27; unit-tested).</summary>
public static class ChampionTactics
{
    /// <summary>Ios's speed while she goes for her back stun (owner 2026-10-28: 4x her run speed, was 2x).</summary>
    public const double BackStunBoost = 4.0;

    /// <summary>Ios kites at this health or less.</summary>
    public const int IosKitePercent = 40;

    /// <summary>Gwyraan and Toothmouth act on their low health below this.</summary>
    public const int LowHealthPercent = 50;

    public const string Slam = "Slam";

    public const string Numb = GauntletChampion.NumbStyleName;

    /// <summary>
    /// The purge (owner 2026-10-27): a mez, or a stun that is not Numb's, at once; Numb's stun only when the coin tossed
    /// for it says so (50%).
    /// </summary>
    public static bool PurgeWanted(bool mezzed, bool stunned, bool numbStun, bool numbCoinPurges)
    {
        return mezzed || stunned && (!numbStun || numbCoinPurges);
    }

    /// <summary>The champion's priority outside its steps: the immunity window's own while the challenger is stun-immune.</summary>
    public static IReadOnlyList<string> BasePriority(ChampionSignature signature, bool stunImmune)
    {
        if (signature == null)
        {
            return Array.Empty<string>();
        }

        return stunImmune && signature.ImmunePriority.Length > 0 ? signature.ImmunePriority : signature.Priority;
    }

    /// <summary>
    /// Ios's back-stun attempt (owner 2026-10-27): it goes on, after a miss or a turn, until Damaging Grasp lands (or the
    /// challenger is stunned some other way); it is dropped when the challenger becomes immune to stuns or is mezzed.
    /// Meanwhile a challenger who faces her gets the run through, any other the walk to (or the stay at) their back.
    /// </summary>
    public static BackStunMove NextBackStunMove(bool landed, bool challengerStunned, bool stunImmune, bool challengerMezzed,
        bool challengerFacesChampion, bool championBehind)
    {
        if (landed || challengerStunned)
        {
            return BackStunMove.Landed;
        }

        if (stunImmune || challengerMezzed)
        {
            return BackStunMove.Drop;
        }

        return challengerFacesChampion && !championBehind ? BackStunMove.RunThrough : BackStunMove.MoveBehind;
    }

    /// <summary>Ios's speed factor: <see cref="BackStunBoost"/> while she goes for her back stun, her own otherwise.</summary>
    public static double BackStunSpeedFactor(bool attempting)
    {
        return attempting ? BackStunBoost : 1.0;
    }

    /// <summary>
    /// Drunkstains's shield stun (owner 2026-10-27): none while the challenger is stunned or immune; Slam as his opener;
    /// then Numb or Slam at even odds.
    /// </summary>
    public static string ShieldStun(bool stunImmune, bool challengerStunned, bool openerDone, bool numbCoin)
    {
        if (stunImmune || challengerStunned)
        {
            return null;
        }

        if (!openerDone)
        {
            return Slam;
        }

        return numbCoin ? Numb : Slam;
    }

    /// <summary>
    /// Drunkstains's slot (owner 2026-10-27): the shield (Standard) between swings and for a shield stun; the polearm
    /// (TwoHanded) once his next swing is due within <paramref name="swapWindowMs"/> (his next think, plus a margin).
    /// </summary>
    public static eActiveWeaponSlot ShieldOrPolearm(bool shieldStunNow, long msUntilSwing, long swapWindowMs)
    {
        return !shieldStunNow && msUntilSwing <= swapWindowMs ? eActiveWeaponSlot.TwoHanded : eActiveWeaponSlot.Standard;
    }

    /// <summary>Ios at 40% health, once: the kite when she can, Ignore Pain when she cannot.</summary>
    public static LowHealthAction IosLowHealth(int healthPercent, bool kiteUsed, bool canKite)
    {
        if (kiteUsed || healthPercent > IosKitePercent)
        {
            return LowHealthAction.None;
        }

        return canKite ? LowHealthAction.Kite : LowHealthAction.IgnorePain;
    }

    /// <summary>Gwyraan below half health: the root escape whenever it is possible, else his instant heal when ready.</summary>
    public static LowHealthAction GwyraanLowHealth(int healthPercent, bool rootReady, bool challengerRootImmune, bool instantHealReady)
    {
        if (healthPercent >= LowHealthPercent)
        {
            return LowHealthAction.None;
        }

        if (rootReady && !challengerRootImmune)
        {
            return LowHealthAction.RootEscape;
        }

        return instantHealReady ? LowHealthAction.InstantHeal : LowHealthAction.None;
    }

    /// <summary>Toothmouth below half health: a mez and a retreat whenever a mez is ready, else Speed of Sound and a retreat.</summary>
    public static LowHealthAction ToothmouthLowHealth(int healthPercent, bool mezReady, bool challengerMezImmune, bool challengerMezzed,
        bool speedOfSoundReady)
    {
        if (healthPercent >= LowHealthPercent)
        {
            return LowHealthAction.None;
        }

        if (mezReady && !challengerMezImmune && !challengerMezzed)
        {
            return LowHealthAction.MezRetreat;
        }

        return speedOfSoundReady ? LowHealthAction.SpeedOfSoundRetreat : LowHealthAction.None;
    }

    /// <summary>
    /// Pre-fight buffing is done (owner 2026-10-27): the minimum time is over and nothing is being cast or left to cast;
    /// or the safety deadline has passed (0 for none).
    /// </summary>
    public static bool PrefightDone(long now, long buffUntilTick, long deadlineTick, bool busy, bool buffMissing)
    {
        if (now < buffUntilTick)
        {
            return false;
        }

        return !busy && !buffMissing || deadlineTick > 0 && now >= deadlineTick;
    }
}

/// <summary>The champions' filler style (owner 2026-10-27; pure, unit-tested).</summary>
public static class ChampionStyles
{
    /// <summary>An anytime style: no opening (no preceding style, attack result or position) and no stealth.</summary>
    public static bool IsAnytime(Style style)
    {
        return style != null && style.OpeningRequirementType == Style.eOpening.Offensive && style.OpeningRequirementValue == 0 &&
            style.AttackResultRequirement == Style.eAttackResultRequirement.Any && !style.StealthRequirement;
    }

    /// <summary>The better filler: the higher growth rate, then the higher to-hit bonus.</summary>
    public static bool Better(Style candidate, Style current)
    {
        return current == null || candidate.GrowthRate > current.GrowthRate ||
            candidate.GrowthRate == current.GrowthRate && candidate.BonusToHit > current.BonusToHit;
    }

    /// <summary>
    /// The filler among <paramref name="styles"/> that pass <paramref name="usable"/>: the one named
    /// <paramref name="fillerName"/>, or for none the best anytime style; null for none.
    /// </summary>
    public static Style BestFiller(IEnumerable<Style> styles, string fillerName, Func<Style, bool> usable)
    {
        Style best = null;

        foreach (Style style in styles ?? Enumerable.Empty<Style>())
        {
            bool candidate = fillerName != null ? style?.Name == fillerName : IsAnytime(style);

            if (candidate && usable(style) && Better(style, best))
            {
                best = style;
            }
        }

        return best;
    }
}

/// <summary>Where a champion goes around its target: behind it, at either side, or in front.</summary>
public enum ChampionSpot
{
    Behind,
    Left,
    Right,
    Front
}

/// <summary>Where an attacker stands relative to its target's facing, as StyleProcessor's positional check sees it.</summary>
public enum TargetArc
{
    Front,
    Side,
    Back
}

/// <summary>
/// Pure geometry for the champions' positional styles (no world access, unit-tested in UT_ExpeditionGauntletSignatures).
/// The math mirrors the server's exactly: Point2D.GetHeading and GetPointFromHeading (heading 0 points along +Y,
/// headings turn clockwise, 4096 to a circle), GameObject.GetAngle and IsObjectInFront, and the angle ranges of
/// StyleProcessor.CanUseStyle's Positional case.
/// </summary>
public static class GauntletPositioning
{
    /// <summary>The point <paramref name="distance"/> from the target in the given direction from its facing.</summary>
    public static (int X, int Y) SpotFor(int targetX, int targetY, ushort targetHeading, ChampionSpot spot, int distance)
    {
        int offset = spot switch
        {
            ChampionSpot.Behind => 2048,
            ChampionSpot.Left => 1024,
            ChampionSpot.Right => 3072,
            _ => 0
        };

        // Point2D.GetPointFromHeading, rounded rather than truncated.
        double angle = ((targetHeading + offset) & 0xFFF) * Point2D.HEADING_TO_RADIAN;

        return ((int) Math.Round(targetX - Math.Sin(angle) * distance), (int) Math.Round(targetY + Math.Cos(angle) * distance));
    }

    /// <summary>The heading from one point to another: Point2D.GetHeading.</summary>
    public static ushort HeadingTo(int fromX, int fromY, int toX, int toY)
    {
        float dx = toX - fromX;
        float dy = toY - fromY;
        double heading = Math.Atan2(-dx, dy) * Point2D.RADIAN_TO_HEADING;

        if (heading < 0)
        {
            heading += 4096;
        }

        return (ushort) heading;
    }

    /// <summary>
    /// The angle in degrees, clockwise from the facing of the object at (x, y) with <paramref name="heading"/>, towards
    /// (toX, toY): GameObject.GetAngle. StyleProcessor uses target.GetAngle(attacker) for the position.
    /// </summary>
    public static float AngleFrom(int x, int y, ushort heading, int toX, int toY)
    {
        float headingDifference = HeadingTo(x, y, toX, toY) - (heading & 0xFFF);

        if (headingDifference < 0)
        {
            headingDifference += 4096.0f;
        }

        return headingDifference * 360.0f / 4096.0f;
    }

    /// <summary>StyleProcessor.CanUseStyle's ranges: back (150, 210), side [45, 150] or [210, 315], front the rest.</summary>
    public static TargetArc Classify(float angle)
    {
        if (angle is > 150 and < 210)
        {
            return TargetArc.Back;
        }

        if (angle is (>= 45 and <= 150) or (>= 210 and <= 315))
        {
            return TargetArc.Side;
        }

        return TargetArc.Front;
    }

    /// <summary>GameObject.IsObjectInFront(target, 120) on the plane: within the field of view, or within 32 units.</summary>
    public static bool IsFacing(int x, int y, ushort heading, int toX, int toY, double fieldOfView = 120, int alwaysTrueRange = 32)
    {
        float angle = AngleFrom(x, y, heading, toX, toY);

        if (angle >= 360 - fieldOfView / 2 || angle < fieldOfView / 2)
        {
            return true;
        }

        long dx = toX - x;
        long dy = toY - y;

        return dx * dx + dy * dy <= (long) alwaysTrueRange * alwaysTrueRange;
    }

    /// <summary>
    /// Whether StyleProcessor.CanUseStyle's Positional case accepts an attacker at (x, y) facing <paramref name="heading"/>
    /// for a style that needs <paramref name="required"/> on a target at (targetX, targetY) facing
    /// <paramref name="targetHeading"/>: the attacker faces the target and stands in the required arc.
    /// </summary>
    public static bool Accepts(TargetArc required, int x, int y, ushort heading, int targetX, int targetY, ushort targetHeading)
    {
        return IsFacing(x, y, heading, targetX, targetY) && Classify(AngleFrom(targetX, targetY, targetHeading, x, y)) == required;
    }

    /// <summary>A run through starts only at this share of its top speed or more (late and fast, so it starts on a tick).</summary>
    public const double RunThroughMinSpeedShare = 0.75;

    /// <summary>
    /// The walk speed for a run through a target <paramref name="distance"/> away that is half a tick's travel past it on
    /// the tick before the swing fires, or 0 when this tick is not the one to start (too early: a later tick still makes
    /// it at 75 %+ of <paramref name="maxSpeed"/>; too late: not even <paramref name="maxSpeed"/> makes it). Now is tick 0
    /// and the walk starts on it (MovementService runs after NpcService). The swing fires on the first tick k with
    /// swingDueTick - t(k) - tickDuration / 2 &lt;= 0 (GameServiceUtils.ShouldTick) and sees the position MovementService
    /// gave the champion on tick k - 1, after (k - 1) ticks of walking.
    /// </summary>
    public static double RunThroughSpeed(long now, long swingDueTick, double tickDuration, double distance, double maxSpeed)
    {
        double fireTick = Math.Ceiling((swingDueTick - tickDuration / 2 - now) / tickDuration);

        if (fireTick < 2 || distance <= 0 || maxSpeed <= 0)
        {
            return 0;
        }

        // (k - 1) ticks of walking cover the distance plus half a tick of travel: speed * (k - 1.5) ticks = distance.
        double speed = distance / ((fireTick - 1.5) * tickDuration / 1000.0);

        return speed <= maxSpeed && speed > maxSpeed * RunThroughMinSpeedShare ? speed : 0;
    }

    /// <summary>The point <paramref name="distance"/> past (throughX, throughY) on the line from (fromX, fromY).</summary>
    public static (int X, int Y) PointBeyond(int fromX, int fromY, int throughX, int throughY, int distance)
    {
        double dx = throughX - fromX;
        double dy = throughY - fromY;
        double length = Math.Sqrt(dx * dx + dy * dy);

        if (length <= 0)
        {
            return (throughX, throughY);
        }

        return ((int) Math.Round(throughX + dx / length * distance), (int) Math.Round(throughY + dy / length * distance));
    }

    /// <summary>
    /// Toothmouth's retreat point: <paramref name="distance"/> from his spot, on the far side of it from the challenger
    /// (straight past the spot, +X when the challenger stands on it).
    /// </summary>
    public static (int X, int Y) RetreatPoint(int spotX, int spotY, int challengerX, int challengerY, int distance)
    {
        double dx = spotX - challengerX;
        double dy = spotY - challengerY;
        double length = Math.Sqrt(dx * dx + dy * dy);

        if (length <= 0)
        {
            return (spotX + distance, spotY);
        }

        return ((int) Math.Round(spotX + dx / length * distance), (int) Math.Round(spotY + dy / length * distance));
    }

    /// <summary>The side spot (left or right) nearer to the champion.</summary>
    public static ChampionSpot NearestSide(int targetX, int targetY, ushort targetHeading, int championX, int championY, int distance)
    {
        (int leftX, int leftY) = SpotFor(targetX, targetY, targetHeading, ChampionSpot.Left, distance);
        (int rightX, int rightY) = SpotFor(targetX, targetY, targetHeading, ChampionSpot.Right, distance);
        long left = (long) (leftX - championX) * (leftX - championX) + (long) (leftY - championY) * (leftY - championY);
        long right = (long) (rightX - championX) * (rightX - championX) + (long) (rightY - championY) * (rightY - championY);

        return left <= right ? ChampionSpot.Left : ChampionSpot.Right;
    }
}

/// <summary>
/// Ios's build (owner 2026-10-25): the stock ValewalkerBotSpec's third variant, 50 Scythe, 34 Arboreal Path, 26 Parry,
/// with its two-handed scythe (WeaponOneType Scythe, Is2H, SpecType TwoHanded; GameBot.SetWeapons gives a Valewalker
/// the scythe in the two-hand slot).
/// </summary>
public sealed class IosBotSpec : BotSpec
{
    public IosBotSpec()
    {
        WeaponOneType = eObjectType.Scythe;
        Is2H = true;
        SpecType = eSpecType.TwoHanded;
        Add(Specs.Arboreal_Path, 34, 0.8f);
        Add(Specs.Parry, 26, 0.1f);
        Add(ObjToSpec(WeaponOneType), 50, 0.9f);
    }
}

/// <summary>
/// Drunkstains's build (owner 2026-10-25): the stock ArmsmanBotSpec TwoHandHybrid, 50 Polearm, 35 Slash, 42 Shields,
/// 18 Parry (the stock build's 3 Crossbow left out). GameBot.SetWeapons gives it the slashing one-hander and the
/// polearm, SetShield the shield; GauntletChampion fills a slot the item table left empty.
/// </summary>
public sealed class DrunkstainsBotSpec : BotSpec
{
    public DrunkstainsBotSpec()
    {
        WeaponOneType = eObjectType.SlashingWeapon;
        DamageType = eWeaponDamageType.Slash;
        WeaponTwoType = eObjectType.PolearmWeapon;
        Is2H = true;
        SpecType = eSpecType.TwoHandHybrid;
        // Slash 35, not 39 (owner 2026-10-28: "drunkstains never attempted to slam or numb"): with 39 the bot's points
        // (GameBot's spec allocation) ran out at Shields 38, and Slam needs Shields 42. 35 is the most that leaves 42.
        Add(ObjToSpec(WeaponOneType), 35, 0.6f);
        Add(ObjToSpec(WeaponTwoType), 50, 1.0f);
        Add(Specs.Shields, 42, 0.5f);
        Add(Specs.Parry, 18, 0.1f);
    }
}

/// <summary>
/// Gwyraan's build (owner 2026-10-27): 41 Nature (Degeneration, Bursting Undergrowth, Call Greater Nature Spirit), 23
/// Regrowth (Major Restoration, Bith's Benison) and 26 Nurture (Strength of the Wood, Squall's Dance, Hunter's Offense,
/// the resist buffs). A Druid has 1494 points at 50 (GameBot.GetSpecPointsForLevel, multiplier 10, with the creation
/// bonus); these cost 860 + 275 + 350 = 1485, leaving 9 (less than any next level). A mace (Blunt one-hander) and a
/// small shield: SetWeapons gives a Druid (a hybrid) a one-handed weapon of WeaponOneType, SetShield its Shield 1 shield.
/// </summary>
public sealed class GwyraanBotSpec : BotSpec
{
    public GwyraanBotSpec()
    {
        WeaponOneType = eObjectType.Blunt;
        Is2H = false;
        SpecType = eSpecType.NatureDruid;
        Add(Specs.Nature, 41, 1.0f);
        Add(Specs.Nurture, 26, 0.6f);
        Add(Specs.Regrowth, 23, 0.5f);
    }
}

/// <summary>
/// Toothmouth's build (owner 2026-10-27): 44 Blunt (Back Crush, Crushing Blow), 44 Music (Deluding Harmony, Concussive
/// Roar), 17 Regrowth, 13 Nurture. A Bard has 2223 points at 50 (multiplier 15, with the creation bonus); these cost
/// 989 + 989 + 152 + 90 = 2220. A mace: SetWeapons' Instrument case gives the Bard a one-hander of WeaponOneType (and his
/// instruments), SetShield his Shield 1 (small) shield.
/// </summary>
public sealed class ToothmouthBotSpec : BotSpec
{
    public ToothmouthBotSpec()
    {
        WeaponOneType = eObjectType.Blunt;
        SpecType = eSpecType.Instrument;
        Add(Specs.Blunt, 44, 0.9f);
        Add(Specs.Music, 44, 0.9f);
        Add(Specs.Regrowth, 17, 0.4f);
        Add(Specs.Nurture, 13, 0.3f);
    }
}

/// <summary>Picks the champions' signature spells from a known spell list (pure, unit-tested).</summary>
public static class ChampionSpells
{
    /// <summary>
    /// The buffs a champion casts before its fight, in order (owner 2026-10-27: the spec buffs first, strength and
    /// constitution, dexterity and quickness, haste; then the damage add, resists, armor and the rest): helpful, not
    /// pulsing, no instrument, on itself or others, lasting (concentration or a minute or more).
    /// </summary>
    private static readonly eSpellType[] PrefightOrder =
    [
        eSpellType.StrengthConstitutionBuff, eSpellType.DexterityQuicknessBuff, eSpellType.CombatSpeedBuff, eSpellType.DamageAdd,
        eSpellType.HeatResistBuff, eSpellType.ColdResistBuff, eSpellType.MatterResistBuff, eSpellType.BodyResistBuff,
        eSpellType.SpiritResistBuff, eSpellType.EnergyResistBuff, eSpellType.BodySpiritEnergyBuff, eSpellType.HeatColdMatterBuff,
        eSpellType.AllMagicResistBuff, eSpellType.BaseArmorFactorBuff, eSpellType.SpecArmorFactorBuff, eSpellType.PaladinArmorFactorBuff,
        eSpellType.AFHitsBuff, eSpellType.ArmorAbsorptionBuff, eSpellType.StrengthBuff, eSpellType.ConstitutionBuff,
        eSpellType.DexterityBuff, eSpellType.AcuityBuff, eSpellType.OffensiveProc, eSpellType.DefensiveProc,
        eSpellType.AblativeArmor, eSpellType.HealthRegenBuff
    ];

    /// <summary>A buff lasts long enough to cast before a fight: concentration (no duration) or a minute or more.</summary>
    private const int MinPrefightDurationMs = 60_000;

    public static List<Spell> PrefightBuffs(IEnumerable<Spell> spells, int level)
    {
        return spells?
            .Where(spell => spell != null && spell.Level <= level && !spell.IsHarmful && !spell.IsPulsing && !spell.NeedInstrument &&
                Array.IndexOf(PrefightOrder, spell.SpellType) >= 0 &&
                spell.Target is eSpellTarget.SELF or eSpellTarget.REALM or eSpellTarget.GROUP or eSpellTarget.PET &&
                (spell.Duration == 0 ? spell.Concentration > 0 : spell.Duration >= MinPrefightDurationMs))
            .Distinct()
            .OrderBy(spell => Array.IndexOf(PrefightOrder, spell.SpellType))
            .ThenByDescending(spell => spell.Level)
            .ToList() ?? [];
    }

    /// <summary>The spell with this name; null for none.</summary>
    public static Spell Named(IEnumerable<Spell> spells, string name)
    {
        return spells?.FirstOrDefault(spell => spell != null && spell.Name == name);
    }

    /// <summary>The highest-level spell of <paramref name="type"/> up to <paramref name="level"/>; null for none.</summary>
    public static Spell Best(IEnumerable<Spell> spells, eSpellType type, int level, bool instantOnly = false)
    {
        return spells?
            .Where(spell => spell != null && spell.SpellType == type && spell.Level <= level && (!instantOnly || spell.CastTime <= 0))
            .OrderByDescending(spell => spell.Level)
            .ThenByDescending(spell => spell.Value)
            .FirstOrDefault();
    }

    /// <summary>The highest-level instant root (an instant SpeedDecrease of 99% or more); null for none.</summary>
    public static Spell BestInstantRoot(IEnumerable<Spell> spells, int level)
    {
        return spells?
            .Where(spell => spell != null && spell.SpellType == eSpellType.SpeedDecrease && spell.CastTime <= 0 && spell.Value >= 99 && spell.Level <= level)
            .OrderByDescending(spell => spell.Level)
            .FirstOrDefault();
    }

    /// <summary>The strongest single heal that can land on oneself (target self, realm or group); null for none.</summary>
    public static Spell BestSelfHeal(IEnumerable<Spell> spells, int level)
    {
        return spells?
            .Where(spell => spell != null && spell.SpellType == eSpellType.Heal && spell.Level <= level && spell.CastTime > 0 &&
                spell.Target is eSpellTarget.SELF or eSpellTarget.REALM or eSpellTarget.GROUP)
            .OrderByDescending(spell => spell.Value)
            .ThenByDescending(spell => spell.Level)
            .FirstOrDefault();
    }

    /// <summary>The highest-level instant heal that can land on oneself; null for none.</summary>
    public static Spell BestInstantHeal(IEnumerable<Spell> spells, int level)
    {
        return spells?
            .Where(spell => spell != null && spell.SpellType == eSpellType.Heal && spell.Level <= level && spell.CastTime <= 0 &&
                spell.Target is eSpellTarget.SELF or eSpellTarget.REALM or eSpellTarget.GROUP)
            .OrderByDescending(spell => spell.Level)
            .FirstOrDefault();
    }

    /// <summary>A spell list without pet summons (AutonomousPetSupport's main pets, minions, field pets) and attack spells.</summary>
    public static List<Spell> WithoutPetsAndAttacks(IEnumerable<Spell> spells)
    {
        return spells?
            .Where(spell => spell != null && !spell.IsHarmful && !AutonomousPetSupport.IsMainPetSummon(spell.SpellType) &&
                !AutonomousPetSupport.IsAnimistFieldTurret(spell.SpellType) && spell.SpellType != eSpellType.SummonMinion)
            .ToList() ?? [];
    }
}

/// <summary>
/// Preesh's build (owner 2026-10-25): a Blademaster with 50 Blunt and 50 Celtic Dual (maces in both hands), the rest in
/// Parry, as the stock Blademaster dual-wield build spends it (BlademasterBotSpec).
/// </summary>
public sealed class PreeshBotSpec : BotSpec
{
    public PreeshBotSpec()
    {
        SpecType = eSpecType.DualWield;
        Is2H = false;
        WeaponOneType = eObjectType.Blunt;
        Add(Specs.Blunt, 50, 0.8f);
        Add(Specs.Celtic_Dual, 50, 1.0f);
        Add(Specs.Parry, 28, 0.2f);
    }
}
