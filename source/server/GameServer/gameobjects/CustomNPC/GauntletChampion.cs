using System;
using System.Collections.Generic;
using System.Linq;
using DOL.AI;
using DOL.AI.Brain;
using DOL.Database;

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
    /// Styles this champion uses first whenever one can be used, in order (BotMeleeStylePolicy.Select). Preesh's is the
    /// Blunt parry chain (owner 2026-10-25): Devastating Blow, the 10-second stun that follows Stunning Blow, then
    /// Stunning Blow itself, the style that opens off a parry.
    /// </summary>
    public IReadOnlyList<string> PriorityStyles => string.Equals(Definition?.Name, "Preesh", StringComparison.Ordinal) ? PreeshChain : Array.Empty<string>();

    private static readonly string[] PreeshChain = ["Devastating Blow", "Stunning Blow"];

    /// <summary>A champion's fixed build, or null for its class's usual one.</summary>
    private static BotSpec BuildFor(ChampionDefinition definition)
    {
        return string.Equals(definition?.Name, "Preesh", StringComparison.Ordinal) ? new PreeshBotSpec() : null;
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

    /// <summary>An attack by the challenger's side starts the fight, bow or no bow.</summary>
    public override void OnAttackedByEnemy(AttackData ad)
    {
        if (!Engaged && ad != null && IsChallengerSide(ad.Attacker))
        {
            Engaged = true;
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
/// </summary>
public sealed class GauntletChampionBrain : BotBrain
{
    /// <summary>The aggro the challenger is topped up to every think.</summary>
    private const long ChallengerAggro = 10_000;

    /// <summary>While waiting, a champion moved further than twice this from its spot walks back.</summary>
    private const int CentreReachedDistance = 150;

    /// <summary>How often a champion hunting a stealthed challenger re-plots its walk.</summary>
    private const long HuntRepathMs = 3_000;

    private long _nextHuntTick;

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
