using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DOL.AI.Brain;
using DOL.GS.PacketHandler;

namespace DOL.GS.Expedition;

/// <summary>
/// Wild shrines: three per act, out in the zone at monster camps (one beside an elite pack when the act has one). The
/// journal only gives a shrine's direction until the player comes within <see cref="DiscoverRadius"/>; then it shows
/// its /loc. Walking up to it (<see cref="UseRadius"/>) grants its lesser buff group (<see cref="ExpeditionBuffs"/>) for
/// the rest of the act. The shrine objects exist only while the player's tracker is attached.
/// </summary>
public static class ExpeditionShrines
{
    public const int ShrinesPerAct = 3;

    /// <summary>
    /// A hard expedition's shrines per act (owner 2026-10-29: "Shrines should require elite packs to get them. Entire pack
    /// needs to die to get it. Like the change we had before on DF"): two, each beside an elite pack and dormant until the
    /// whole pack is slain (<see cref="PlaceHard"/>). Fewer when the act has fewer elite packs.
    /// </summary>
    public const int HardShrinesPerAct = 2;
    public const int DiscoverRadius = 2000;
    public const int UseRadius = 300;

    /// <summary>A shrine keeps at least this far from the camp, every objective point and the other shrines.</summary>
    public const int MinDistance = 1500;

    /// <summary>The guarded shrine stands this far from its elite pack's camp point.</summary>
    private const int GuardOffset = 250;

    /// <summary>
    /// objects.csv 4170 "crystal_green_prop" (items.csv 2209, Expansion Only 0, so every client draws it): a green
    /// crystal with a real mesh. Alternatives with a mesh and no expansion: 4171 "crystal_red_prop", 4264 "Dell Crystal".
    /// </summary>
    public const ushort ShrineModel = 4170;

    /// <summary>objects.csv 4171 "crystal_red_prop": the act 5 shrine while it is dormant (owner 2026-10-27).</summary>
    public const ushort DormantModel = 4171;

    /// <summary>Act 5: kills of the camp's monster needed to wake its shrine, half the camp's spawns within these bounds.</summary>
    public const int DormantMinKills = 3;
    public const int DormantMaxKills = 6;

    /// <summary>Act 5: a kill counts for a shrine within this distance (2D) of its camp's centre.</summary>
    public const int DormantCampRadius = 1500;

    /// <summary>
    /// Act 5: the placement rule. 1 (fork-v76) moved the shrine 800-2,000 units from its camp, which put it far from the
    /// camp; 2 puts it beside the camp just outside its monsters' aggro range (ExpeditionArrival.FindShrineSpot); 3 does
    /// so from the floor beneath the camp (a flying camp put 2's shrine up in the walls); 4 only uses camps that con at least
    /// yellow to a level 42 arrival (<see cref="IsWorthyCamp"/>); 5 also leaves out camps deeper than the essence shredders
    /// (<see cref="DormantMaxCampLevel"/>); 6 judges a monster by its strongest anywhere in the zone; 7 puts Darkness Falls'
    /// three shrines at its three realm entrances, awake (<see cref="PlaceAtEntrances"/>); 8 by each entrance's exit portal,
    /// at the foot of the steps. A shrine placed by an older rule is moved again, keeping its camp and kills when
    /// that camp is still worthy.
    /// </summary>
    public const int DormantPlacementVersion = 8;

    /// <summary>The level a player is assumed to reach act 5 at (owner 2026-10-28) for <see cref="IsWorthyCamp"/>.</summary>
    public const int DormantArrivalLevel = 42;

    /// <summary>
    /// The strongest monster a shrine's camp may have (owner 2026-10-28: in Darkness Falls the deepest should be the
    /// shredders; essence shredders are levels 49 to 60, while the crawlers, hulks, nightmares, mahr and succubi go past 60).
    /// </summary>
    public const int DormantMaxCampLevel = 60;

