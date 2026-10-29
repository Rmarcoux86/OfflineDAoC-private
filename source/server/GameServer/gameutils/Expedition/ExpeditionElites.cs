using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.Spells;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// Elite packs: one or two seeded camps per act (<see cref="ActPlan.Elites"/>, chosen by <see cref="ActGenerator"/>)
/// whose live monsters are buffed with an affix when the run's player first comes near, and always drop Rare or better
/// (<see cref="ExpeditionLoot.OnKill"/> asks <see cref="IsElite"/>).
/// <para>
/// The monsters are the world's own spawns, so every change is undone: the original values are kept per monster and
/// restored when it dies (GameNPC respawns reuse the same object, GameNPC.RespawnTimerCallback), when the player's
/// tracker detaches (<see cref="ObjectiveTracker"/>), and on server stop. The Dying event comes after EnemyKilled
/// (GameLiving.ProcessDeath), so the loot roll still sees the monster as elite.
/// </para>
/// Affixes (all on top of level +2, size +15% and double health):
/// Swift (run speed x1.4, swings 20% faster), Venomous (melee hits may poison), Armoured (more armor absorption),
/// Stalwart (triple health, shorter mez/stun/snare), Frenzied (+30% melee damage once below half health),
/// Spectral (translucent, and stealthed until it fights).
/// </summary>
public static class ExpeditionElites
{
    public const string Swift = "Swift";
    public const string Venomous = "Venomous";
    public const string Armoured = "Armoured";
    public const string Stalwart = "Stalwart";
    public const string Frenzied = "Frenzied";
    public const string Spectral = "Spectral";

    /// <summary>The affix pool, in the order the generator's seeded RNG indexes it. Append only: reordering changes seeded runs.</summary>
    public static readonly IReadOnlyList<string> Affixes = new[] { Swift, Venomous, Armoured, Stalwart, Frenzied, Spectral };

    /// <summary>The player must be this close (2D) to an elite camp for its pack to wake.</summary>
    public const int ApproachRadius = 3000;

    public const int LevelBonus = 2;
    public const int SizePercent = 115;
    public const int HealthMultiplier = 2;
    public const int StalwartHealthMultiplier = 3;
    public const double SwiftRunSpeedFactor = 1.4;
    public const int SwiftMeleeSpeedBonus = 20;
    public const int ArmouredArmorAbsorption = 20;
    public const int ArmouredPhysicalAbsorption = 10;
    public const int StalwartCrowdControlReduction = 50;
    public const int FrenzyHealthPercent = 50;
    public const int FrenzyMeleeDamageBonus = 30;
    public const int VenomChancePercent = 35;
    public const long VenomCooldownMs = 6000;

    // A fixed id and effect group for the venom DoT, so a new bite refreshes the old one and stacks with other DoTs.
    private const int VenomSpellId = 1970001;
    private const int VenomEffectGroup = 1970001;
    private const ushort VenomIcon = 4099;
    private const ushort FrenzyMessageRadius = 1500;

    private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly ConcurrentDictionary<GameNPC, EliteState> Elites = new();
    private static readonly object HandlerSync = new();
    private static bool _handlersRegistered;

    /// <summary>What an elite was before its affix, and what was added to it.</summary>
    private sealed class EliteState
    {
        public string OwnerKey;
        public ExpeditionRun Run;
        public EliteCamp Camp;

        // Its spawn was slain before this act: a repeat kill (lesser rewards).
        public bool Repeat;
        public string Affix;
        public string OriginalName;
        public byte OriginalLevel;
        public byte OriginalSize;
        public short OriginalMaxSpeed;
        public bool SpeedChanged;
        public GameNPC.eFlags AddedFlags;
        public readonly List<(eProperty Property, int Amount)> Bonuses = new();
        public bool Frenzied;
        public long NextVenomAt;
    }

    /// <summary>True when the monster belongs to an elite pack of an active run.</summary>
    public static bool IsElite(GameNPC npc)
    {
        return npc != null && Elites.ContainsKey(npc);
    }

