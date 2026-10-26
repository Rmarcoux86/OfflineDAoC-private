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

        // Gear is picked by realm in the base constructor; only now does the champion leave its realm.
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

    /// <summary>
    /// A full template without Trials of Atlantis bonuses (owner 2026-10-25): every stat at the level-50 item cap,
    /// hit points and resists capped, +11 to all skills, on top of the generated gear (capped, so they don't add up past
    /// the caps), and every ToA percentage bonus the gear might carry taken off.
    /// </summary>
    public override void RefreshItemBonuses()
    {
        base.RefreshItemBonuses();

        for (eProperty stat = eProperty.Stat_First; stat <= eProperty.Stat_Last; stat++)
        {
            ItemBonus[stat] = Math.Max(ItemBonus[stat], ExpeditionUniques.LineCap(stat, SpawnLevel));
        }

        for (eProperty resist = eProperty.Resist_First; resist <= eProperty.Resist_Last; resist++)
        {
            ItemBonus[resist] = Math.Max(ItemBonus[resist], ExpeditionUniques.LineCap(resist, SpawnLevel));
        }

        ItemBonus[eProperty.MaxHealth] = Math.Max(ItemBonus[eProperty.MaxHealth], ExpeditionUniques.LineCap(eProperty.MaxHealth, SpawnLevel));
        ItemBonus[eProperty.AllSkills] = Math.Max(ItemBonus[eProperty.AllSkills], ExpeditionUniques.LineCap(eProperty.AllSkills, SpawnLevel));

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

    /// <summary>How long one behind attempt may take before it is given up.</summary>
    private const long BehindStepMs = 6_000;

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
                // the fight starts, so nothing pulls it early.
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
            champion.CurrentPriority = null;
            champion.HeldSlot = null;
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
    /// Behind: the stuck simulation (UT_ExpeditionGauntletSignatures) shows a challenger who keeps facing the champion
    /// (/stick) cannot be hit from behind, except within one heading update after the champion runs through them; so the
    /// attempt is made only when the challenger is not facing the champion already (they look or run elsewhere), is not
    /// stunned (that is the side chain's turn), not immune to stuns, and not mezzed (bots do not hit mezzed players,
    /// BotPvpCrowdControl), at most every 20 s. A rooted challenger can still turn, so a root does not count.
    /// Otherwise it keeps up the pressure with the signature's priority and the bot's normal picks.
    /// </summary>
    private void UpdateStep(GauntletChampion champion, GamePlayer challenger, long now)
    {
        ChampionSignature signature = champion.Signature;

        if (challenger == null)
        {
            _step = SignatureStep.None;
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
        else if (_step == SignatureStep.Behind && (stunned || now >= _stepUntil || lastStyle == signature.BehindOpener))
        {
            // Landed (a stun-immune challenger is not stunned by it), stunned, or out of time.
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
                champion.IsWithinRadius(challenger, BehindStartRange) &&
                GauntletPositioning.Classify(championAngle) != TargetArc.Front)
            {
                _step = SignatureStep.Behind;
                _stepUntil = now + BehindStepMs;
                _sprintUsed = false;
            }
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
public sealed record ChampionSignature(string[] Priority, string BehindOpener, string[] SideChain, string[] PolearmChain, string ShieldStyle)
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
