using System;
using System.Collections.Generic;
using System.Linq;
using DOL.AI.Brain;
using DOL.Events;
using DOL.GS.PacketHandler;

namespace DOL.GS.Expedition;

/// <summary>
/// The player-side effects of the run's boons. Stat boons add to <see cref="GameLiving.AbilityBonus"/> as the realm
/// ability property enhancers do (RAPropertyEnhancer.Activate) and subtract the same amounts on removal. The boons
/// applied to a player object are recorded in its TempProperties, so applying twice is applying once, and a new login
/// (a new GamePlayer object) starts clean. AbilityBonus is never saved, so nothing leaks into the character's row.
/// </summary>
public static partial class BoonEffects
{
    private const string StateKey = "EXPEDITION_BOONS_APPLIED";

    private const int RejuvenationIntervalMilliseconds = 3000;
    private const int RejuvenationPercent = 2;
    private const int VampiricChancePercent = 25;
    private const int PulseIntervalMilliseconds = 500;

    /// <summary>Boons that act on hits (the global AttackedByEnemy handler).</summary>
    private static readonly HashSet<string> HitBoons = new()
    {
        ExpeditionBoons.Thornmail, ExpeditionBoons.Vampiric, ExpeditionBoons.Executioner,
        ExpeditionBoons.Frostbite, ExpeditionBoons.Siphon
    };

    /// <summary>Boons that watch the player's health on a timer.</summary>
    private static readonly HashSet<string> PulseBoons = new() { ExpeditionBoons.PhoenixFeather, ExpeditionBoons.Desperation };

    private static readonly object CreateSync = new();
    private static readonly object HandlerSync = new();
    private static bool _handlersRegistered;

    /// <summary>
    /// The AbilityBonus amounts per boon. Of_Toughness is a percent of base max health (MaxHealthCalculator);
    /// PhysicalAbsorption and MagicAbsorption are percent damage absorbed, negative meaning more damage taken
    /// (AttackComponent.CalculateTargetAbsorbFactor, SpellHandler's absorption step); CriticalMeleeHitChance is
    /// percent, capped at 50 (CriticalMeleeHitChanceCalculator).
    /// </summary>
    private static readonly Dictionary<string, (eProperty Property, int Amount)[]> PropertyBonuses = new()
    {
        [ExpeditionBoons.Ironhide] = new[] { (eProperty.Of_Toughness, 10), (eProperty.PhysicalAbsorption, 10) },
        [ExpeditionBoons.Warding] = new[] { (eProperty.MagicAbsorption, 15) },
        [ExpeditionBoons.Bloodlust] = new[] { (eProperty.CriticalMeleeHitChance, 15) },
        [ExpeditionBoons.ArcaneFury] = new[] { (eProperty.CriticalSpellHitChance, 15) },
        [ExpeditionBoons.GlassCannon] = new[] { (eProperty.MeleeDamage, 25), (eProperty.SpellDamage, 25), (eProperty.Of_Toughness, -15) }
    };

    private sealed class AppliedBoons
    {
        public readonly HashSet<string> Ids = new();
        public ECSGameTimer RejuvenationTimer;

        // Combat boons (BoonEffects.Combat.cs).
        public ECSGameTimer PulseTimer;
        public int PhoenixUsedAct;
        public bool DesperationOn;
        public long NextFrostbiteAt;
    }

    /// <summary>Applies every boon of the run not yet applied to this player object, then updates the client.</summary>
    public static void ApplyAll(GamePlayer player, ExpeditionRun run)
    {
        if (player == null || run == null)
        {
            return;
        }

        List<string> boons;

        lock (run.Boons)
        {
            boons = run.Boons.ToList();
        }

        if (boons.Count == 0)
        {
            return;
        }

        AppliedBoons state = GetOrCreateState(player);
        bool statsChanged = false;

        lock (state)
        {
            foreach (string id in boons)
            {
                if (!state.Ids.Add(id))
                {
                    continue;
                }

                if (PropertyBonuses.TryGetValue(id, out (eProperty Property, int Amount)[] bonuses))
                {
                    foreach ((eProperty property, int amount) in bonuses)
                    {
                        player.AbilityBonus[property] += amount;
                    }

                    statsChanged = true;
                }

                if (id == ExpeditionBoons.Rejuvenation && state.RejuvenationTimer == null)
                {
                    state.RejuvenationTimer = new ECSGameTimer(player, timer => RejuvenationTick(player, state), RejuvenationIntervalMilliseconds);
                }

                if (HitBoons.Contains(id))
                {
                    EnsureHandlers();
                }

                if (PulseBoons.Contains(id) && state.PulseTimer == null)
                {
                    state.PulseTimer = new ECSGameTimer(player, timer => PulseTick(player, state), PulseIntervalMilliseconds);
                }
            }
        }

        if (statsChanged)
        {
            SendUpdates(player);
        }
    }

