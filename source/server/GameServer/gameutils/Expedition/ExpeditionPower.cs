using System;
using System.Collections.Concurrent;
using DOL.AI.Brain;
using DOL.Events;

namespace DOL.GS.Expedition;

/// <summary>
/// The hard run's power meter (owner 2026-10-30: "Is there a way to tie it to the power level of the player?" ... "Go 90%
/// power"): the damage a hard-run player (and their pet) lands on monsters per second of fighting, measured over the last
/// <see cref="WindowSeconds"/> of combat. The hunt's stalker is sized from it (<see cref="StalkerHealth"/>) and its hits from
/// the player's health (<see cref="StalkerHitDamage"/>), both at <see cref="PowerFraction"/> of an even match, so it keeps
/// pace with gear, boons and realm rank without a formula over them.
/// </summary>
public static class ExpeditionPower
{
    /// <summary>How much of an even match the stalker is.</summary>
    public const double PowerFraction = 0.9;

    /// <summary>An even match: the player's measured damage takes this long to kill the stalker.</summary>
    public const double MatchedKillSeconds = 30;

    /// <summary>An even match: the stalker's hits would kill a player who only stands there in this long.</summary>
    public const double MatchedDeathSeconds = 25;

    /// <summary>The combat time the meter keeps: older fighting fades out as newer comes in.</summary>
    public const double WindowSeconds = 300;

    /// <summary>The least combat time measured before the meter is trusted; until then the stalker is level-based.</summary>
    public const double MinimumSeconds = 20;

    /// <summary>A gap between two hits counts as at most this much fighting (a pause between pulls is not combat).</summary>
    public const double MaxGapSeconds = 3;

    /// <summary>The time between two stalker hits counted for one hit's damage, at least and at most.</summary>
    public const double MinSwingSeconds = 1.5;
    public const double MaxSwingSeconds = 5;

    private sealed class Meter
    {
        public double Damage;
        public double Seconds;
        public long LastHitTick;
    }

    private static readonly ConcurrentDictionary<string, Meter> Meters = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<ExpeditionStalker, long> StalkerLastHit = new();
    private static readonly object HandlerSync = new();
    private static bool _handlersRegistered;

    /// <summary>
    /// One hit into the meter: the gap since the last hit (capped at <see cref="MaxGapSeconds"/>) counts as fighting, and
    /// when the meter holds more than <see cref="WindowSeconds"/> both totals are scaled back to it.
    /// </summary>
    public static (double Damage, double Seconds) Add(double damage, double seconds, double hitDamage, double gapSeconds)
    {
        damage += Math.Max(0, hitDamage);
        seconds += Math.Clamp(gapSeconds, 0, MaxGapSeconds);

        if (seconds > WindowSeconds)
        {
            double keep = WindowSeconds / seconds;
            damage *= keep;
            seconds = WindowSeconds;
        }

        return (damage, seconds);
    }

    /// <summary>The measured damage per second, or 0 until <see cref="MinimumSeconds"/> of fighting are in.</summary>
    public static double Dps(double damage, double seconds)
    {
        return seconds < MinimumSeconds ? 0 : damage / seconds;
    }

    /// <summary>The stalker's health for a measured damage per second; 0 (level-based) without a measure.</summary>
    public static int StalkerHealth(double dps)
    {
        return dps <= 0 ? 0 : (int) Math.Round(PowerFraction * dps * MatchedKillSeconds);
    }

    /// <summary>One stalker hit for a player with this maximum health, <paramref name="swingSeconds"/> after its last hit.</summary>
    public static int StalkerHitDamage(int playerMaxHealth, double swingSeconds)
    {
        double perSecond = PowerFraction * playerMaxHealth / MatchedDeathSeconds;

        return Math.Max(1, (int) Math.Round(perSecond * Math.Clamp(swingSeconds, MinSwingSeconds, MaxSwingSeconds)));
    }

    /// <summary>The player's measured damage per second (0 until the meter is trusted).</summary>
    public static double DpsOf(GamePlayer player)
    {
        return player != null && !string.IsNullOrEmpty(player.ObjectId) && Meters.TryGetValue(player.ObjectId, out Meter meter) ? Dps(meter.Damage, meter.Seconds) : 0;
    }

    /// <summary>Starts measuring (the hunt's tick on a hard run calls it).</summary>
    public static void EnsureHandlers()
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

    /// <summary>Forgets a stalker that is gone (ExpeditionHunt removes it).</summary>
    public static void Forget(ExpeditionStalker stalker)
    {
        if (stalker != null)
        {
            StalkerLastHit.TryRemove(stalker, out _);
        }
    }

    /// <summary>
    /// Every attack that lands, before its damage is applied (GameLiving.OnAttackedByEnemy): a hard-run player's (or their
    /// pet's) hit on a monster goes into the meter; a power-matched stalker's hit on its player is set to its matched size.
    /// </summary>
    private static void OnAttackedByEnemy(DOLEvent e, object sender, EventArgs args)
    {
        if (args is not AttackedByEnemyEventArgs attackedArgs || attackedArgs.AttackData is not AttackData ad || !ad.IsHit)
        {
            return;
        }

        try
        {
            if (sender is GamePlayer defender && ad.Attacker is ExpeditionStalker stalker && stalker.PowerMatched)
            {
                long now = GameLoop.GameLoopTime;
                double swing = StalkerLastHit.TryGetValue(stalker, out long last) ? (now - last) / 1000.0 : MaxSwingSeconds;
                StalkerLastHit[stalker] = now;
                int damage = StalkerHitDamage(defender.MaxHealth, swing);
                int jitter = (int) Math.Round(damage * (Random.Shared.NextDouble() * 0.3 - 0.15));
                ad.Damage = Math.Max(1, damage + jitter);
                ad.CriticalDamage = ad.CriticalDamage > 0 ? ad.Damage / 2 : 0;

                return;
            }

            if (sender is not GameNPC || sender is ExpeditionStalker)
            {
                return;
            }

            GamePlayer striker = ad.Attacker as GamePlayer ?? (ad.Attacker is GameNPC pet && pet.Brain is IControlledBrain brain ? brain.GetPlayerOwner() : null);

            if (striker == null || string.IsNullOrEmpty(striker.ObjectId) || !ExpeditionHardMode.IsHard(striker))
            {
                return;
            }

            Meter meter = Meters.GetOrAdd(striker.ObjectId, _ => new Meter());

            lock (meter)
            {
                long now = GameLoop.GameLoopTime;
                double gap = meter.LastHitTick == 0 ? MaxGapSeconds : (now - meter.LastHitTick) / 1000.0;
                meter.LastHitTick = now;
                (meter.Damage, meter.Seconds) = Add(meter.Damage, meter.Seconds, ad.Damage + ad.CriticalDamage, gap);
            }
        }
        catch (Exception)
        {
            // The meter must never break combat.
        }
    }
}