    /// <summary>
    /// True for an elite whose spawn was already slain this act (owner 2026-10-28): a repeat kill keeps the Rare-or-better
    /// drop with half the Unique weight (<see cref="RepeatUniqueWeightDivisor"/>) and gives normal realm points.
    /// </summary>
    public static bool IsRepeat(GameNPC npc)
    {
        return npc != null && Elites.TryGetValue(npc, out EliteState state) && state.Repeat;
    }

    /// <summary>The elite camp an elite of a run belongs to; null for a monster that is not one (or a Gauntlet champion).</summary>
    public static EliteCamp CampOf(GameNPC npc)
    {
        return npc != null && Elites.TryGetValue(npc, out EliteState state) ? state.Camp : null;
    }

    /// <summary>A repeat elite kill's Unique weight is divided by this (the other rarities' weights are multiplied).</summary>
    public const int RepeatUniqueWeightDivisor = 2;

    /// <summary>Realm points for a first elite kill (owner 2026-10-28): about its health over the normal monster's.</summary>
    public const double EliteRealmPointMultiplier = 2.2;
    public const double StalwartRealmPointMultiplier = 3.3;

    /// <summary>
    /// The realm point multiplier of a kill: 2.2 for an elite, 3.3 for a Stalwart one, 1 for a repeat kill (its spawn
    /// slain before this act) and for a monster that is not elite.
    /// </summary>
    public static double RealmPointMultiplier(GameNPC npc)
    {
        if (npc == null || !Elites.TryGetValue(npc, out EliteState state) || state.Repeat)
        {
            return 1.0;
        }

        return state.Affix == Stalwart ? StalwartRealmPointMultiplier : EliteRealmPointMultiplier;
    }

    /// <summary>Journal lines about the act's elite packs; empty when the act has none.</summary>
    public static IReadOnlyList<string> DescribeElites(ActPlan plan)
    {
        List<string> lines = new();

        if (plan?.Elites == null)
        {
            return lines;
        }

        foreach (EliteCamp camp in plan.Elites)
        {
            if (camp != null && !string.IsNullOrEmpty(camp.Description))
            {
                string loc = ActGenerator.DescribeLoc(camp.RegionId, camp.X, camp.Y);
                lines.Add(loc == null ? $"Elite: {camp.Description}" : $"Elite: {camp.Description} ({loc})");
            }
        }

        return lines;
    }

    /// <summary>The elite name of a monster: "{Affix} {Name}".</summary>
    public static string EliteName(string affix, string mobName)
    {
        return $"{affix} {mobName}";
    }

    /// <summary>What every elite gets on top of its affix, in one line for the status window.</summary>
    public const string CommonEffect = "All elites: +2 levels, double health, bigger, Rare+ loot.";

    /// <summary>What an affix does, in a few words for the status window and the approach message.</summary>
    public static string AffixEffect(string affix)
    {
        return affix switch
        {
            Swift => $"Runs {SwiftRunSpeedFactor:0.#}x as fast, swings {SwiftMeleeSpeedBonus}% faster.",
            Venomous => $"Hits can poison ({VenomChancePercent}% chance): body damage over 15s.",
            Armoured => $"+{ArmouredArmorAbsorption}% armor and +{ArmouredPhysicalAbsorption}% physical absorption.",
            Stalwart => $"Triple health; mez, stun and snare last {StalwartCrowdControlReduction}% as long.",
            Frenzied => $"+{FrenzyMeleeDamageBonus}% melee damage below {FrenzyHealthPercent}% health.",
            Spectral => "Translucent and stealthed until it fights.",
            _ => string.Empty
        };
    }

    /// <summary>The approach message, sent once per camp and attach.</summary>
    public static string ApproachMessage(EliteCamp camp)
    {
        return $"You sense a {camp.Affix} pack of {camp.MobName} nearby.";
    }

    #region Waking packs (tracker tick)

