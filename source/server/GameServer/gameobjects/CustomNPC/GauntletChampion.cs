using System;
using System.Linq;
using DOL.AI;
using DOL.AI.Brain;
using DOL.Database;

namespace DOL.GS.Expedition.Gauntlet;

/// <summary>
/// A Gauntlet champion (docs/EXPEDITION-GAUNTLET-PLAN.md, Approach A): an ownerless level-50 GameBot of the champion's
/// class, with realm None so the challenger may attack it and it may attack the challenger without any server-rule
/// change. It is a throwaway: no owner, no database row, no BotManager or autonomous registration, no corpse and no
/// respawn. It walks in from the gate, buffs at the arena centre, then fights only its challenger and the challenger's
/// pets, within <see cref="GauntletRules.LeashRadius"/> of the centre. Its death is reported to
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

    /// <summary>When it spawned at the gate (GameLoop time).</summary>
    internal long WalkInStartTick { get; set; }

    /// <summary>When its buffing at the centre ends and it attacks; 0 while it is still walking in.</summary>
    internal long EngageAtTick { get; set; }

    /// <summary>True once it fights: after its buffing time, or as soon as the challenger's side hits it.</summary>
    public bool Engaged { get; internal set; }

    public GauntletChampion(GamePlayer challenger, ChampionDefinition definition, int round, RoundTier tier)
        : base((byte) definition.ClassId, definition.Name, (byte) definition.RaceId, (byte) definition.Gender, SpawnLevel)
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

        // The leash is measured from SpawnPoint, which GauntletManager sets to the arena centre after AddToWorld (GameNPC.
        // AddToWorld makes the gate it appears at the spawn point).
        TetherRange = GauntletRules.LeashRadius;

        GauntletChampionBrain brain = new() { IsHealer = (Brain as BotBrain)?.IsHealer ?? false };
        SetOwnBrain(brain);
    }

    /// <summary>No corpse: GameNPC.ProcessDeath deletes the body, and RespawnInterval -1 keeps it gone.</summary>
    protected override bool RetainCorpseForResurrection => false;

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

        if (!ExpeditionBossLooks.Apply(this, Name))
        {
            Inventory = realInventory;

            return;
        }

        IGameInventory looks = Inventory;
        Inventory = realInventory;

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
/// The champion's brain: the bot's own combat AI (styles, spells, heals) with three changes. It walks in and buffs
/// before fighting; it fights only its challenger's side and keeps the challenger on its aggro list (BotBrain's AGGRO
/// state drops out after 6 s without combat); and it is leashed to the arena centre (BotBrain ignores TetherRange).
/// </summary>
public sealed class GauntletChampionBrain : BotBrain
{
    /// <summary>The aggro the challenger is topped up to every think.</summary>
    private const long ChallengerAggro = 10_000;

    /// <summary>A walk-in that has not reached the centre by then stops where it is and starts buffing.</summary>
    private const long WalkInTimeoutMs = 20_000;

    /// <summary>The champion is at the centre within this distance.</summary>
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
            if (champion.EngageAtTick == 0)
            {
                // Walking in from the gate: nothing else may give it orders.
                if (champion.IsWithinRadius(champion.SpawnPoint, CentreReachedDistance) || now - champion.WalkInStartTick > WalkInTimeoutMs)
                {
                    champion.StopMoving();
                    champion.EngageAtTick = now + GauntletRules.ChampionBuffSeconds * 1000L;
                }
                else if (!champion.IsMoving)
                {
                    champion.WalkTo(champion.SpawnPoint, champion.MaxSpeed);
                }

                return;
            }

            if (now < champion.EngageAtTick)
            {
                // Buffing: the idle state casts its defensive spells; AddToAggroList refuses everyone but the
                // challenger's side, and a hit from that side engages at once.
                base.Think();

                return;
            }

            champion.Engaged = true;
        }

        if (champion.IsOutOfTetherRange)
        {
            // Leash: drop the fight and go back to the centre.
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

        // Hit during the walk-in or the buffing: fight now.
        if (!champion.Engaged)
        {
            champion.Engaged = true;
        }

        base.AddToAggroList(living, aggroAmount);
    }
}
