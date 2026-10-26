using System;
using System.Collections.Generic;
using System.Linq;
using DOL.AI;
using DOL.AI.Brain;
using DOL.Database;
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

    /// <summary>The player this champion fights; null once it has been removed.</summary>
    public GamePlayer Challenger { get; private set; }

    public ChampionDefinition Definition { get; }

    /// <summary>Its signature (styles and positions), as data; <see cref="ChampionSignature.None"/> for a champion without one yet.</summary>
    public ChampionSignature Signature { get; }

    /// <summary>The styles the brain wants first right now (its signature step); null for the signature's own priority.</summary>
    internal IReadOnlyList<string> CurrentPriority { get; set; }

    /// <summary>
    /// The weapon slot the brain holds (Drunkstains: shield between polearm swings). While set, every melee switch
    /// request (BotBrain's own weapon choice included) goes to this slot; null leaves the bot's choice alone.
    /// </summary>
    internal eActiveWeaponSlot? HeldSlot { get; set; }

    /// <summary>Gwyraan's best damage-over-time spell, cast only by his brain (kept out of <see cref="GameNPC.Spells"/>).</summary>
    internal Spell SignatureDot { get; private set; }

    /// <summary>Gwyraan's best damage add, kept up on himself.</summary>
    internal Spell SignatureBuff { get; private set; }

    /// <summary>Toothmouth's best instant mez, cast only by his brain (kept out of <see cref="GameNPC.Spells"/>).</summary>
    internal Spell SignatureMez { get; private set; }

    /// <summary>Toothmouth's best heal for himself.</summary>
    internal Spell SignatureHeal { get; private set; }

    /// <summary>When it last blocked an attack (GameLoop time); 0 for never.</summary>
    internal long LastBlockTick { get; private set; }

    /// <summary>The Gauntlet round this champion was spawned for.</summary>
    public int Round { get; }

    /// <summary>When its buffing ends and it waits to be challenged (GameLoop time).</summary>
    internal long BuffUntilTick { get; set; }

    /// <summary>True once it has finished buffing and waits to be challenged (it stays where it appeared).</summary>
    internal bool AtCentre { get; set; }

    /// <summary>
    /// True once it fights (owner 2026-10-25): when its challenger targets it and bows (GauntletManager.OnBow), or as
    /// soon as the challenger's side attacks it. Until then it never starts a fight.
    /// </summary>
    public bool Engaged { get; internal set; }

    public GauntletChampion(GamePlayer challenger, ChampionDefinition definition, int round, RoundTier tier)
        : base((byte) definition.ClassId, definition.Name, (byte) definition.RaceId, (byte) definition.Gender, SpawnLevel, eSpecType.None, BuildFor(definition))
    {
        Challenger = challenger;
        Definition = definition;
        Round = round;
        Signature = SignatureFor(definition?.Name);

        // Drunkstains fights with a polearm and with a one-hander and shield (owner 2026-10-25). EquipBot draws a
        // TwoHandHybrid Armsman's one-hander, polearm and shield from the item table (GameBot.SetWeapons, SetShield) and
        // leaves a slot empty when the table has nothing that fits; an empty slot gets a generated item from the
        // generator bots use for a missing weapon (BotEquipment.SetWeaponROG, GeneratedUniqueItem).
        if (Signature.PolearmChain.Length > 0 && Inventory != null)
        {
            eCharacterClass characterClass = (eCharacterClass) definition.ClassId;

            if (Inventory.GetItem(eInventorySlot.TwoHandWeapon) == null)
            {
                BotEquipment.SetWeaponROG(this, Realm, characterClass, SpawnLevel, eObjectType.PolearmWeapon, eInventorySlot.TwoHandWeapon, eDamageType.Slash);
            }

            if (Inventory.GetItem(eInventorySlot.RightHandWeapon) == null)
            {
                BotEquipment.SetWeaponROG(this, Realm, characterClass, SpawnLevel, eObjectType.SlashingWeapon, eInventorySlot.RightHandWeapon, eDamageType.Slash);
            }

            if (Inventory.GetItem(eInventorySlot.LeftHandWeapon) == null)
            {
                // A shield's Type_Damage is its size (BotEquipment.SetShield); 3 is a large shield.
                BotEquipment.SetWeaponROG(this, Realm, characterClass, SpawnLevel, eObjectType.Shield, eInventorySlot.LeftHandWeapon, (eDamageType) 3);
            }

            SwitchWeapon(eActiveWeaponSlot.TwoHanded);
        }

        if (Signature.RangedDot)
        {
            // Gwyraan: a staff in both hands, like his look. GameBot.SetWeapons gives a Druid (a hybrid) a one-handed
            // weapon of WeaponOneType, and no staff is one-handed, so the staff is generated. Whether a Druid may use a
            // staff is the database's ability table (AbstractServerRules.CheckAbilityToUseItem, "Weaponry: Staves");
            // if not, he fights with the Blunt one-hander the stock DruidBotSpec uses.
            eCharacterClass characterClass = (eCharacterClass) definition.ClassId;

            if (Inventory != null && Inventory.GetItem(eInventorySlot.TwoHandWeapon) == null)
            {
                BotEquipment.SetWeaponROG(this, Realm, characterClass, SpawnLevel, eObjectType.Staff, eInventorySlot.TwoHandWeapon, eDamageType.Crush);
            }

            if (Inventory != null && BotWeaponStats.CanUseMelee(this, Inventory.GetItem(eInventorySlot.TwoHandWeapon)))
            {
                SwitchWeapon(eActiveWeaponSlot.TwoHanded);
            }
            else if (Inventory != null)
            {
                BotSpec.WeaponOneType = eObjectType.Blunt;
                BotSpec.Is2H = false;

                if (Inventory.GetItem(eInventorySlot.RightHandWeapon) == null)
                {
                    BotEquipment.SetWeaponROG(this, Realm, characterClass, SpawnLevel, eObjectType.Blunt, eInventorySlot.RightHandWeapon, eDamageType.Crush);
                }

                SwitchWeapon(eActiveWeaponSlot.Standard);
            }

            // No pet (owner 2026-10-25), no attack spell but his DoT, which only his brain casts; heals and buffs stay.
            SignatureDot = ChampionSpells.Best(Spells, eSpellType.DamageOverTime, Level);
            SignatureBuff = ChampionSpells.Best(Spells, eSpellType.DamageAdd, Level);
            Spells = ChampionSpells.WithoutPetsAndAttacks(Spells);
        }

        if (Signature.MezRetreat)
        {
            // Toothmouth: the stock Bard plan equips a one-handed blade (SetWeapons' Instrument case) and a shield; a
            // missing blade is generated so he can always melee.
            if (Inventory != null && Inventory.GetItem(eInventorySlot.RightHandWeapon) == null)
            {
                BotEquipment.SetWeaponROG(this, Realm, (eCharacterClass) definition.ClassId, SpawnLevel, eObjectType.Blades, eInventorySlot.RightHandWeapon, eDamageType.Slash);
                SwitchWeapon(eActiveWeaponSlot.Standard);
            }

            // His mez is his signature's alone: the bot's own crowd control never casts it.
            SignatureMez = ChampionSpells.Best(Spells, eSpellType.Mesmerize, Level, true);
            SignatureHeal = ChampionSpells.BestSelfHeal(Spells, Level);
            Spells = Spells.Where(spell => spell.SpellType != eSpellType.Mesmerize).ToList();
        }

        // Gear is picked by realm in the base constructor; only now does the champion leave its realm.
        _homeRealm = Realm;
        Realm = eRealm.None;

        // Escalation (GauntletEscalation), as far as the prototype applies it: the realm rank, and the open-ended stat
        // bonus from round 21. Realm abilities, potions, gear tiers, affixes and helpers come in Phase 2.
        RealmLevel = Math.Max(0, (tier.RealmRank - 1) * 10);

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
        RollEpicGear();

        // Its last name is its realm rank's title in its class's home realm (owner 2026-10-25: "Ios Raven Ardent").
        Name = $"{definition.Name} {RankTitle(definition, tier.RealmRank)}";
        RefreshItemBonuses();
        Health = MaxHealth;

        // The leash is measured from SpawnPoint: where it appears (GameNPC.AddToWorld sets it).
        TetherRange = GauntletRules.LeashRadius;

        GauntletChampionBrain brain = new() { IsHealer = (Brain as BotBrain)?.IsHealer ?? false };
        SetOwnBrain(brain);
    }

    /// <summary>
    /// Styles this champion uses first whenever one can be used, in order (BotMeleeStylePolicy.Select, at every swing).
    /// The brain sets them for its current signature step; otherwise they are the signature's own priority. These may be
    /// positional: Select lets the champion's own priority styles through BotMeleeStylePolicy.IsEligible's positional
    /// exclusion, and StyleProcessor.CanUseStyle still decides whether the position is right.
    /// </summary>
    public IReadOnlyList<string> PriorityStyles => CurrentPriority ?? Signature?.Priority ?? Array.Empty<string>();

    /// <summary>
    /// The signatures (owner 2026-10-25), by Style.Name in the server's style table.
    /// Ios (Scythe): she runs behind her challenger for Damaging Grasp (back positional, 8 s stun), then from the side
    /// Stunning Blade (side positional), Winter's Scythe, Blizzard Blade.
    /// Preesh (Blunt, Celtic Dual): Devastating Blow, the 10 s stun that follows Stunning Blow, then Stunning Blow, the
    /// style that opens off a parry; while the challenger is stunned, from the side Ice Storm (side positional), Tempest,
    /// Supernova.
    /// Drunkstains (Polearm, Shields): shield held between polearm swings; the polearm swings Phalanx (front positional),
    /// then Defender's Revenge (9 s stun); after a block, Slam (9 s stun) from the shield.
    /// </summary>
    public static ChampionSignature SignatureFor(string name)
    {
        return name switch
        {
            "Ios" => new ChampionSignature([], "Damaging Grasp", ["Stunning Blade", "Winter's Scythe", "Blizzard Blade"], [], null),
            "Preesh" => new ChampionSignature(["Devastating Blow", "Stunning Blow"], null, ["Ice Storm", "Tempest", "Supernova"], [], null),
            "Drunkstains" => new ChampionSignature([], null, [], ["Phalanx", "Defender's Revenge"], "Slam"),
            "Gwyraan" => new ChampionSignature([], null, [], [], null, RangedDot: true),
            "Toothmouth" => new ChampionSignature([], null, [], [], null, MezRetreat: true),
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
    /// Epic gear (owner 2026-10-26: "maybe they're just in epic gear", so players who reach the Gauntlet with drop gear
    /// can stand up to them): every armor and jewelry piece is rolled again as an Expedition Epic of its type and slot for
    /// the champion's class and realm, keeping the piece's look. Weapons and shields stay the class's own.
    /// </summary>
    private void RollEpicGear()
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

            GeneratedUniqueItem rolled = ExpeditionLoot.RollFor(_homeRealm, (eCharacterClass) CharacterClass.ID, SpawnLevel, (eObjectType) item.Object_Type, (eInventorySlot) item.Item_Type, eLootRarity.Epic);

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

    /// <summary>The gear's bonuses without any Trials of Atlantis percentage bonus (owner 2026-10-25: no ToA).</summary>
    public override void RefreshItemBonuses()
    {
        base.RefreshItemBonuses();

        foreach (eProperty toa in new[] { eProperty.MeleeDamage, eProperty.StyleDamage, eProperty.MeleeSpeed, eProperty.SpellDamage, eProperty.CastingSpeed, eProperty.SpellDuration })
        {
            ItemBonus[toa] = 0;
        }
    }

    /// <summary>Every champion is of the SmuG guild (GameBot.AddToWorld would show its own bot guild otherwise).</summary>
    public override string GuildName
    {
        get { return GauntletRules.GuildName; }
        set { base.GuildName = GauntletRules.GuildName; }
    }

    /// <summary>
    /// An attack by the challenger's side starts the fight, bow or no bow. A blocked attack is noted for Drunkstains's
    /// Slam (GameLiving.OnAttackedByEnemy runs for every attack received, whatever its result).
    /// </summary>
    public override void OnAttackedByEnemy(AttackData ad)
    {
        if (!Engaged && ad != null && IsChallengerSide(ad.Attacker))
        {
            Engaged = true;
        }

        if (ad != null && ad.AttackResult == eAttackResult.Blocked && ad.Target == this)
        {
            LastBlockTick = GameLoop.GameLoopTime;
        }

        base.OnAttackedByEnemy(ad);
    }

    /// <summary>No corpse: GameNPC.ProcessDeath deletes the body, and RespawnInterval -1 keeps it gone.</summary>
    protected override bool RetainCorpseForResurrection => false;

    /// <summary>
    /// A champion kill gives the normal kill rewards (experience, realm points): GameNPC.RewardStatus denies every NPC with
    /// an IControlledBrain, and BotBrain is one, so it is allowed here.
    /// </summary>
    public override RewardEligibility RewardStatus => RewardEligibility.Eligible;

    public override void ProcessDeath(GameObject killer)
    {
        base.ProcessDeath(killer);

        // Only a real death counts as a kill; Remove() deletes without coming here.
        if (Health <= 0)
        {
            GauntletManager.OnChampionDied(this);
        }
    }

    /// <summary>Takes the champion out of the world for good, without counting a kill.</summary>
    public void Remove()
    {
        Challenger = null;

        if (ObjectState != eObjectState.Deleted)
        {
            Delete();
        }
    }

    /// <summary>True for the challenger and anything the challenger controls (pets).</summary>
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
                Level = 50,
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
/// The champion's brain: the bot's own combat AI (styles, spells, heals) with three changes. It buffs and waits
/// before fighting; it fights only its challenger's side and keeps the challenger on its aggro list (BotBrain's AGGRO
/// state drops out after 6 s without combat); and it is leashed to where it appeared (BotBrain ignores TetherRange).
/// In the fight it plays its <see cref="ChampionSignature"/>: the positional steps (behind, side) and Drunkstains's
/// shield/polearm swap.
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

    /// <summary>How long one behind attempt may take before it is given up (a run through waits for a swing it can time).</summary>
    private const long BehindStepMs = 8_000;

    /// <summary>A challenger whose facing is within this many degrees of the champion gets the run through.</summary>
    private const float RunThroughMaxAngle = 30;

    /// <summary>The run through stops this far past the challenger (inside IsObjectInFront's 32 always-in-front units).</summary>
    private const int RunThroughStopPast = 20;

    /// <summary>
    /// While a run through waits for its moment the brain thinks every loop tick: NpcService schedules the next think at
    /// GameLoopTime + ThinkInterval and ShouldTick's half-tick tolerance makes 1 ms the next tick.
    /// </summary>
    private const int RunThroughThinkMs = 1;

    /// <summary>Meanwhile the bot's own fight (BotBrain.AttackMostWanted) runs at its usual pace.</summary>
    private const long PressureThinkMs = 500;

    /// <summary>The wait after a behind attempt before the next one (owner 2026-10-25: "e.g. every 20 s").</summary>
    private const long BehindRetryMs = 20_000;

    /// <summary>How long the side chain may take.</summary>
    private const long SideStepMs = 12_000;

    /// <summary>A stun with less than this left is not worth walking to the side for.</summary>
    private const long MinStunForSideMs = 2_000;

    /// <summary>
    /// A champion's sprint: MaxSpeedCalculator multiplies a player's speed by 1.3 while sprinting (its private SPRINT
    /// constant) but never an NPC's, and a GameBot is a GameNPC, so GameBot.Sprint's effect would not make it faster.
    /// The sprint is the same factor on MaxSpeedBase (the base speed bots set to PLAYER_BASE_SPEED), for a short burst.
    /// </summary>
    private const double SprintFactor = 1.3;

    private const long SprintMs = 4_000;

    /// <summary>Drunkstains takes up the polearm this long before a swing is due, on top of a think interval.</summary>
    private const long SwapMarginMs = 150;

    /// <summary>Slam follows a block within this long (the 3 s reactionary window, AttackAction.IsAllowedToTick).</summary>
    private const long SlamWindowMs = 3_000;

    /// <summary>Gwyraan's DoT is for a challenger beyond melee reach: the attack range plus this margin.</summary>
    private const int DotMeleeMargin = 50;

    /// <summary>A signature cast (DoT, self-buff) is tried again no sooner than this after the last one ended.</summary>
    private const long SpellRetryMs = 4_000;

    /// <summary>Toothmouth waits this long for his instant mez to show on the challenger before counting it resisted.</summary>
    private const long MezLandWindowMs = 1_500;

    /// <summary>Toothmouth retreats this share of the leash radius past his spot (owner: "~80% of the leash").</summary>
    private const double RetreatLeashShare = 0.8;

    private enum SignatureStep
    {
        None,
        Behind,
        Side
    }

    private long _nextHuntTick;
    private SignatureStep _step;
    private long _stepUntil;
    private long _nextBehindTick;
    private bool _sideDone;
    private bool _sprintUsed;
    private long _sprintUntil;
    private short _speedBeforeSprint;
    private bool _behindRunThrough;
    private long _runSwingTick;
    private long _nextPressureTick;
    private int _thinkIntervalBeforeRun;
    private long _nextDotTick;
    private long _nextBuffTick;
    private bool _mezUsed;
    private long _mezCastTick;
    private bool _retreating;
    private Point3D _retreatPoint;

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
            if (now < champion.BuffUntilTick)
            {
                // Buffing at the gate: the idle state casts its defensive spells. AddToAggroList refuses everyone until
                // the fight starts, so nothing pulls it early. A signature self-buff (Gwyraan's damage add) goes first.
                if (MaintainSignatureBuff(champion, now))
                {
                    return;
                }

                base.Think();

                return;
            }

            if (!champion.AtCentre)
            {
                // It stays where it appeared (owner 2026-10-25: no walk to the middle) and waits there.
                champion.StopMoving();
                champion.AtCentre = true;
                GauntletManager.OnChampionReady(champion);

                return;
            }

            // Waiting to be challenged: buffs kept up, facing the challenger, back to its spot if anything moved it.
            if (MaintainSignatureBuff(champion, now))
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

            return;
        }

        if (champion.IsOutOfTetherRange)
        {
            // Leash: drop the fight and go back to its spot.
            EndSprint(champion);
            _step = SignatureStep.None;
            EndRunThrough();
            champion.CurrentPriority = null;
            champion.HeldSlot = null;
            _retreating = false;
            _mezCastTick = 0;
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

        // Toothmouth's mez and retreat owns the think while it runs: no BotBrain fight touches the mezzed challenger.
        if (champion.Signature.MezRetreat && ThinkMezRetreat(champion, challenger, now))
        {
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

        bool challengerHere = challenger != null && challenger.IsAlive &&
            challenger.ObjectState == GameObject.eObjectState.Active && challenger.CurrentRegion == champion.CurrentRegion;

        if (challengerHere)
        {
            if (challenger.IsStealthed && GetBaseAggroAmount(challenger) <= 0)
            {
                // It cannot see a hidden challenger, so it hunts where they are; GamePlayer.UncoverStealthAction's
                // normal rules reveal them once it is close.
                if (now >= _nextHuntTick)
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

        if (_step == SignatureStep.Behind && _behindRunThrough && !champion.IsIncapacitated)
        {
            RunThrough(champion, challenger, now);

            return;
        }

        if (_step != SignatureStep.None && !champion.IsIncapacitated)
        {
            MoveForStep(champion, challenger, now);

            return;
        }

        EndSprint(champion);
        base.AttackMostWanted();

        if (champion.Signature.PolearmChain.Length > 0)
        {
            HoldShieldOrPolearm(champion, now);
        }
    }

    /// <summary>
    /// The signature step and the styles it wants first.
    /// Side: while the challenger is stunned (Ios's Damaging Grasp, Preesh's Devastating Blow, or any other stun) it
    /// stands at their side for the side chain; a stunned player cannot turn, so the side stays the side.
    /// Behind: a challenger facing the champion (/stick) gets a run through timed to her swing (<see cref="RunThrough"/>);
    /// one facing elsewhere gets a walk to the spot behind them. Either only while the challenger is not stunned (that is
    /// the side chain's turn), not immune to stuns, and not mezzed (bots do not hit mezzed players, BotPvpCrowdControl),
    /// at most every 20 s. A rooted challenger can still turn, so a root does not count.
    /// Otherwise it keeps up the pressure with the signature's priority and the bot's normal picks.
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
        AttackData last = champion.attackComponent.attackAction.LastAttackData;
        string lastStyle = last != null && last.AttackResult == eAttackResult.HitStyle ? last.Style?.Name : null;
        int chainIndex = lastStyle == null ? -1 : Array.IndexOf(signature.SideChain, lastStyle);

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
        else if (_step == SignatureStep.Behind && (stunned || now >= _stepUntil || lastStyle == signature.BehindOpener ||
            _runSwingTick > 0 && now > _runSwingTick + (long) (2 * GameLoop.TickDuration)))
        {
            // Landed (a stun-immune challenger is not stunned by it), stunned, out of time, or the timed swing is past.
            _step = SignatureStep.None;
            _nextBehindTick = now + BehindRetryMs;
        }

        if (_step == SignatureStep.None)
        {
            long stunLeft = EffectListService.GetEffectOnTarget(challenger, eEffect.Stun)?.GetRemainingTimeForClient() ?? 0;
            float championAngle = GauntletPositioning.AngleFrom(challenger.X, challenger.Y, challenger.Heading, champion.X, champion.Y);

            // Owner 2026-10-25: no run for the back stun while the challenger is immune to stuns. The same test as
            // CCSpellHandler.CheckSpellResist, which StyleStun (a StunSpellHandler) goes through: the StunImmunity
            // effect a stun leaves on a player when it ends (StyleStun.OnEffectExpires: five times the stun), or the
            // StunImmunity ability.
            bool stunImmune = challenger.effectListComponent.ContainsEffectForEffectType(eEffect.StunImmunity) ||
                challenger.HasAbility(Abilities.StunImmunity);

            if (signature.SideChain.Length > 0 && stunned && !_sideDone && stunLeft >= MinStunForSideMs)
            {
                _step = SignatureStep.Side;
                _stepUntil = now + SideStepMs;
                _sprintUsed = false;
            }
            else if (signature.BehindOpener != null && !stunned && !stunImmune && !challenger.IsMezzed && now >= _nextBehindTick &&
                champion.IsWithinRadius(challenger, BehindStartRange))
            {
                bool facingHer = championAngle <= RunThroughMaxAngle || championAngle >= 360 - RunThroughMaxAngle;

                if (facingHer || GauntletPositioning.Classify(championAngle) != TargetArc.Front)
                {
                    _step = SignatureStep.Behind;
                    _stepUntil = now + BehindStepMs;
                    _sprintUsed = false;
                    _behindRunThrough = facingHer;
                    _runSwingTick = 0;
                }
            }
        }

        if (_step != SignatureStep.Behind || !_behindRunThrough)
        {
            EndRunThrough();
        }

        champion.CurrentPriority = _step switch
        {
            SignatureStep.Side => Enumerable.Reverse(signature.SideChain).Concat(signature.Priority).ToArray(),
            SignatureStep.Behind => new[] { signature.BehindOpener }.Concat(signature.Priority).ToArray(),
            _ => signature.Priority.Concat(Enumerable.Reverse(signature.PolearmChain)).ToArray()
        };
    }

    /// <summary>
    /// Moves to the step's spot (re-plotted every think as the challenger moves or turns), sprinting on the way: a
    /// straight walk, so from in front it runs through the challenger to their back. At the spot it stops and faces
    /// the challenger, as StyleProcessor's positional check also needs the champion to face its target.
    /// </summary>
    private void MoveForStep(GauntletChampion champion, GamePlayer challenger, long now)
    {
        champion.TargetObject = challenger;

        if (!champion.attackComponent.AttackState)
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
            StartSprint(champion, now);
            champion.WalkTo(point, champion.MaxSpeed);

            return;
        }

        EndSprint(champion);

        if (champion.IsMoving)
        {
            champion.StopMoving();
        }

        champion.TurnTo(challenger);
    }

    /// <summary>
    /// Gwyraan's spells in the fight (owner 2026-10-25: "attempt to cast a nature dot and just auto attack otherwise"):
    /// his attack-spell turn (BotBrain.AttackMostWanted, and NpcAttackAction.PrepareMeleeAttack before every swing) is
    /// only his DoT, and never a crowd control. Heals and buffs stay the class's own (Defensive).
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
    /// His best Nature DoT on the challenger when they are beyond melee reach, do not carry it, and he is not already
    /// casting; a cast that ended (landed or interrupted) waits <see cref="SpellRetryMs"/> before the next try. Before
    /// the DoT, a missing damage add goes back up.
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

        if (dot == null || challenger == null || !challenger.IsAlive || challenger.CurrentRegion != champion.CurrentRegion ||
            champion.IsWithinRadius(challenger, champion.attackComponent.AttackRange + DotMeleeMargin))
        {
            return false;
        }

        if (MaintainSignatureBuff(champion, now))
        {
            return true;
        }

        if (now < _nextDotTick || LivingHasEffect(challenger, dot))
        {
            return false;
        }

        _nextDotTick = now + SpellRetryMs;
        champion.TargetObject = challenger;

        return champion.CastSpell(dot, m_mobSpellLine);
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
        GameObject previousTarget = champion.TargetObject;
        champion.TargetObject = champion;
        bool cast = champion.CastSpell(buff, m_mobSpellLine);

        if (!cast)
        {
            champion.TargetObject = previousTarget;
        }

        return cast;
    }

    /// <summary>
    /// Toothmouth (owner 2026-10-25): once per fight, the first time he is at half health or less, his best instant mez
    /// on the challenger; if it shows on them, he stops attacking, sprints to a point past his spot away from them (within
    /// the leash) and heals himself with his best heal until he is full or the mez is gone. A resisted mez, or a
    /// mez-immune challenger, spends the one use. True while this owns the think (no BotBrain turn: nothing hits the
    /// mezzed challenger).
    /// </summary>
    private bool ThinkMezRetreat(GauntletChampion champion, GamePlayer challenger, long now)
    {
        bool challengerHere = challenger != null && challenger.IsAlive &&
            challenger.ObjectState == GameObject.eObjectState.Active && challenger.CurrentRegion == champion.CurrentRegion;

        if (_retreating)
        {
            bool full = champion.Health >= champion.MaxHealth;

            if (!challengerHere || !challenger.IsMezzed || full && !champion.IsCasting)
            {
                // Back to the fight: the next think is the bot's own again.
                _retreating = false;
                EndSprint(champion);

                return false;
            }

            if (champion.attackComponent.AttackState)
            {
                champion.StopAttack();
            }

            if (!champion.IsWithinRadius(_retreatPoint, ArrivedDistance * 2, true))
            {
                if (!champion.IsMoving)
                {
                    StartSprint(champion, now);
                    champion.PathTo(_retreatPoint, champion.MaxSpeed);
                }

                return true;
            }

            EndSprint(champion);

            if (champion.IsMoving)
            {
                champion.StopMoving();
            }

            Spell heal = champion.SignatureHeal;

            if (heal != null && !full && !champion.IsCasting && champion.castingComponent?.HasPendingSkillRequests != true)
            {
                champion.TargetObject = champion;
                champion.CastSpell(heal, m_mobSpellLine);
            }

            return true;
        }

        if (_mezCastTick > 0)
        {
            if (challengerHere && challenger.IsMezzed)
            {
                _mezCastTick = 0;
                _retreating = true;
                _sprintUsed = false;
                (int x, int y) = GauntletPositioning.RetreatPoint(champion.SpawnPoint.X, champion.SpawnPoint.Y, challenger.X, challenger.Y,
                    (int) (GauntletRules.LeashRadius * RetreatLeashShare));
                _retreatPoint = new Point3D(x, y, champion.SpawnPoint.Z);
                champion.StopAttack();
                champion.StopFollowing();
                champion.TargetObject = null;

                return true;
            }

            if (now - _mezCastTick < MezLandWindowMs && challengerHere)
            {
                // The instant cast is on its way; hold the swings so nothing breaks a mez that lands.
                return true;
            }

            // Resisted: the one use is spent.
            _mezCastTick = 0;

            return false;
        }

        Spell mez = champion.SignatureMez;

        if (_mezUsed || mez == null || !challengerHere || champion.HealthPercent > 50)
        {
            return false;
        }

        _mezUsed = true;

        if (challenger.IsMezzed || challenger.effectListComponent.ContainsEffectForEffectType(eEffect.MezImmunity))
        {
            return false;
        }

        champion.StopAttack();

        if (champion.IsCasting)
        {
            champion.StopCurrentSpellcast();
        }

        champion.TargetObject = challenger;

        if (!champion.CastSpell(mez, m_mobSpellLine))
        {
            return false;
        }

        _mezCastTick = now;

        return true;
    }

    /// <summary>
    /// Ios's run through a challenger who faces her (owner 2026-10-25: get the back stun off a stuck challenger). The
    /// server knows a player's heading only from their client's packets (ClientService.BeginTick, the start of a loop
    /// tick), so right after she passes them their heading still points where she came from. A swing fires on the first
    /// loop tick within half a tick of AttackAction.NextTick (GameServiceUtils.ShouldTick), in AttackService, which runs
    /// before MovementService: it sees her where the tick before moved her. She keeps fighting until the tick from which
    /// a walk, at up to sprint speed, puts her half a tick's travel past the challenger on the tick before that swing
    /// (GauntletPositioning.RunThroughSpeed), then walks straight through them and stops 20 units past, so the swing
    /// checks Damaging Grasp from behind (within 32 units she counts as facing them). It fails only when the
    /// challenger's client sends a heading in that last tick (UT_ExpeditionGauntletSignatures measures the rates).
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
        double topSpeed = Math.Floor(champion.Endurance > 10 && !_sprintUsed ? champion.MaxSpeed * SprintFactor : champion.MaxSpeed);

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

        if (speed > champion.MaxSpeed)
        {
            StartSprint(champion, now);
        }

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

    /// <summary>One sprint burst per step (see <see cref="SprintFactor"/>); it needs some endurance, as GameBot.Sprint does.</summary>
    private void StartSprint(GauntletChampion champion, long now)
    {
        if (_sprintUntil > 0)
        {
            if (now >= _sprintUntil)
            {
                EndSprint(champion);
            }

            return;
        }

        if (_sprintUsed || champion.Endurance <= 10)
        {
            return;
        }

        _sprintUsed = true;
        _speedBeforeSprint = champion.MaxSpeedBase;
        champion.MaxSpeedBase = (short) Math.Round(_speedBeforeSprint * SprintFactor);
        _sprintUntil = now + SprintMs;
    }

    private void EndSprint(GauntletChampion champion)
    {
        if (_sprintUntil == 0)
        {
            return;
        }

        champion.MaxSpeedBase = _speedBeforeSprint;
        _sprintUntil = 0;
    }

    /// <summary>
    /// Drunkstains: the one-hander and shield (Standard) between swings so he can block, the polearm (TwoHanded) for the
    /// swing. The next swing's time does not depend on the slot: AttackAction adds the interval of the weapon that last
    /// swung (FinalizeMeleeAttack, AttackSpeed(_weapon)) to _nextMeleeTick, and GameNPC.SwitchWeapon's StopAttack and
    /// RequestStartAttack leave _nextMeleeTick alone (OnStopAttack only resets the ranged tick). So the polearm is taken
    /// up when the swing is due within a think interval (plus a margin), and the shield again at the next think after
    /// it. After a block, Slam is wanted when StyleProcessor would accept it; he then keeps the shield for that swing.
    /// </summary>
    private void HoldShieldOrPolearm(GauntletChampion champion, long now)
    {
        IGameInventory inventory = champion.Inventory;
        DbInventoryItem shield = inventory?.GetItem(eInventorySlot.LeftHandWeapon);
        bool kit = inventory?.GetItem(eInventorySlot.TwoHandWeapon) != null && inventory.GetItem(eInventorySlot.RightHandWeapon) != null &&
            shield?.Object_Type == (int) eObjectType.Shield;

        if (!kit || !champion.attackComponent.AttackState || champion.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
        {
            champion.HeldSlot = null;

            return;
        }

        string slamName = champion.Signature.ShieldStyle;
        Style slam = slamName == null ? null : champion.Styles?.FirstOrDefault(style => style.Name == slamName);
        AttackData last = champion.attackComponent.attackAction.LastAttackData;
        bool slamNow = slam != null && now - champion.LastBlockTick <= SlamWindowMs && slam.Level <= champion.Level &&
            StyleProcessor.CheckEnduranceCost(champion, shield, slam) && champion.CheckStyleStun(slam) &&
            StyleProcessor.CanUseStyle(last, champion, slam, shield);

        if (slamNow)
        {
            champion.CurrentPriority = champion.PriorityStyles.Append(slamName).ToArray();
        }

        long untilSwing = champion.attackComponent.attackAction.NextTick - now;
        eActiveWeaponSlot wanted = !slamNow && untilSwing <= ThinkInterval + SwapMarginMs ? eActiveWeaponSlot.TwoHanded : eActiveWeaponSlot.Standard;

        if (champion.HeldSlot != wanted || champion.ActiveWeaponSlot != wanted)
        {
            champion.HeldSlot = wanted;
            champion.SwitchWeapon(wanted);
        }
    }
}

/// <summary>
/// A champion's signature as data (owner 2026-10-25), by Style.Name in the server's style table.
/// </summary>
/// <param name="Priority">Styles used first whenever one can be used, in order.</param>
/// <param name="BehindOpener">A back positional the champion goes behind its challenger to land; null for none.</param>
/// <param name="SideChain">A side positional and its follow-ups, in chain order, swung from the challenger's side while the
/// challenger is stunned.</param>
/// <param name="PolearmChain">A two-handed chain in chain order (its opener may be a front positional), swung with the
/// polearm, the shield held between swings.</param>
/// <param name="ShieldStyle">A shield style used after a block; null for none.</param>
/// <param name="RangedDot">Gwyraan: his best DoT on a challenger beyond melee reach, melee otherwise, no other attack spell.</param>
/// <param name="MezRetreat">Toothmouth: once, at half health, an instant mez, a retreat and self-heals.</param>
public sealed record ChampionSignature(string[] Priority, string BehindOpener, string[] SideChain, string[] PolearmChain, string ShieldStyle,
    bool RangedDot = false, bool MezRetreat = false)
{
    public static readonly ChampionSignature None = new([], null, [], [], null);
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
/// Drunkstains's build (owner 2026-10-25): the stock ArmsmanBotSpec TwoHandHybrid, 50 Polearm, 39 Slash, 42 Shields,
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
        Add(ObjToSpec(WeaponOneType), 39, 0.6f);
        Add(ObjToSpec(WeaponTwoType), 50, 1.0f);
        Add(Specs.Shields, 42, 0.5f);
        Add(Specs.Parry, 18, 0.1f);
    }
}