    /// <summary>
    /// Takes off everything <see cref="ApplyAll"/> put on this player object. <paramref name="sendUpdates"/> is false
    /// when the client is leaving (quit), so no packets are sent to it.
    /// </summary>
    public static void RemoveAll(GamePlayer player, bool sendUpdates)
    {
        if (player == null)
        {
            return;
        }

        AppliedBoons state = player.TempProperties.GetProperty<AppliedBoons>(StateKey, null);

        if (state == null)
        {
            return;
        }

        bool statsChanged = false;

        lock (state)
        {
            foreach (string id in state.Ids)
            {
                if (PropertyBonuses.TryGetValue(id, out (eProperty Property, int Amount)[] bonuses))
                {
                    foreach ((eProperty property, int amount) in bonuses)
                    {
                        player.AbilityBonus[property] -= amount;
                    }

                    statsChanged = true;
                }
            }

            state.Ids.Clear();
            state.RejuvenationTimer?.Stop();
            state.RejuvenationTimer = null;
            state.PulseTimer?.Stop();
            state.PulseTimer = null;

            if (state.DesperationOn)
            {
                SetDesperation(player, state, false);
                statsChanged = true;
            }
        }

        player.TempProperties.RemoveProperty(StateKey);

        if (statsChanged && sendUpdates)
        {
            SendUpdates(player);
        }
    }

    private static AppliedBoons GetOrCreateState(GamePlayer player)
    {
        lock (CreateSync)
        {
            AppliedBoons state = player.TempProperties.GetProperty<AppliedBoons>(StateKey, null);

            if (state == null)
            {
                state = new AppliedBoons();
                player.TempProperties.SetProperty(StateKey, state);
            }

            return state;
        }
    }

    /// <summary>As the stat buff effects do (StatBuffECSEffect): regeneration clamps health to the new maximum, then the client is told.</summary>
    private static void SendUpdates(GamePlayer player)
    {
        player.StartHealthRegeneration();

        if (player.Out == null)
        {
            return;
        }

        player.Out.SendCharStatsUpdate();
        player.Out.SendUpdateWeaponAndArmorStats();
        player.UpdatePlayerStatus();
    }

    /// <summary>True when the boon is applied to this player object (cheap: no run lookup).</summary>
    private static bool HasBoon(GamePlayer player, string id)
    {
        AppliedBoons state = player?.TempProperties.GetProperty<AppliedBoons>(StateKey, null);

        if (state == null)
        {
            return false;
        }

        lock (state)
        {
            return state.Ids.Contains(id);
        }
    }

    /// <summary>Every 3 seconds: 2% of maximum health, in or out of combat. Stops when the run or the boon is gone.</summary>
    private static int RejuvenationTick(GamePlayer player, AppliedBoons state)
    {
        ExpeditionRun run = ExpeditionManager.GetRun(player);

        lock (state)
        {
            if (run == null || state.RejuvenationTimer == null || !state.Ids.Contains(ExpeditionBoons.Rejuvenation))
            {
                state.RejuvenationTimer = null;

                return 0;
            }
        }

        if (player.IsAlive && player.CurrentRegion != null && player.Health < player.MaxHealth)
        {
            player.ChangeHealth(player, eHealthChangeType.Regenerate, Math.Max(1, player.MaxHealth * RejuvenationPercent / 100));
        }

        return RejuvenationIntervalMilliseconds;
    }

    private static void EnsureHandlers()
    {
        lock (HandlerSync)
        {
            if (_handlersRegistered)
            {
                return;
            }

            GameEventMgr.AddHandler(GameLivingEvent.AttackedByEnemy, OnAttackedByEnemy);
            _handlersRegistered = true;
        }
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        lock (HandlerSync)
        {
            if (_handlersRegistered)
            {
                GameEventMgr.RemoveHandler(GameLivingEvent.AttackedByEnemy, OnAttackedByEnemy);
                _handlersRegistered = false;
            }
        }
    }

    /// <summary>
    /// Global AttackedByEnemy handler (GameLiving.OnAttackedByEnemy, raised for every attack on the attacked living):
    /// Thornmail burns a monster whose melee hit lands on the player; Vampiric heals the player from a hit it lands.
    /// </summary>
    private static void OnAttackedByEnemy(DOLEvent e, object sender, EventArgs args)
    {
        if (args is not AttackedByEnemyEventArgs attackedArgs || attackedArgs.AttackData is not AttackData ad || !ad.IsHit)
        {
            return;
        }

        try
        {
            if (sender is GamePlayer defender && ad.Attacker is GameNPC attacker)
            {
                OnPlayerHit(defender, attacker, ad);
            }

            if (ad.Attacker is GamePlayer striker && ad.Target != striker)
            {
                OnPlayerLandsHit(striker, ad);
            }
        }
        catch (Exception)
        {
            // A boon must never break combat.
        }
    }

    /// <summary>A monster's hit on the player (before the damage is applied): Thornmail.</summary>
    private static void OnPlayerHit(GamePlayer defender, GameNPC attacker, AttackData ad)
    {
        if (ad.IsMeleeAttack && attacker.IsAlive && defender.IsAlive && HasBoon(defender, ExpeditionBoons.Thornmail))
        {
            int thorns = 3 + defender.Level;
            attacker.TakeDamage(defender, eDamageType.Heat, thorns, 0);
            defender.Out?.SendMessage($"Thornmail burns {attacker.GetName(0, false)} for {thorns} damage!", eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
        }
    }
}
