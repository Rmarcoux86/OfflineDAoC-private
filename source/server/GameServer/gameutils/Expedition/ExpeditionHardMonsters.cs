using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.AI;
using DOL.AI.Brain;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.Logging;
using CampEntry = DOL.GS.AutonomousCapnBryGoalCatalog.Entry;

namespace DOL.GS.Expedition;

/// <summary>
/// The monsters of a hard run (owner 2026-10-29), all read from <see cref="ExpeditionHardMode.IsHard(ExpeditionRun)"/>:
/// <list type="bullet">
/// <item>Dread: every monster of the act's zone that the player comes near is claimed once, one level higher and named
/// "Dread goblin". The world's own spawns are changed, so every change is undone as the elites' are: on its death (a
/// respawn reuses the same GameNPC and does not reload its name or level, GameNPC.RespawnTimerCallback), when the
/// player's tracker detaches, and on server stop.</item>
/// <item>Every camp of the act is an elite pack with two affixes (<see cref="HardenPlan"/>); a pack also gets the Dread
/// level and name ("Swift Venomous Dread goblin", <see cref="ExpeditionElites"/>).</item>
/// <item>The act boss, whatever NPC it is, gets two affixes (<see cref="ExpeditionElites.AddBossAffixes"/>) and a
/// doubled leash (<see cref="TickBossLeash"/>).</item>
/// </list>
/// The hunt is <see cref="ExpeditionHunt"/>.
/// </summary>
public static class ExpeditionHardMonsters
{
    public const string DreadPrefix = "Dread ";
    public const int DreadLevelBonus = 1;

    /// <summary>Monsters this close to the hard-run player (2D) are claimed as Dread on the tracker tick.</summary>
    public const int ClaimRadius = 3000;

    /// <summary>How many affixes a hard-run act boss ends up with.</summary>
    public const int BossAffixCount = 2;

    /// <summary>
    /// The act boss's leash is this many times the standard monster's: the standard AI lets a target go beyond
    /// <see cref="StandardMobBrain.MAX_AGGRO_LIST_DISTANCE"/> and walks home after 25 s out of combat.
    /// </summary>
    public const int BossLeashMultiplier = 2;

    private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly ConcurrentDictionary<GameNPC, DreadState> Dread = new();
    private static readonly ConcurrentDictionary<string, BossLeash> Leashes = new(StringComparer.Ordinal);
    private static readonly object HandlerSync = new();
    private static bool _handlersRegistered;

    private sealed class DreadState
    {
        public string OwnerKey;
        public string OriginalName;
        public byte OriginalLevel;
    }

    private sealed class BossLeash
    {
        public GameNPC Boss;

        // The boss has fought the player since it last went home.
        public bool Engaged;
    }

    #region Names

    /// <summary>"Dread goblin"; a name that already starts with "dread" ("dread lich") is kept as it is.</summary>
    public static string DreadName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.StartsWith(DreadPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return DreadPrefix + name;
    }

    /// <summary>
    /// A monster's name without the elite affixes and the Dread prefix in front: "Swift Venomous Dread goblin",
    /// "Dread goblin" and "Swift goblin" are all "goblin".
    /// </summary>
    public static string BaseName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        string rest = name;
        bool stripped = true;

        while (stripped)
        {
            stripped = false;

            foreach (string affix in ExpeditionElites.Affixes)
            {
                if (rest.StartsWith(affix + " ", StringComparison.Ordinal) && rest.Length > affix.Length + 1)
                {
                    rest = rest.Substring(affix.Length + 1);
                    stripped = true;
                }
            }
        }

        if (rest.StartsWith(DreadPrefix, StringComparison.Ordinal) && rest.Length > DreadPrefix.Length)
        {
            rest = rest.Substring(DreadPrefix.Length);
        }