    /// <summary>
    /// A camp worth waking a shrine at (owner 2026-10-28: Darkness Falls shrines were beside grey camps): its strongest
    /// monster cons at least yellow to a level <see cref="DormantArrivalLevel"/> player (level 38 or more), and is no
    /// stronger than <see cref="DormantMaxCampLevel"/>.
    /// </summary>
    public static bool IsWorthyCamp(IReadOnlyCollection<int> levels)
    {
        if (levels == null || levels.Count == 0)
        {
            return false;
        }

        int strongest = levels.Max();

        return strongest <= DormantMaxCampLevel && ConLevels.GetConLevel(DormantArrivalLevel, strongest) >= (int) ConColor.YELLOW;
    }

    private const string DormantWarnedProperty = "expedition.shrine.dormantWarned";
    private const long DormantWarnEveryMs = 10_000;

    private static readonly ConcurrentDictionary<string, List<GameStaticItem>> Spawned = new(StringComparer.Ordinal);

    /// <summary>The kills a camp of this many spawns asks for: half of them, from 3 to 6.</summary>
    public static int DormantKillsFor(int campSpawns)
    {
        return Math.Clamp(campSpawns / 2, DormantMinKills, DormantMaxKills);
    }

    /// <summary>True while the shrine still waits for kills.</summary>
    public static bool IsDormant(ShrineSpec shrine)
    {
        return shrine != null && shrine.KillsNeeded > 0 && shrine.Kills < shrine.KillsNeeded;
    }