/// <summary>
/// Gwyraan's build (owner 2026-10-25): 50 Nature, so Defedation (the level-48 Nature DoT) and Hooks of the Wild (the
/// level-46 damage add) are his, and the rest in Regrowth with the last few points in Nurture: a Druid has 1494 points
/// at 50 (GameBot.GetSpecPointsForLevel, multiplier 10, with the creation bonus), Nature 50 costs 1274, Regrowth 20
/// 209 and Nurture 4 9, leaving 2 (less than any next level). A staff, two-handed, like his look.
/// </summary>
public sealed class GwyraanBotSpec : BotSpec
{
    public GwyraanBotSpec()
    {
        WeaponOneType = eObjectType.Staff;
        Is2H = true;
        SpecType = eSpecType.NatureDruid;
        Add(Specs.Nature, 50, 1.0f);
        Add(Specs.Regrowth, 20, 0.4f);
        Add(Specs.Nurture, 4, 0.1f);
    }
}

/// <summary>
/// Toothmouth's build (owner 2026-10-25, the healer bard): the stock BardBotSpec's second variant, 37 Music,
/// 43 Nurture, 33 Regrowth, with its instruments (SpecType Instrument) and a one-handed blade like his look.
/// </summary>
public sealed class ToothmouthBotSpec : BotSpec
{
    public ToothmouthBotSpec()
    {
        WeaponOneType = eObjectType.Blades;
        SpecType = eSpecType.Instrument;
        Add(Specs.Music, 37, 0.4f);
        Add(Specs.Nurture, 43, 0.9f);
        Add(Specs.Regrowth, 33, 0.7f);
    }
}

/// <summary>Picks the champions' signature spells from a known spell list (pure, unit-tested).</summary>
public static class ChampionSpells
{
    /// <summary>The highest-level spell of <paramref name="type"/> up to <paramref name="level"/>; null for none.</summary>
    public static Spell Best(IEnumerable<Spell> spells, eSpellType type, int level, bool instantOnly = false)
    {
        return spells?
            .Where(spell => spell != null && spell.SpellType == type && spell.Level <= level && (!instantOnly || spell.CastTime <= 0))
            .OrderByDescending(spell => spell.Level)
            .ThenByDescending(spell => spell.Value)
            .FirstOrDefault();
    }

    /// <summary>The strongest single heal that can land on oneself (target self, realm or group); null for none.</summary>
    public static Spell BestSelfHeal(IEnumerable<Spell> spells, int level)
    {
        return spells?
            .Where(spell => spell != null && spell.SpellType == eSpellType.Heal && spell.Level <= level &&
                spell.Target is eSpellTarget.SELF or eSpellTarget.REALM or eSpellTarget.GROUP)
            .OrderByDescending(spell => spell.Value)
            .ThenByDescending(spell => spell.Level)
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