        return rest;
    }

    /// <summary>The living's name as the world knows it: a Dread monster's own name, else its name.</summary>
    public static string PlainName(GameLiving living)
    {
        if (living is GameNPC npc && Dread.TryGetValue(npc, out DreadState state))
        {
            return state.OriginalName;
        }

        return living?.Name;
    }

    #endregion

    #region Dread

    /// <summary>
    /// Called on the tracker tick (game loop): claims the monsters of the act's zone within <see cref="ClaimRadius"/>
    /// of a hard-run player, after <see cref="ExpeditionElites.CheckCamps"/> so a pack is woken as elite first.
    /// </summary>
    internal static void ClaimNearby(GamePlayer player, ExpeditionRun run, ActPlan plan)
    {
        if (!ExpeditionHardMode.IsHard(run) || plan == null || player?.CurrentRegion == null || player.CurrentRegionID != plan.RegionId || string.IsNullOrEmpty(player.ObjectId))
        {
            return;
        }

        List<GameNPC> found = new();
        player.CurrentRegion.GetInRadius(new Point3D(player.X, player.Y, player.Z), eGameObjectType.NPC, (ushort) ClaimRadius, found);

        foreach (GameNPC npc in found)
        {
            if (!CanClaim(npc, plan))
            {
                continue;
            }

            try
            {
                Claim(npc, player.ObjectId);
            }
            catch (Exception ex)
            {
                log.Error($"[EXPEDITION] Could not make {npc.Name} Dread for {player.Name}.", ex);
                Release(npc);
            }
        }
    }

    private static bool CanClaim(GameNPC npc, ActPlan plan)
    {
        if (npc == null || !npc.IsAlive || npc.ObjectState != GameObject.eObjectState.Active)
        {
            return false;
        }

        if (npc is GameBot || npc is ExpeditionBoss || npc is ExpeditionStalker || npc.Brain is not StandardMobBrain || npc.Brain is IControlledBrain)
        {
            return false;
        }

        if (npc.Realm != eRealm.None || (npc.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) != 0)
        {
            return false;
        }

        if (Dread.ContainsKey(npc) || ExpeditionElites.IsElite(npc) || IsHardBoss(npc))
        {
            return false;
        }

        return npc.CurrentRegionID == plan.RegionId && npc.CurrentZone != null && npc.CurrentZone.ID == plan.ZoneId;
    }

    private static void Claim(GameNPC npc, string ownerKey)
    {
        DreadState state = new()
        {
            OwnerKey = ownerKey,
            OriginalName = npc.Name,
            OriginalLevel = npc.Level
        };

        if (!Dread.TryAdd(npc, state))
        {
            return;
        }

        EnsureHandlers();

        // Level first (the setter re-derives its stats); a monster already hurt keeps its share of health.
        int percent = npc.HealthPercent;
        npc.Level = (byte) Math.Min(byte.MaxValue, npc.Level + DreadLevelBonus);
        npc.Health = Math.Max(1, npc.MaxHealth * percent / 100);

        // The name setter re-creates the monster for nearby clients, so they see the new level as well.
        npc.Name = DreadName(state.OriginalName);
    }

    /// <summary>Makes a Dread monster plain again (it is becoming elite, it died, or its run let go); false when it was not Dread.</summary>
    public static bool Release(GameNPC npc)
    {
        if (npc == null || !Dread.TryRemove(npc, out DreadState state))
        {
            return false;
        }

        try
        {
            // The Level setter also clamps health to the restored maximum; the name setter then refreshes clients.
            npc.Level = state.OriginalLevel;
            npc.Name = state.OriginalName;
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] Could not restore Dread monster {state.OriginalName}.", ex);
        }

        return true;
    }

    /// <summary>
    /// Called by AbstractServerRules.DropLoot before the world's loot tables are read: they are keyed by the monster's
    /// name, so a Dread monster drops what its own kind drops. The rest is restored by the Dying handler.
    /// </summary>
    public static void OnLootDrop(GameNPC npc)
    {
        if (npc == null || Dread.IsEmpty || !Dread.TryGetValue(npc, out DreadState state) || npc.Name == state.OriginalName)
        {
            return;
        }

        npc.Name = state.OriginalName;
    }

    /// <summary>Restores every Dread monster and forgets the boss leash of this player's run (tracker detach).</summary>
    public static void RestoreAll(GamePlayer player)
    {
        string key = player?.ObjectId;

        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        Leashes.TryRemove(key, out _);

        foreach (KeyValuePair<GameNPC, DreadState> entry in Dread)
        {
            if (entry.Value.OwnerKey == key)
            {
                Release(entry.Key);
            }
        }
    }

    private static void EnsureHandlers()
    {
        lock (HandlerSync)
        {
            if (_handlersRegistered)
            {
                return;
            }

            GameEventMgr.AddHandler(GameLivingEvent.Dying, OnDying);
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
                GameEventMgr.RemoveHandler(GameLivingEvent.Dying, OnDying);
                _handlersRegistered = false;
            }
        }

        foreach (GameNPC npc in Dread.Keys)
        {
            Release(npc);
        }

        Leashes.Clear();
    }

    /// <summary>Global Dying handler: a Dread monster respawns as its plain self (the same GameNPC object).</summary>
    private static void OnDying(DOLEvent e, object sender, EventArgs args)
    {
        if (Dread.IsEmpty || sender is not GameNPC npc)
        {
            return;
        }

        Release(npc);
    }

    #endregion

    #region Elite camps

    /// <summary>
    /// Called on the tracker tick before <see cref="ExpeditionElites.CheckCamps"/>: the first time a hard run's act is
    /// played, every camp of the act becomes an elite pack (<see cref="HardenElites"/>, from the live world's camps) and
    /// the plan is saved.
    /// </summary>
    internal static void HardenPlan(GamePlayer player, ExpeditionRun run, ActPlan plan)
    {
        if (!ExpeditionHardMode.IsHard(run) || plan == null || plan.AllCampsElite)
        {
            return;
        }

        try
        {
            (int ox, int oy) = ActGenerator.WorldZoneOffset(plan.ZoneId);
            IReadOnlyList<CampEntry> camps = WorldThenCatalogCampSource.Instance.GetZoneCamps(plan.RegionId, plan.ZoneId);
            int added = HardenElites(plan, camps, ox, oy, new Random(unchecked(run.Seed * 7919 + plan.Act)));
            ExpeditionDebug.Report(player, $"hard run: {added} more elite camps in act {plan.Act} ({plan.Elites.Count} in all).");
            ExpeditionManager.Save(run);
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] Could not make the act {plan.Act} camps elite for {player?.Name}.", ex);
            plan.AllCampsElite = true;
        }
    }

    /// <summary>
    /// Makes every camp of the act an elite pack with two different affixes: the plan's own packs get a second affix,
    /// and every other camp of the act's band (the elite rule: up to <see cref="ActGenerator.EliteLevelSlack"/> levels
    /// above it) whose monster is not an objective's or the boss's (their kills are counted by name) becomes one. Wake-ups
    /// stay per camp, within <see cref="ExpeditionElites.ApproachRadius"/>. Sets <see cref="ActPlan.AllCampsElite"/> and
    /// returns the number of packs added.
    /// </summary>
    public static int HardenElites(ActPlan plan, IReadOnlyList<CampEntry> camps, int ox, int oy, Random rng)
    {
        if (plan == null || plan.AllCampsElite || rng == null)
        {
            return 0;
        }

        plan.Elites ??= new List<EliteCamp>();
        HashSet<string> excluded = new(StringComparer.OrdinalIgnoreCase) { plan.Boss?.BaseMobName ?? string.Empty };

        foreach (ActObjective objective in plan.Objectives ?? [])
        {
            if (!string.IsNullOrEmpty(objective?.MobName))
            {
                excluded.Add(objective.MobName);
            }
        }

        foreach (EliteCamp camp in plan.Elites)
        {
            if (camp == null)
            {
                continue;
            }

            camp.ExtraAffixes ??= new List<string>();

            if (ExpeditionElites.CampAffixes(camp).Count < 2)
            {
                camp.ExtraAffixes.Add(OtherAffix(rng, camp.Affix));
            }

            camp.Level += DreadLevelBonus;
            camp.Description = Describe(plan, camp);
        }

        int added = 0;

        foreach (CampEntry entry in camps ?? [])
        {
            if (entry == null || string.IsNullOrEmpty(entry.Name) || entry.Levels is not { Length: > 0 } || excluded.Contains(entry.Name))
            {
                continue;
            }

            if (entry.Levels.Min() > plan.MaxLevel + ActGenerator.EliteLevelSlack || entry.Levels.Max() < plan.MinLevel)
            {
                continue;
            }

            int x = ox + entry.LocalX;
            int y = oy + entry.LocalY;

            if (plan.Elites.Any(e => e != null && e.X == x && e.Y == y && string.Equals(e.MobName, entry.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string first = ExpeditionElites.Affixes[rng.Next(ExpeditionElites.Affixes.Count)];
            EliteCamp pack = new()
            {
                MobName = entry.Name,
                Affix = first,
                ExtraAffixes = new List<string> { OtherAffix(rng, first) },
                RegionId = plan.RegionId,
                X = x,
                Y = y,
                Z = entry.Z,
                Radius = ActGenerator.EliteCampRadius,
                Level = entry.Levels.Max() + ExpeditionElites.LevelBonus + DreadLevelBonus
            };

            pack.Description = Describe(plan, pack);
            plan.Elites.Add(pack);
            added++;
        }

        plan.AllCampsElite = true;

        return added;
    }

    private static string OtherAffix(Random rng, string affix)
    {
        List<string> pool = ExpeditionElites.Affixes.Where(a => a != affix).ToList();

        return pool[rng.Next(pool.Count)];
    }

    private static string Describe(ActPlan plan, EliteCamp camp)
    {
        return $"A {ExpeditionElites.CampAffixText(camp)} pack of {camp.MobName} lairs {ActGenerator.DescribeFromCamp(plan.CampX, plan.CampY, camp.X, camp.Y)}";
    }

    #endregion

    #region Act boss

    /// <summary>True for the act boss of a hard run (it is never claimed as Dread).</summary>
    public static bool IsHardBoss(GameNPC npc)
    {
        if (npc == null || Leashes.IsEmpty)
        {
            return false;
        }

        foreach (BossLeash leash in Leashes.Values)
        {
            if (leash.Boss == npc)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Called by <see cref="ObjectiveTracker"/> right after the act boss (any GameNPC) is spawned: in a hard run it gets
    /// its second affix (<see cref="ExpeditionElites.AddBossAffixes"/>), a doubled tether when it has one, and the
    /// doubled leash of <see cref="TickBossLeash"/>. The player is told what the affixes do.
    /// </summary>
    internal static void OnBossSpawned(GamePlayer player, GameNPC boss)
    {
        if (boss == null || string.IsNullOrEmpty(player?.ObjectId) || !ExpeditionHardMode.IsHard(player))
        {
            return;
        }

        try
        {
            if (boss.TetherRange > 0)
            {
                boss.TetherRange *= BossLeashMultiplier;
            }

            Leashes[player.ObjectId] = new BossLeash { Boss = boss };
            List<string> added = ExpeditionElites.AddBossAffixes(boss, BossAffixCount, player.ObjectId, Random.Shared);

            foreach (string affix in added)
            {
                string effect = ExpeditionElites.AffixEffect(affix);

                if (effect.Length > 0)
                {
                    player.Out.SendMessage($"{boss.Name} is {affix}: {effect}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] Could not harden the act boss {boss.Name} for {player.Name}.", ex);
        }
    }

    /// <summary>
    /// Called on the tracker tick: the hard-run boss that has fought the player does not let go while the player is
    /// within twice the standard leash (<see cref="BossLeashMultiplier"/> x <see cref="StandardMobBrain.MAX_AGGRO_LIST_DISTANCE"/>).
    /// When its AI lets go (too far for its aggro list, or 25 s out of combat), the player goes back on its aggro list and
    /// it walks after them. Farther than that, dead, or in another region, the normal reset happens.
    /// </summary>
    internal static void TickBossLeash(GamePlayer player)
    {
        if (string.IsNullOrEmpty(player?.ObjectId) || Leashes.IsEmpty || !Leashes.TryGetValue(player.ObjectId, out BossLeash leash))
        {
            return;
        }

        GameNPC boss = leash.Boss;

        if (boss == null || !boss.IsAlive || boss.ObjectState != GameObject.eObjectState.Active)
        {
            Leashes.TryRemove(new KeyValuePair<string, BossLeash>(player.ObjectId, leash));

            return;
        }

        if (boss.Brain is not StandardMobBrain brain || brain is IControlledBrain)
        {
            return;
        }

        int leashRange = StandardMobBrain.MAX_AGGRO_LIST_DISTANCE * BossLeashMultiplier;

        if (!player.IsAlive || player.ObjectState != GameObject.eObjectState.Active || player.CurrentRegion != boss.CurrentRegion || !boss.IsWithinRadius(player, leashRange))
        {
            leash.Engaged = false;

            return;
        }

        // Close enough for the standard AI to chase and hit: it has the player.
        if (boss.TargetObject == player && boss.IsWithinRadius(player, boss.StickMaximumRange))
        {
            leash.Engaged = true;

            return;
        }

        if (!leash.Engaged)
        {
            return;
        }

        if (boss.IsReturningToSpawnPoint)
        {
            boss.CancelReturnToSpawnPoint();
        }

        brain.AddToAggroList(player, 1);

        if (brain.FSM != null && brain.FSM.GetCurrentState()?.StateType != eFSMStateType.AGGRO)
        {
            brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
        }

        if (!boss.IsWithinRadius(player, boss.StickMaximumRange))
        {
            boss.PathTo(new Point3D(player.X, player.Y, player.Z), boss.MaxSpeed);
        }
    }

    #endregion
}