    /// <summary>
    /// True when a monster of this name, killed at this point, counts for the shrine: its camp's monster (an elite's
    /// name carries an affix in front, "Swift essence shredder") within <see cref="DormantCampRadius"/> of the camp.
    /// </summary>
    /// <param name="ofCamp">The monster is an elite of the shrine's own pack (ExpeditionElites.CampOf): a hard shrine then counts it wherever it died.</param>
    public static bool CountsFor(ShrineSpec shrine, string name, int regionId, int x, int y, bool ofCamp = false)
    {
        if (!IsDormant(shrine) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(shrine.CampMob) || regionId != shrine.RegionId)
        {
            return false;
        }

        // A hard shrine counts only the pack's elites: the camp's monster with at least one affix in front.
        if (shrine.HardPack)
        {
            bool nearCamp = Dist2(x, y, shrine.CampX, shrine.CampY) <= (long) DormantCampRadius * DormantCampRadius;

            return MatchesCampMob(name, shrine.CampMob, out int affixes) && affixes > 0 && (ofCamp || nearCamp);
        }

        bool sameMob = string.Equals(name, shrine.CampMob, StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith(" " + shrine.CampMob, StringComparison.OrdinalIgnoreCase);

        return sameMob && Dist2(x, y, shrine.CampX, shrine.CampY) <= (long) DormantCampRadius * DormantCampRadius;
    }

    /// <summary>
    /// True when a monster's name is the camp's monster under its elite dressing: leading affix words
    /// (<see cref="ExpeditionElites.Affixes"/>, capitalised as the elites carry them; a hard expedition's elites may carry
    /// two, "Swift Venomous goblin") and a hard expedition's "Dread " prefix are stripped, then the rest is compared without
    /// case. <paramref name="affixes"/> is how many affix words were stripped (0 for a plain monster).
    /// </summary>
    public static bool MatchesCampMob(string name, string campMob, out int affixes)
    {
        affixes = 0;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(campMob))
        {
            return false;
        }

        string rest = name.Trim();
        string wanted = campMob.Trim();
        bool dreadStripped = false;

        while (true)
        {
            // A camp monster whose own name starts with an affix word or "Dread" matches before it is stripped.
            if (string.Equals(rest, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            int space = rest.IndexOf(' ');

            if (space <= 0)
            {
                return false;
            }

            string word = rest.Substring(0, space);

            if (ExpeditionElites.Affixes.Contains(word, StringComparer.Ordinal))
            {
                affixes++;
            }
            else if (!dreadStripped && string.Equals(word, "Dread", StringComparison.Ordinal))
            {
                dreadStripped = true;
            }
            else
            {
                return false;
            }

            rest = rest.Substring(space + 1).TrimStart();
        }
    }

    #region Generation

    /// <summary>
    /// Three shrines, each a different buff group, at zone camps away from the act camp, the objectives and each other;
    /// the first beside an elite pack when there is one. Fewer when the zone has too few camps.
    /// </summary>
    public static List<ShrineSpec> Choose(Random rng, IReadOnlyList<(int X, int Y, int Z)> campsGlobal, (int X, int Y) campPoint,
        IReadOnlyList<ActObjective> objectives, IReadOnlyList<EliteCamp> elites, int regionId, Func<int, int, string> directionFromCamp)
    {
        List<string> groups = ExpeditionBuffs.Groups.ToList();

        for (int i = groups.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (groups[i], groups[j]) = (groups[j], groups[i]);
        }

        var shrines = new List<ShrineSpec>();
        var taken = new List<(int X, int Y)> { campPoint };
        taken.AddRange(objectives.Select(o => (o.X, o.Y)));
        long minSquared = (long) MinDistance * MinDistance;

        bool FarEnough(int x, int y) => taken.All(t => Dist2(t.X, t.Y, x, y) > minSquared);

        // The guarded shrine: beside the first elite pack.
        EliteCamp guard = elites?.FirstOrDefault(e => e != null && FarEnough(e.X + GuardOffset, e.Y));

        if (guard != null)
        {
            Add(guard.X + GuardOffset, guard.Y, guard.Z, true);
        }

        List<(int X, int Y, int Z)> candidates = campsGlobal.Where(c => FarEnough(c.X, c.Y)).ToList();

        while (shrines.Count < ShrinesPerAct && candidates.Count > 0)
        {
            (int x, int y, int z) = candidates[rng.Next(candidates.Count)];
            Add(x, y, z, false);
            candidates = candidates.Where(c => FarEnough(c.X, c.Y)).ToList();
        }

        return shrines;

        void Add(int x, int y, int z, bool guarded)
        {
            shrines.Add(new ShrineSpec
            {
                Group = groups[shrines.Count % groups.Count],
                RegionId = regionId,
                X = x,
                Y = y,
                Z = z,
                Guarded = guarded,
                Direction = directionFromCamp(x, y)
            });
            taken.Add((x, y));
        }
    }

    private static long Dist2(int x1, int y1, int x2, int y2)
    {
        long dx = x1 - x2;
        long dy = y1 - y2;

        return dx * dx + dy * dy;
    }

    #endregion

    #region Tracker tick

    /// <summary>
    /// Called by the objective tracker's tick while the player is alive and in the world: puts the shrine objects in
    /// the world, discovers and uses shrines in range, and keeps the run's lesser buffs up.
    /// </summary>
    public static void Tick(GamePlayer player, ExpeditionRun run, ActPlan plan)
    {
        List<ShrineSpec> shrines = plan?.Shrines;
        bool changed = false;
        bool usedNow = false;
        bool hard = ExpeditionHardMode.IsHard(run);

        // A hard expedition: every act's shrines go beside its elite packs, dormant until each whole pack is slain.
        if (hard && plan != null && PlaceHard(player, plan))
        {
            changed = true;
            Despawn(player);
            shrines = plan.Shrines;
        }

        if (shrines != null && shrines.Count > 0)
        {
            if (!hard && PlaceDormant(player, plan))
            {
                changed = true;

                // The objects stand where the shrines were; the next line puts them at their new spots.
                Despawn(player);
            }

            EnsureSpawned(player, shrines);

            foreach (ShrineSpec shrine in shrines)
            {
                if (shrine == null || shrine.Used || player.CurrentRegionID != shrine.RegionId)
                {
                    continue;
                }

                long distance2 = Dist2(player.X, player.Y, shrine.X, shrine.Y);
                string loc = ActGenerator.DescribeLoc(shrine.RegionId, shrine.X, shrine.Y);

                if (!shrine.Discovered && distance2 <= (long) DiscoverRadius * DiscoverRadius)
                {
                    shrine.Discovered = true;
                    changed = true;

                    // On screen as well (owner, 2026-10-24): the journal just gained the shrine's /loc.
                    player.Out.SendMessage($"You discover the {ExpeditionBuffs.ShrineName(shrine.Group)}! Your journal shows where it is.", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage(IsDormant(shrine)
                        ? $"You discover the {ExpeditionBuffs.ShrineName(shrine.Group)}{(loc == null ? string.Empty : $" at {loc}")}. It lies dormant: slay {shrine.KillsNeeded - shrine.Kills} {shrine.CampMob} {CampWhere(shrine)} to wake it."
                        : $"You discover the {ExpeditionBuffs.ShrineName(shrine.Group)}{(loc == null ? string.Empty : $" at {loc}")}. Walk up to it to receive its blessing.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                }

                if (distance2 <= (long) UseRadius * UseRadius && IsDormant(shrine))
                {
                    long now = GameLoop.GameLoopTime;

                    if (now - player.TempProperties.GetProperty<long>(DormantWarnedProperty) >= DormantWarnEveryMs)
                    {
                        player.TempProperties.SetProperty(DormantWarnedProperty, now);
                        player.Out.SendMessage($"The {ExpeditionBuffs.ShrineName(shrine.Group)} is dormant. Slay {shrine.KillsNeeded - shrine.Kills} more {shrine.CampMob} {CampWhere(shrine)}.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    }

                    continue;
                }

                if (distance2 <= (long) UseRadius * UseRadius)
                {
                    shrine.Discovered = true;
                    shrine.Used = true;
                    changed = true;
                    usedNow = true;
                    run.AddLog($"Act {plan.Act}: blessed at the {ExpeditionBuffs.ShrineName(shrine.Group)}.");
                    player.Out.SendMessage($"The {ExpeditionBuffs.ShrineName(shrine.Group)} blesses you: lesser {ExpeditionBuffs.NameOf(shrine.Group).ToLowerInvariant()} for the rest of the act.", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"The {ExpeditionBuffs.ShrineName(shrine.Group)} blesses you: lesser {ExpeditionBuffs.NameOf(shrine.Group).ToLowerInvariant()} for the rest of the act.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                }
            }
        }

        ExpeditionBuffs.Maintain(player, run);

        if (changed)
        {
            ExpeditionManager.Save(run);
        }

        if (usedNow)
        {
            // The shrine objects carry their name from when they were spawned; the next tick puts them back with a
            // used shrine shown as "(spent)".
            Despawn(player);
        }
    }

    private static void EnsureSpawned(GamePlayer player, List<ShrineSpec> shrines)
    {
        if (Spawned.TryGetValue(player.ObjectId, out List<GameStaticItem> existing) && existing.All(o => o.ObjectState == GameObject.eObjectState.Active))
        {
            return;
        }

        Despawn(player);

        var objects = new List<GameStaticItem>();

        foreach (ShrineSpec shrine in shrines)
        {
            if (shrine == null)
            {
                continue;
            }

            Region region = WorldMgr.GetRegion((ushort) shrine.RegionId);

            if (region == null)
            {
                continue;
            }

            GameStaticItem item = new()
            {
                CurrentRegion = region,
                X = shrine.X,
                Y = shrine.Y,
                Z = shrine.Z,
                Heading = 0,
                Model = IsDormant(shrine) ? DormantModel : ShrineModel,
                Name = shrine.Used ? $"{ExpeditionBuffs.ShrineName(shrine.Group)} (spent)"
                    : IsDormant(shrine) ? $"{ExpeditionBuffs.ShrineName(shrine.Group)} (dormant {shrine.Kills}/{shrine.KillsNeeded})"
                    : ExpeditionBuffs.ShrineName(shrine.Group)
            };

            if (item.AddToWorld())
            {
                objects.Add(item);
            }
        }

        Spawned[player.ObjectId] = objects;
    }

    /// <summary>
    /// Darkness Falls (owner 2026-10-28: "put one shrine at each entrance of df. Mid, alb, and hib. Make sure it's reachable
    /// without having to go up the steps"; "put outside the portal that is down the steps"): the act's shrines go to the
    /// Albion, Midgard and Hibernia exit portals in turn (ExpeditionArrival.FindPortalShrineSpot), awake: no camp, no kills.
    /// </summary>
    private static bool PlaceAtEntrances(Region region, ActPlan plan)
    {
        bool changed = false;

        for (int i = 0; i < plan.Shrines.Count; i++)
        {
            ShrineSpec shrine = plan.Shrines[i];

            if (shrine == null || shrine.Used || shrine.PlacementVersion >= DormantPlacementVersion)
            {
                continue;
            }

            (string realm, (int X, int Y, int Z) portal, (int X, int Y) landing) = ExpeditionArrival.DarknessFallsEntrances[i % ExpeditionArrival.DarknessFallsEntrances.Length];
            (shrine.X, shrine.Y, shrine.Z) = ExpeditionArrival.FindPortalShrineSpot(region, portal, landing);
            shrine.Direction = $"at the {realm} entrance";
            shrine.CampMob = string.Empty;
            shrine.CampX = shrine.X;
            shrine.CampY = shrine.Y;
            shrine.CampZ = shrine.Z;
            shrine.KillsNeeded = 0;
            shrine.Kills = 0;
            shrine.Placed = true;
            shrine.PlacementVersion = DormantPlacementVersion;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Act 5: gives each unused shrine not yet placed by the current rule (<see cref="DormantPlacementVersion"/>) its world
    /// camp (the one nearest its site, or the camp it already had) and moves it beside that camp, just outside its
    /// monsters' aggro range (ExpeditionArrival.FindShrineSpot). True when a shrine changed.
    /// </summary>
    private static bool PlaceDormant(GamePlayer player, ActPlan plan)
    {
        if (plan.Act != ActGenerator.ActCount || plan.Shrines.All(shrine => shrine == null || shrine.Used || shrine.PlacementVersion >= DormantPlacementVersion))
        {
            return false;
        }

        Region region = player.CurrentRegion;

        if (region == null || region.ID != plan.RegionId)
        {
            return false;
        }

        if (plan.ZoneId == ActGenerator.DarknessFallsZoneId)
        {
            return PlaceAtEntrances(region, plan);
        }

        IReadOnlyList<AutonomousCapnBryGoalCatalog.Entry> allCamps = WorldCampSource.Instance.GetZoneCamps(plan.RegionId, plan.ZoneId);

        if (allCamps.Count == 0)
        {
            return false;
        }

        // Only camps that con at least yellow on arrival, of a monster no stronger anywhere in the zone than the shredders
        // (owner 2026-10-28: a camp of level 54-60 mahr passed on its own levels, but mahr go up to 67 and live deeper);
        // all of them if the zone has none.
        Dictionary<string, int> strongestByName = allCamps.Where(c => c.Levels is { Length: > 0 })
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(c => c.Levels.Max()), StringComparer.OrdinalIgnoreCase);
        List<AutonomousCapnBryGoalCatalog.Entry> camps = allCamps
            .Where(c => IsWorthyCamp(c.Levels) && strongestByName.GetValueOrDefault(c.Name) <= DormantMaxCampLevel).ToList();

        if (camps.Count == 0)
        {
            camps = allCamps.ToList();
        }

        (int ox, int oy) = ActGenerator.WorldZoneOffset(plan.ZoneId);
        bool changed = false;

        foreach (ShrineSpec shrine in plan.Shrines)
        {
            if (shrine == null || shrine.Used || shrine.PlacementVersion >= DormantPlacementVersion)
            {
                continue;
            }

            // A shrine placed by an older rule keeps its camp and its kills while that camp is still worthy; otherwise, and for
            // a new one, it takes the worthy camp nearest its site.
            AutonomousCapnBryGoalCatalog.Entry camp = shrine.Placed && !string.IsNullOrEmpty(shrine.CampMob)
                ? camps.Where(c => string.Equals(c.Name, shrine.CampMob, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => Dist2(ox + c.LocalX, oy + c.LocalY, shrine.CampX, shrine.CampY))
                    .FirstOrDefault()
                : null;

            if (camp == null)
            {
                camp = camps.OrderBy(c => Dist2(ox + c.LocalX, oy + c.LocalY, shrine.X, shrine.Y)).First();
                shrine.CampMob = camp.Name;
                shrine.KillsNeeded = DormantKillsFor(camp.Sightings);
                shrine.Kills = 0;
            }

            (int X, int Y, int Z) centre = (ox + camp.LocalX, oy + camp.LocalY, camp.Z);
            shrine.CampX = centre.X;
            shrine.CampY = centre.Y;
            shrine.CampZ = centre.Z;
            (int X, int Y, int Z)? spot = ExpeditionArrival.FindShrineSpot(region, plan.ZoneId, centre, player.Realm);

            if (spot != null)
            {
                shrine.X = spot.Value.X;
                shrine.Y = spot.Value.Y;
                shrine.Z = spot.Value.Z;
                shrine.Direction = ActGenerator.DescribeFromCamp(plan.CampX, plan.CampY, shrine.X, shrine.Y);
            }

            shrine.Placed = true;
            shrine.PlacementVersion = DormantPlacementVersion;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// A hard expedition (owner 2026-10-29): replaces the act's shrines, once, by up to <see cref="HardShrinesPerAct"/>
    /// shrines, one beside each of the act's first elite packs (<see cref="ChooseHardCamps"/>) just outside its monsters'
    /// aggro range (ExpeditionArrival.FindShrineSpot), each dormant until its whole pack is slain: KillsNeeded is the
    /// number of monsters the pack's wake-up makes elite (the camp's monster within the camp's radius,
    /// ExpeditionElites.WakeCamp). Keeps the act's buff groups. True when the shrines changed.
    /// </summary>
    private static bool PlaceHard(GamePlayer player, ActPlan plan)
    {
        List<ShrineSpec> current = plan.Shrines ?? new List<ShrineSpec>();

        if (current.Count <= HardShrinesPerAct && current.All(shrine => shrine != null && shrine.HardPack))
        {
            return false;
        }

        Region region = player.CurrentRegion;

        if (region == null || region.ID != plan.RegionId)
        {
            return false;
        }

        // The act's own groups first (seeded at generation), then any others when it had too few shrines.
        List<string> groups = current.Where(shrine => shrine != null && !string.IsNullOrEmpty(shrine.Group)).Select(shrine => shrine.Group).ToList();
        groups.AddRange(ExpeditionBuffs.Groups.Where(group => !groups.Contains(group)));

        var placed = new List<ShrineSpec>();

        foreach (EliteCamp camp in ChooseHardCamps(plan.Elites, HardShrinesPerAct))
        {
            int radius = camp.Radius > 0 ? camp.Radius : ActGenerator.EliteCampRadius;
            var found = new List<GameNPC>();
            region.GetInRadius(new Point3D(camp.X, camp.Y, camp.Z), eGameObjectType.NPC, (ushort) Math.Clamp(radius, 1, ushort.MaxValue), found);

            // The pack as the wake-up sees it (ExpeditionElites.CanBecomeElite): the camp's monster, not a bot, boss or pet.
            int pack = found.Count(npc => npc is not GameBot && npc is not ExpeditionBoss && npc.Brain is not IControlledBrain
                && MatchesCampMob(npc.Name, camp.MobName, out _));
            (int X, int Y, int Z) centre = (camp.X, camp.Y, camp.Z);
            (int X, int Y, int Z) spot = ExpeditionArrival.FindShrineSpot(region, plan.ZoneId, centre, player.Realm) ?? (camp.X + GuardOffset, camp.Y, camp.Z);

            placed.Add(new ShrineSpec
            {
                Group = groups[placed.Count % groups.Count],
                RegionId = plan.RegionId,
                X = spot.X,
                Y = spot.Y,
                Z = spot.Z,
                Guarded = true,
                Direction = ActGenerator.DescribeFromCamp(plan.CampX, plan.CampY, spot.X, spot.Y),
                Placed = true,
                CampMob = camp.MobName,
                CampX = camp.X,
                CampY = camp.Y,
                CampZ = camp.Z,

                // A camp found empty (its monsters dead or not loaded) still asks for a small pack.
                KillsNeeded = pack > 0 ? pack : DormantMinKills,
                Kills = 0,
                PlacementVersion = DormantPlacementVersion,
                HardPack = true
            });
        }

        plan.Shrines = placed;

        return true;
    }

    /// <summary>
    /// The elite packs a hard expedition's shrines stand beside: the first of the act's elite camps, then each next one
    /// farthest from those already chosen, up to <paramref name="count"/>. Fewer when the act has fewer.
    /// </summary>
    public static List<EliteCamp> ChooseHardCamps(IReadOnlyList<EliteCamp> elites, int count)
    {
        List<EliteCamp> left = elites?.Where(camp => camp != null && !string.IsNullOrEmpty(camp.MobName)).ToList() ?? new List<EliteCamp>();
        var chosen = new List<EliteCamp>();

        while (chosen.Count < count && left.Count > 0)
        {
            EliteCamp next = chosen.Count == 0
                ? left[0]
                : left.OrderByDescending(camp => chosen.Min(other => Dist2(camp.X, camp.Y, other.X, other.Y))).First();
            chosen.Add(next);
            left.Remove(next);
        }

        return chosen;
    }

    /// <summary>"at the camp around /loc x, y" for a dormant shrine's messages, or "at the camp beside it" without a zone.</summary>
    public static string CampWhere(ShrineSpec shrine)
    {
        string loc = ActGenerator.DescribeLoc(shrine.RegionId, shrine.CampX, shrine.CampY);

        return loc == null ? "at the camp beside it" : $"at the camp around {loc}";
    }

    /// <summary>
    /// Act 5: a kill the tracker credited to the player. A kill of a dormant shrine's camp monster near its camp counts
    /// toward waking it; the shrine turns green (respawned by the next tick) once it has its kills.
    /// </summary>
    public static void OnKill(GamePlayer player, ExpeditionRun run, ActPlan plan, GameLiving target)
    {
        // Act 5's dormant shrines, and every act's on a hard expedition.
        if (player == null || run == null || plan?.Shrines == null || target == null || (plan.Act != ActGenerator.ActCount && !ExpeditionHardMode.IsHard(run)))
        {
            return;
        }

        EliteCamp eliteCamp = target is GameNPC npc ? ExpeditionElites.CampOf(npc) : null;

        bool changed = false;

        foreach (ShrineSpec shrine in plan.Shrines)
        {
            bool ofCamp = eliteCamp != null && shrine != null && eliteCamp.X == shrine.CampX && eliteCamp.Y == shrine.CampY;

            if (!CountsFor(shrine, target.Name, target.CurrentRegionID, target.X, target.Y, ofCamp))
            {
                continue;
            }

            shrine.Kills++;
            changed = true;
            string name = ExpeditionBuffs.ShrineName(shrine.Group);

            if (IsDormant(shrine))
            {
                player.Out.SendMessage($"The {name} stirs ({shrine.Kills}/{shrine.KillsNeeded}).", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                continue;
            }

            string loc = ActGenerator.DescribeLoc(shrine.RegionId, shrine.X, shrine.Y);
            run.AddLog($"Act {plan.Act}: the {name} awakened.");
            player.Out.SendMessage($"The {name} awakens!", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
            player.Out.SendMessage($"The {name} awakens{(loc == null ? string.Empty : $" at {loc}")}. Walk up to it to receive its blessing.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        }

        if (changed)
        {
            ExpeditionManager.Save(run);

            // Respawned on the next tick with the new name (and the green crystal once awake).
            Despawn(player);
        }
    }

    /// <summary>Takes the player's shrine objects out of the world (the tracker detached: act change, quit, run end).</summary>
    public static void Despawn(GamePlayer player)
    {
        if (player == null || !Spawned.TryRemove(player.ObjectId, out List<GameStaticItem> objects))
        {
            return;
        }

        foreach (GameStaticItem item in objects)
        {
            try
            {
                item.Delete();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }
    }

    #endregion

    /// <summary>Journal lines: a discovered shrine's /loc and state; an undiscovered one only its direction.</summary>
    public static IReadOnlyList<string> Describe(ActPlan plan)
    {
        var lines = new List<string>();

        foreach (ShrineSpec shrine in plan?.Shrines ?? [])
        {
            if (shrine == null)
            {
                continue;
            }

            string guarded = shrine.Guarded ? " (guarded by an elite pack)" : string.Empty;

            if (!shrine.Discovered)
            {
                lines.Add($"Shrine: something glimmers {shrine.Direction}{guarded}");

                continue;
            }

            string loc = ActGenerator.DescribeLoc(shrine.RegionId, shrine.X, shrine.Y);
            string state = shrine.Used ? "blessing received"
                : IsDormant(shrine) ? $"dormant: slay {shrine.Kills}/{shrine.KillsNeeded} {shrine.CampMob} {CampWhere(shrine)}"
                : "not yet used";
            lines.Add($"Shrine: {ExpeditionBuffs.ShrineName(shrine.Group)}{(loc == null ? string.Empty : $" ({loc})")} - {state}{guarded}");
        }

        return lines;
    }
}
