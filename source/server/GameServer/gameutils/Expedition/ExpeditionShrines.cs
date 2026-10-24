using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

    private static readonly ConcurrentDictionary<string, List<GameStaticItem>> Spawned = new(StringComparer.Ordinal);

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

        if (shrines != null && shrines.Count > 0)
        {
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
                    player.Out.SendMessage($"You discover the {ExpeditionBuffs.ShrineName(shrine.Group)}{(loc == null ? string.Empty : $" at {loc}")}. Walk up to it to receive its blessing.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
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
                Model = ShrineModel,
                Name = shrine.Used ? $"{ExpeditionBuffs.ShrineName(shrine.Group)} (spent)" : ExpeditionBuffs.ShrineName(shrine.Group)
            };

            if (item.AddToWorld())
            {
                objects.Add(item);
            }
        }

        Spawned[player.ObjectId] = objects;
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
            string state = shrine.Used ? "blessing received" : "not yet used";
            lines.Add($"Shrine: {ExpeditionBuffs.ShrineName(shrine.Group)}{(loc == null ? string.Empty : $" ({loc})")} - {state}{guarded}");
        }

        return lines;
    }
}