    /// <summary>
    /// Called from <see cref="ObjectiveTracker"/>'s tick (game loop) for the attached player: wakes each elite camp of the
    /// plan the player is near and that this attach has not woken yet. <paramref name="wokenCamps"/> holds the woken
    /// camp indexes and belongs to the tracker state. A camp whose monsters are all dead or fighting is tried again on a
    /// later tick.
    /// </summary>
    internal static void CheckCamps(GamePlayer player, ExpeditionRun run, ActPlan plan, HashSet<int> wokenCamps)
    {
        if (player?.CurrentRegion == null || plan?.Elites == null || wokenCamps == null || string.IsNullOrEmpty(player.ObjectId))
        {
            return;
        }

        for (int i = 0; i < plan.Elites.Count; i++)
        {
            EliteCamp camp = plan.Elites[i];

            if (camp == null || wokenCamps.Contains(i) || camp.RegionId != player.CurrentRegionID || string.IsNullOrEmpty(camp.MobName))
            {
                continue;
            }

            Point3D point = new(camp.X, camp.Y, camp.Z);

            if (!player.IsWithinRadius(point, ApproachRadius, true))
            {
                continue;
            }

            int woken = WakeCamp(player, run, camp, point, out int repeats);

            if (woken == 0)
            {
                continue;
            }

            wokenCamps.Add(i);
            player.Out.SendMessage(ApproachMessage(camp), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            string effect = AffixEffect(camp.Affix);

            if (effect.Length > 0)
            {
                player.Out.SendMessage($"{camp.Affix}: {effect}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }

            if (repeats > 0)
            {
                string them = repeats == woken ? "The pack" : $"{repeats} of them";
                player.Out.SendMessage($"{them} you have slain before this act: lesser spoils (normal realm points, half the Unique chance).", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }

            ExpeditionDebug.Report(player, $"elite camp {i} ({camp.Affix} {camp.MobName}) woke {woken} monsters.");
        }
    }

    private static int WakeCamp(GamePlayer player, ExpeditionRun run, EliteCamp camp, Point3D point, out int repeats)
    {
        repeats = 0;
        HashSet<string> slain;

        lock (camp.Slain)
        {
            slain = new HashSet<string>(camp.Slain, StringComparer.Ordinal);
        }

        List<GameNPC> found = new();
        int radius = Math.Clamp(camp.Radius > 0 ? camp.Radius : ActGenerator.EliteCampRadius, 1, ushort.MaxValue);
        player.CurrentRegion.GetInRadius(point, eGameObjectType.NPC, (ushort)radius, found);
        int woken = 0;

        foreach (GameNPC npc in found)
        {
            if (!CanBecomeElite(npc, camp.MobName))
            {
                continue;
            }

            // An elite already slain this act comes back (its respawn is the same spawn) as a repeat kill.
            bool repeat = npc.InternalID != null && slain.Contains(npc.InternalID);

            try
            {
                if (Apply(npc, camp.Affix, player.ObjectId, run, camp, repeat))
                {
                    woken++;

                    if (repeat)
                    {
                        repeats++;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"[EXPEDITION] Could not make {npc.Name} elite for {player.Name}.", ex);
                RestoreOne(npc);
            }
        }

        return woken;
    }

    private static bool CanBecomeElite(GameNPC npc, string mobName)
    {
        if (npc == null || npc is GameBot || npc is ExpeditionBoss || npc.Brain is IControlledBrain)
        {
            return false;
        }

        if (!npc.IsAlive || npc.InCombat || Elites.ContainsKey(npc))
        {
            return false;
        }

        return string.Equals(npc.Name, mobName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Apply(GameNPC npc, string affix, string ownerKey, ExpeditionRun run = null, EliteCamp camp = null, bool repeat = false)
    {
        EliteState state = new()
        {
            OwnerKey = ownerKey,
            Run = run,
            Camp = camp,
            Repeat = repeat,
            Affix = affix,
            OriginalName = npc.Name,
            OriginalLevel = npc.Level,
            OriginalSize = npc.Size,
            OriginalMaxSpeed = npc.MaxSpeedBase
        };

        if (!Elites.TryAdd(npc, state))
        {
            return false;
        }

        EnsureHandlers();

        // Level first: the Level setter re-derives the stats the health formula reads (GameNPC.Level, SetStats).
        npc.Level = (byte)Math.Min(byte.MaxValue, npc.Level + LevelBonus);

        // GameNPC health is (constitution + level curve + BaseBuffBonusCategory[MaxHealth]) x MaxHealthScalingFactor
        // (MaxHealthCalculator); a flat buff bonus of (multiplier - 1) x the current maximum multiplies it.
        int multiplier = affix == Stalwart ? StalwartHealthMultiplier : HealthMultiplier;
        double scaling = Math.Max(0.01, npc.MaxHealthScalingFactor);
        AddBonus(npc, state, eProperty.MaxHealth, (int)Math.Round(npc.MaxHealth * (multiplier - 1) / scaling));
        ApplyAffix(npc, state, affix);

        // Venomous and Frenzied act through the AttackedByEnemy handler.
        npc.Size = (byte)Math.Clamp(npc.Size * SizePercent / 100, 1, byte.MaxValue);
        npc.Name = EliteName(affix, state.OriginalName);
        npc.Health = npc.MaxHealth;

        return true;
    }

    /// <summary>
    /// A Gauntlet champion's affix (owner 2026-10-28): the affix's own effect and its name before the champion's, without
    /// an elite's extra levels, size or health (Stalwart's triple health is GauntletChampion.MaxHealth). The champion is
    /// forgotten on its death (the Dying handler) or by <see cref="Forget"/> when it is removed. It never counts for loot or
    /// realm points: a champion is a bot, never a lootable kill.
    /// </summary>
    /// <param name="swiftSwings">Swift's faster swings too (owner 2026-10-28: Preesh only; other champions get its run speed only).</param>
    public static bool ApplyChampionAffix(GameNPC npc, string affix, bool swiftSwings = false)
    {
        if (npc == null || string.IsNullOrEmpty(affix))
        {
            return false;
        }

        EliteState state = new()
        {
            Affix = affix,
            OriginalName = npc.Name,
            OriginalLevel = npc.Level,
            OriginalSize = npc.Size,
            OriginalMaxSpeed = npc.MaxSpeedBase
        };

        if (!Elites.TryAdd(npc, state))
        {
            return false;
        }

        EnsureHandlers();
        ApplyAffix(npc, state, affix, swiftSwings);
        npc.Name = EliteName(affix, state.OriginalName);

        return true;
    }

    /// <summary>Takes the monster's elite state off and restores it (a champion removed from the arena).</summary>
    public static void Forget(GameNPC npc)
    {
        RestoreOne(npc);
    }

    /// <summary>An affix's own effect (Venomous and Frenzied act through the AttackedByEnemy handler).</summary>
    /// <param name="swiftSwings">Swift's faster swings as well as its run speed (false for most Gauntlet champions, owner 2026-10-28).</param>
    private static void ApplyAffix(GameNPC npc, EliteState state, string affix, bool swiftSwings = true)
    {
        switch (affix)
        {
            case Swift:
            {
                if (npc.MaxSpeedBase > 0)
                {
                    npc.MaxSpeedBase = (short)Math.Min(short.MaxValue, Math.Round(npc.MaxSpeedBase * SwiftRunSpeedFactor));
                    state.SpeedChanged = true;
                }

                // NPC swing time is base x MeleeSpeed% (MeleeSpeedPercentCalculator: 100 - buff for NPCs).
                if (swiftSwings)
                {
                    AddBonus(npc, state, eProperty.MeleeSpeed, SwiftMeleeSpeedBonus);
                }

                break;
            }
            case Armoured:
            {
                AddBonus(npc, state, eProperty.ArmorAbsorption, ArmouredArmorAbsorption);
                AddBonus(npc, state, eProperty.PhysicalAbsorption, ArmouredPhysicalAbsorption);
                break;
            }
            case Stalwart:
            {
                AddBonus(npc, state, eProperty.MesmerizeDurationReduction, StalwartCrowdControlReduction);
                AddBonus(npc, state, eProperty.StunDurationReduction, StalwartCrowdControlReduction);
                AddBonus(npc, state, eProperty.SpeedDecreaseDurationReduction, StalwartCrowdControlReduction);
                break;
            }
            case Spectral:
            {
                // STEALTH is lifted by the mob brain when it aggroes (StandardMobState_AGGRO.Enter) and when attacked.
                GameNPC.eFlags spectral = GameNPC.eFlags.GHOST | GameNPC.eFlags.STEALTH;
                state.AddedFlags = spectral & ~npc.Flags;
                npc.Flags |= spectral;
                break;
            }
        }
    }

    private static void AddBonus(GameNPC npc, EliteState state, eProperty property, int amount)
    {
        if (amount == 0)
        {
            return;
        }

        // RestoreOne removes the state before it takes this lock, so a bonus is never added after the restore.
        lock (state)
        {
            if (!Elites.TryGetValue(npc, out EliteState current) || current != state)
            {
                return;
            }

            npc.BaseBuffBonusCategory[property] += amount;
            state.Bonuses.Add((property, amount));
        }
    }

    #endregion

    #region Restoring

    /// <summary>Restores every elite woken by this player's run (tracker detach, act change, run end).</summary>
    public static void RestoreAll(GamePlayer player)
    {
        string key = player?.ObjectId;

        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        foreach (KeyValuePair<GameNPC, EliteState> entry in Elites)
        {
            if (entry.Value.OwnerKey == key)
            {
                RestoreOne(entry.Key);
            }
        }
    }

    /// <summary>Restores every elite of every run (server stop).</summary>
    public static void RestoreAll()
    {
        foreach (GameNPC npc in Elites.Keys)
        {
            RestoreOne(npc);
        }
    }

    /// <summary>Removes the monster's elite state and puts its original values back; false when it was not elite.</summary>
    private static bool RestoreOne(GameNPC npc)
    {
        if (npc == null || !Elites.TryRemove(npc, out EliteState state))
        {
            return false;
        }

        try
        {
            lock (state)
            {
                foreach ((eProperty property, int amount) in state.Bonuses)
                {
                    npc.BaseBuffBonusCategory[property] -= amount;
                }

                state.Bonuses.Clear();
            }

            if (state.SpeedChanged)
            {
                npc.MaxSpeedBase = state.OriginalMaxSpeed;
            }

            if (state.AddedFlags != 0)
            {
                npc.Flags &= ~state.AddedFlags;
            }

            // The Level setter also clamps health to the restored maximum. It goes before the name, whose setter
            // re-creates the monster for nearby clients (as in Apply), so they see the restored level too.
            npc.Level = state.OriginalLevel;
            npc.Size = state.OriginalSize;
            npc.Name = state.OriginalName;
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] Could not restore elite monster {state.OriginalName}.", ex);
        }

        return true;
    }

    #endregion

    #region Events

    private static void EnsureHandlers()
    {
        lock (HandlerSync)
        {
            if (_handlersRegistered)
            {
                return;
            }

            GameEventMgr.AddHandler(GameLivingEvent.AttackedByEnemy, OnAttackedByEnemy);
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
                GameEventMgr.RemoveHandler(GameLivingEvent.AttackedByEnemy, OnAttackedByEnemy);
                GameEventMgr.RemoveHandler(GameLivingEvent.Dying, OnDying);
                _handlersRegistered = false;
            }
        }

        RestoreAll();
    }

    /// <summary>Global Dying handler: an elite that dies respawns as its plain self (the same GameNPC object).</summary>
    private static void OnDying(DOLEvent e, object sender, EventArgs args)
    {
        if (Elites.IsEmpty || sender is not GameNPC npc)
        {
            return;
        }

        // Remember the slain elite's spawn on its camp: a later login makes its respawn a repeat kill.
        if (Elites.TryGetValue(npc, out EliteState state) && state.Camp != null && npc.InternalID != null)
        {
            bool added;

            lock (state.Camp.Slain)
            {
                added = !state.Camp.Slain.Contains(npc.InternalID);

                if (added)
                {
                    state.Camp.Slain.Add(npc.InternalID);
                }
            }

            if (added && state.Run != null)
            {
                try
                {
                    ExpeditionManager.Save(state.Run);
                }
                catch (Exception ex)
                {
                    log.Error("[EXPEDITION] Could not save a slain elite.", ex);
                }
            }
        }

        RestoreOne(npc);
    }

    /// <summary>
    /// Global AttackedByEnemy handler (GameLiving.OnAttackedByEnemy notifies it for every attack): a Venomous elite's
    /// melee hit may poison its target; a Frenzied elite that drops to half health hits harder.
    /// </summary>
    private static void OnAttackedByEnemy(DOLEvent e, object sender, EventArgs args)
    {
        if (Elites.IsEmpty || args is not AttackedByEnemyEventArgs attackedArgs || attackedArgs.AttackData == null)
        {
            return;
        }

        AttackData ad = attackedArgs.AttackData;

        try
        {
            if (ad.Attacker is GameNPC attacker && Elites.TryGetValue(attacker, out EliteState attackerState) && attackerState.Affix == Venomous)
            {
                TryVenom(attacker, attackerState, ad);
            }

            if (sender is GameNPC defender && Elites.TryGetValue(defender, out EliteState defenderState) && defenderState.Affix == Frenzied)
            {
                TryFrenzy(defender, defenderState);
            }
        }
        catch (Exception ex)
        {
            log.Error("[EXPEDITION] Elite affix handling failed.", ex);
        }
    }

    private static void TryVenom(GameNPC attacker, EliteState state, AttackData ad)
    {
        if (!ad.IsMeleeAttack || ad.AttackResult is not (eAttackResult.HitUnstyled or eAttackResult.HitStyle))
        {
            return;
        }

        GameLiving target = ad.Target;

        if (target == null || !target.IsAlive || !attacker.IsAlive)
        {
            return;
        }

        long now = GameLoop.GameLoopTime;

        lock (state)
        {
            if (now < state.NextVenomAt || !Util.Chance(VenomChancePercent))
            {
                return;
            }

            state.NextVenomAt = now + VenomCooldownMs;
        }

        // The weapon-proc route (GameLiving.StartWeaponMagicalEffect): a spell handler started straight on the target.
        ISpellHandler handler = ScriptMgr.CreateSpellHandler(attacker, CreateVenomSpell(attacker.Level), SkillBase.GetSpellLine(GlobalSpellsLines.Mob_Spells));
        handler?.StartSpell(target);
    }

    private static Spell CreateVenomSpell(int level)
    {
        DbSpell spell = new()
        {
            AllowAdd = false,
            SpellID = VenomSpellId,
            Name = "Elite Venom",
            Description = "A poisoned bite that burns for 15 seconds.",
            Icon = VenomIcon,
            ClientEffect = VenomIcon,
            TooltipId = VenomIcon,
            Damage = Math.Max(3, Math.Round(level * 1.2)),
            DamageType = (int)eDamageType.Body,
            Duration = 15,
            Frequency = 30,
            Range = 0,
            Target = eSpellTarget.ENEMY.ToString(),
            Type = eSpellType.DamageOverTime.ToString(),
            EffectGroup = VenomEffectGroup,
            Uninterruptible = true,
            Message1 = "Venom burns in your veins!",
            Message2 = "{0} is poisoned!",
            Message3 = "The venom wears off.",
            Message4 = "{0} looks healthy again."
        };

        return new Spell(spell, Math.Max(1, level));
    }

    private static void TryFrenzy(GameNPC defender, EliteState state)
    {
        if (!defender.IsAlive || defender.HealthPercent > FrenzyHealthPercent)
        {
            return;
        }

        lock (state)
        {
            if (state.Frenzied)
            {
                return;
            }

            state.Frenzied = true;
        }

        AddBonus(defender, state, eProperty.MeleeDamage, FrenzyMeleeDamageBonus);
        string text = $"{defender.GetName(0, true)} flies into a frenzy!";

        foreach (GamePlayer player in defender.GetPlayersInRadius(FrenzyMessageRadius))
        {
            player.Out.SendMessage(text, eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        }
    }

    #endregion
}
