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

    // Every 6 s (owner 2026-10-29: was every 3 s); every 10 s on a hard expedition (owner 2026-10-29: 60% of normal).
    private const int RejuvenationIntervalMilliseconds = 6000;
    private const int HardRejuvenationIntervalMilliseconds = 10000;
    // 1% (owner 2026-10-29: nerfed from 2%).
    private const int RejuvenationPercent = 1;
    private const int VampiricChancePercent = 25;
    private const int PulseIntervalMilliseconds = 500;
    private const int IconIntervalMilliseconds = 2000;

    /// <summary>Boons that act on hits (the global AttackedByEnemy handler).</summary>
    private static readonly HashSet<string> HitBoons = new()
    {
        ExpeditionBoons.Thornmail, ExpeditionBoons.Vampiric, ExpeditionBoons.Executioner, ExpeditionBoons.Siphon,
        ExpeditionBoons.HuntersInstinct, ExpeditionBoons.BeastBond
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
    /// percent, capped at 50 (CriticalMeleeHitChanceCalculator); MeleeSpeed is percent faster (MeleeSpeedPercentCalculator);
    /// the resists are secondary resists (ResistCalculator), and Second Skin's cap bonus is read by the same calculator.
    /// </summary>
    private static readonly Dictionary<string, (eProperty Property, int Amount)[]> PropertyBonuses = new()
    {
        [ExpeditionBoons.Ironhide] = new[] { (eProperty.Of_Toughness, 10), (eProperty.PhysicalAbsorption, 10) },
        [ExpeditionBoons.Bloodlust] = new[] { (eProperty.CriticalMeleeHitChance, 15) },
        [ExpeditionBoons.ArcaneFury] = new[] { (eProperty.CriticalSpellHitChance, 15) },
        [ExpeditionBoons.GlassCannon] = new[] { (eProperty.MeleeDamage, 25), (eProperty.SpellDamage, 25), (eProperty.Of_Toughness, -15) },
        [ExpeditionBoons.Berserker] = new[] { (eProperty.MeleeDamage, 30), (eProperty.MeleeSpeed, 15) },

        // Juggernaut (hard expedition only): the crowd control reductions are percents off the duration, read by the
        // Mesmerize/Stun/SpeedDecreaseDurationPercentCalculator from AbilityBonus (the player's side of Stalwart).
        [ExpeditionBoons.Juggernaut] = new[]
        {
            (eProperty.Of_Toughness, 25), (eProperty.MesmerizeDurationReduction, JuggernautCrowdControlReduction),
            (eProperty.StunDurationReduction, JuggernautCrowdControlReduction), (eProperty.SpeedDecreaseDurationReduction, JuggernautCrowdControlReduction)
        },

        // SpellRange and CastingSpeed are percents outside the item caps (SpellRangePercentCalculator, TOABonusCalculator);
        // RangedDamage and ArcherySpeed likewise, CriticalArcheryHitChance capped at 50 (owner 2026-10-29).
        [ExpeditionBoons.Overreach] = new[] { (eProperty.SpellRange, 15), (eProperty.CastingSpeed, 10) },
        [ExpeditionBoons.Deadeye] = new[] { (eProperty.CriticalArcheryHitChance, 15) },
        [ExpeditionBoons.Sharpshooter] = new[] { (eProperty.RangedDamage, 25), (eProperty.ArcherySpeed, 10), (eProperty.MeleeDamage, -20) },
        [ExpeditionBoons.Skirmisher] = new[] { (eProperty.MeleeDamage, 25), (eProperty.MeleeSpeed, 10), (eProperty.RangedDamage, -20) },
        [ExpeditionBoons.SecondSkin] = new[]
        {
            (eProperty.Resist_Crush, SecondSkinResistPercent), (eProperty.Resist_Slash, SecondSkinResistPercent), (eProperty.Resist_Thrust, SecondSkinResistPercent),
            (eProperty.Resist_Body, SecondSkinResistPercent), (eProperty.Resist_Cold, SecondSkinResistPercent), (eProperty.Resist_Energy, SecondSkinResistPercent),
            (eProperty.Resist_Heat, SecondSkinResistPercent), (eProperty.Resist_Matter, SecondSkinResistPercent), (eProperty.Resist_Spirit, SecondSkinResistPercent)
        }
    };

    private const int SecondSkinResistPercent = 10;

    /// <summary>Juggernaut: mesmerize, stun and snare on the player last this many percent less (as a Stalwart elite's).</summary>
    public const int JuggernautCrowdControlReduction = ExpeditionElites.StalwartCrowdControlReduction;
    private const int SecondSkinResistCapBonus = 5;

    /// <summary>Second Skin: how much higher the player's resist cap from items is (0 without the boon). Called by ResistCalculator.</summary>
    public static int ResistCapBonus(GameLiving living)
    {
        return living is GamePlayer player && HasBoon(player, ExpeditionBoons.SecondSkin) ? SecondSkinResistCapBonus : 0;
    }

    private sealed class AppliedBoons
    {
        public readonly HashSet<string> Ids = new();
        public ECSGameTimer RejuvenationTimer;

        // Combat boons (BoonEffects.Combat.cs).
        public ECSGameTimer PulseTimer;
        public int PhoenixUsedAct;
        public bool DesperationOn;

        // The buff bar icons (BoonIconEffect) by boon id, and the timer that puts back one that death or a cancel ended.
        public readonly Dictionary<string, BoonIconEffect> Icons = new();
        public ECSGameTimer IconTimer;
    }

    /// <summary>
    /// Boons that do nothing in the Gauntlet (owner 2026-10-27): Phoenix Feather, and the loot boons, which have no
    /// loot to touch there. The rest of the won run's boons are applied for the session (<see cref="ApplyForGauntlet"/>).
    /// </summary>
    public static readonly IReadOnlySet<string> NotInGauntlet = new HashSet<string>(StringComparer.Ordinal)
    {
        ExpeditionBoons.PhoenixFeather, ExpeditionBoons.Fortune, ExpeditionBoons.BountyHunter, ExpeditionBoons.TreasureSense
    };

    /// <summary>
    /// The won run's boons for a Gauntlet session (owner 2026-10-27: they carry into the final fights as their balance),
    /// less <see cref="NotInGauntlet"/> and retired boons. Taken off when the session stops (GauntletManager.Stop).
    /// </summary>
    public static void ApplyForGauntlet(GamePlayer player, IEnumerable<string> runBoons)
    {
        List<string> boons = runBoons?
            .Where(id => !NotInGauntlet.Contains(id) && !ExpeditionBoons.Retired.Contains(id) && ExpeditionBoons.Find(id) != null)
            .ToList() ?? new List<string>();

        Apply(player, boons);
    }

    /// <summary>A timer boon keeps ticking while the player has an active run or a Gauntlet session.</summary>
    private static bool HasBoonSource(GamePlayer player, ExpeditionRun run)
    {
        return run != null || Gauntlet.GauntletManager.GetState(player) != null;
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

        Apply(player, boons);
    }

    private static void Apply(GamePlayer player, List<string> boons)
    {
        if (player == null)
        {
            return;
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
                    state.RejuvenationTimer = new ECSGameTimer(player, timer => RejuvenationTick(player, state),
                        RejuvenationIntervalFor(ExpeditionHardMode.IsHard(player)));
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

            if (state.Ids.Count > 0 && state.IconTimer == null)
            {
                state.IconTimer = new ECSGameTimer(player, timer => IconTick(player, state), IconIntervalMilliseconds);
            }
        }

        EnsureIcons(player, state);

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
        List<BoonIconEffect> icons;

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
            state.IconTimer?.Stop();
            state.IconTimer = null;
            icons = state.Icons.Values.ToList();
            state.Icons.Clear();

            if (state.DesperationOn)
            {
                SetDesperation(player, state, false);
                statsChanged = true;
            }
        }

        player.TempProperties.RemoveProperty(StateKey);

        // Ended outside the state lock: ending takes the effect list's lock.
        foreach (BoonIconEffect icon in icons)
        {
            icon.End();
        }

        if (statsChanged && sendUpdates)
        {
            SendUpdates(player);
        }
    }

    /// <summary>Every 2 seconds: puts back a boon icon that death or a cancel ended. Stops with the boons.</summary>
    private static int IconTick(GamePlayer player, AppliedBoons state)
    {
        lock (state)
        {
            if (state.IconTimer == null || state.Ids.Count == 0)
            {
                state.IconTimer = null;

                return 0;
            }
        }

        EnsureIcons(player, state);

        return IconIntervalMilliseconds;
    }

    /// <summary>Starts the buff bar icon of every applied boon that has none running (a living player in the world).</summary>
    private static void EnsureIcons(GamePlayer player, AppliedBoons state)
    {
        if (player == null || !player.IsAlive || player.ObjectState != GameObject.eObjectState.Active)
        {
            return;
        }

        List<BoonDefinition> missing = new();

        lock (state)
        {
            foreach (string id in state.Ids)
            {
                if (state.Icons.TryGetValue(id, out BoonIconEffect icon) && (icon.IsActive || icon.IsStarting))
                {
                    continue;
                }

                if (ExpeditionBoons.Find(id) is BoonDefinition boon)
                {
                    missing.Add(boon);
                }
            }
        }

        // Started outside the state lock: starting takes the effect list's lock.
        foreach (BoonDefinition boon in missing)
        {
            BoonIconEffect icon = ECSGameEffectFactory.Create(new ECSGameEffectInitParams(player, 0, 1), boon,
                static (in ECSGameEffectInitParams init, BoonDefinition definition) => new BoonIconEffect(init, definition));

            bool kept;

            lock (state)
            {
                kept = state.Ids.Contains(boon.Id);

                if (kept)
                {
                    state.Icons[boon.Id] = icon;
                }
            }

            // The boons came off while it started.
            if (!kept)
            {
                icon.End();
            }
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
        player.Out.SendCharResistsUpdate();
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

    /// <summary>Rejuvenation's interval: every 6 seconds, every 10 on a hard expedition.</summary>
    public static int RejuvenationIntervalFor(bool hard)
    {
        return hard ? HardRejuvenationIntervalMilliseconds : RejuvenationIntervalMilliseconds;
    }

    /// <summary>Every 6 seconds (10 on a hard expedition): 1% of maximum health, in or out of combat. Stops when the run or the boon is gone.</summary>
    private static int RejuvenationTick(GamePlayer player, AppliedBoons state)
    {
        ExpeditionRun run = ExpeditionManager.GetRun(player);

        lock (state)
        {
            if (!HasBoonSource(player, run) || state.RejuvenationTimer == null || !state.Ids.Contains(ExpeditionBoons.Rejuvenation))
            {
                state.RejuvenationTimer = null;

                return 0;
            }
        }

        if (player.IsAlive && player.CurrentRegion != null && player.Health < player.MaxHealth)
        {
            player.ChangeHealth(player, eHealthChangeType.Regenerate, Math.Max(1, player.MaxHealth * RejuvenationPercent / 100));
        }

        return RejuvenationIntervalFor(ExpeditionHardMode.IsHard(run));
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
            else if (PetOwner(ad.Attacker) is GamePlayer owner)
            {
                OnPetLandsHit(owner, ad);
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
