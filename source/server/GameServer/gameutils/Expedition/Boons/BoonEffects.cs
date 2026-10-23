using System.Collections.Generic;
using System.Linq;
using DOL.AI.Brain;
using DOL.GS.PacketHandler;

namespace DOL.GS.Expedition;

/// <summary>
/// The player-side effects of the run's boons. Stat boons add to <see cref="GameLiving.AbilityBonus"/> as the realm
/// ability property enhancers do (RAPropertyEnhancer.Activate) and subtract the same amounts on removal. The boons
/// applied to a player object are recorded in its TempProperties, so applying twice is applying once, and a new login
/// (a new GamePlayer object) starts clean. AbilityBonus is never saved, so nothing leaks into the character's row.
/// </summary>
public static class BoonEffects
{
    private const string StateKey = "EXPEDITION_BOONS_APPLIED";

    private const ushort HuntersEyeRadius = 2000;
    private const int HuntersEyeIntervalMilliseconds = 3000;
    private const long HuntersEyeRepeatMilliseconds = 60_000;

    private static readonly object CreateSync = new();

    /// <summary>
    /// The AbilityBonus amounts per boon. Of_Toughness is a percent of base max health (MaxHealthCalculator);
    /// PhysicalAbsorption and MagicAbsorption are percent damage absorbed, negative meaning more damage taken
    /// (AttackComponent.CalculateTargetAbsorbFactor, SpellHandler's absorption step); CriticalMeleeHitChance is
    /// percent, capped at 50 (CriticalMeleeHitChanceCalculator).
    /// </summary>
    private static readonly Dictionary<string, (eProperty Property, int Amount)[]> PropertyBonuses = new()
    {
        [ExpeditionBoons.Greed] = new[] { (eProperty.PhysicalAbsorption, -15), (eProperty.MagicAbsorption, -15) },
        [ExpeditionBoons.Ironhide] = new[] { (eProperty.Of_Toughness, 10), (eProperty.PhysicalAbsorption, 10) },
        [ExpeditionBoons.Warding] = new[] { (eProperty.MagicAbsorption, 15) },
        [ExpeditionBoons.Bloodlust] = new[] { (eProperty.CriticalMeleeHitChance, 15) }
    };

    private sealed class AppliedBoons
    {
        public readonly HashSet<string> Ids = new();
        public readonly Dictionary<GameNPC, long> Announced = new();
        public ECSGameTimer HuntersEyeTimer;
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

                if (id == ExpeditionBoons.HuntersEye && state.HuntersEyeTimer == null)
                {
                    state.HuntersEyeTimer = new ECSGameTimer(player, timer => HuntersEyeTick(player, state), HuntersEyeIntervalMilliseconds);
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
            state.Announced.Clear();
            state.HuntersEyeTimer?.Stop();
            state.HuntersEyeTimer = null;
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

    /// <summary>
    /// Every 3 seconds: named monsters (name starting with a capital letter, realm none, not a pet, bot or peaceful
    /// or hidden NPC) within 2000 units are announced, each at most once a minute. Stops itself when the run is over
    /// or the boon is gone; skips a tick while the player is dead or between regions.
    /// </summary>
    private static int HuntersEyeTick(GamePlayer player, AppliedBoons state)
    {
        ExpeditionRun run = ExpeditionManager.GetRun(player);

        lock (state)
        {
            if (run == null || state.HuntersEyeTimer == null || !state.Ids.Contains(ExpeditionBoons.HuntersEye))
            {
                state.HuntersEyeTimer = null;

                return 0;
            }
        }

        if (!player.IsAlive || player.CurrentRegion == null)
        {
            return HuntersEyeIntervalMilliseconds;
        }

        long now = GameLoop.GameLoopTime;
        var announce = new List<GameNPC>();

        lock (state)
        {
            foreach (GameNPC stale in state.Announced.Where(entry => now - entry.Value >= HuntersEyeRepeatMilliseconds).Select(entry => entry.Key).ToList())
            {
                state.Announced.Remove(stale);
            }

            foreach (GameNPC npc in player.GetNPCsInRadius(HuntersEyeRadius))
            {
                if (!IsNamedMonster(npc) || state.Announced.ContainsKey(npc))
                {
                    continue;
                }

                state.Announced[npc] = now;
                announce.Add(npc);
            }
        }

        foreach (GameNPC npc in announce)
        {
            player.Out.SendMessage($"Hunter's Eye: {npc.Name} (level {npc.Level}) is nearby.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        }

        return HuntersEyeIntervalMilliseconds;
    }

    private static bool IsNamedMonster(GameNPC npc)
    {
        if (npc == null || !npc.IsAlive || npc is GameBot || npc.Brain is IControlledBrain || npc.Realm != eRealm.None)
        {
            return false;
        }

        if ((npc.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET | GameNPC.eFlags.DONTSHOWNAME)) != 0)
        {
            return false;
        }

        string name = npc.Name;

        return !string.IsNullOrEmpty(name) && char.IsUpper(name[0]);
    }
}
