using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using DOL.GS.Scheduler;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// A hard run's dark sky (owner 2026-10-29: "make sure you get the weather right"): while the run's player is in the act's
/// region, the region has a storm of the server's own kind (WeatherManager, RegionWeather) laid over the whole of it and
/// kept there; it is stopped when the player leaves the region, the act changes, the run ends or the player logs out.
/// <para>
/// How the server's weather works (RegionWeather, WeatherManager): weather is per region, a front at
/// <c>CurrentPosition</c> (Position plus Speed units a second since it began) covering the X range
/// <c>(CurrentPosition - Width, CurrentPosition)</c> over the whole Y axis (the manager's "sky clears" test and the two
/// brains that read it, AlluvianGlobuleBrain and WaterElementalBrain, all use that range). It ends at DueTime, when its
/// back edge has passed the region's east edge, and the manager then rolls random weather again. The client gets
/// (CurrentPosition, Width, Speed, FogDiffusion, Intensity) in one Weather packet (PacketLib168.SendWeather: position and
/// width as 32-bit ints) on region entry and on every change.
/// </para>
/// <para>
/// The storm here: its front at the region's east edge (the largest zone XOffset + Width) and as wide as that, so its back
/// edge starts at X 0, west of every zone (zone offsets start at 8192), and the whole region is under it at once; at the
/// slowest speed (1 unit a second, the least CreateWeather allows) the back edge creeps east a unit a second and the storm
/// lasts as many seconds as the region is wide (a day or more). It is started again when the back edge comes within
/// <see cref="RestartMarginUnits"/> of the act zone, or when the region's weather is no longer this storm.
/// </para>
/// </summary>
public static class ExpeditionWeather
{
    /// <summary>The slowest a storm can move (RegionWeather.CreateWeather clamps the speed to at least 1).</summary>
    public const ushort StormSpeed = 1;

    /// <summary>
    /// Rain and darkness: the top of the server's own random weather (30-110) and below 120, where RegionWeather says the
    /// client gives "weird results". WaterElementalBrain counts 100 and up as its heaviest rain.
    /// </summary>
    public const ushort StormIntensity = 110;

    /// <summary>
    /// Fog diffusion: the random weather uses 16000 (the densest fog) to 32000; 30000 is near the thin end, so the sky
    /// darkens without fog hiding the zone.
    /// </summary>
    public const ushort StormFogDiffusion = 30000;

    /// <summary>The storm is laid again when its back edge comes this close (units, so seconds) to the act zone.</summary>
    public const int RestartMarginUnits = 600;

    /// <summary>Never laid again sooner than this after the last time (a zone at X 0 would otherwise ask every tick).</summary>
    private const long RestartMinMs = 10 * 60 * 1000;

    /// <summary>How long the player must have stood in the region before the weather packet is sent (as ExpeditionManager.WhenSettled).</summary>
    private const long SettleMs = 3000;

    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    private sealed class Storm
    {
        public ushort RegionId;
        public long InRegionSince;
        public bool Started;
        public long StartedAt;
    }

    /// <summary>The storm each hard-run player has (by GamePlayer.ObjectId).</summary>
    private static readonly ConcurrentDictionary<string, Storm> Storms = new(StringComparer.Ordinal);

    /// <summary>The front: at the region's east edge, as wide as that (its back edge at X 0).</summary>
    public static (uint Position, uint Width) FrontFor(int regionMaxX)
    {
        uint edge = (uint) Math.Max(1, regionMaxX);

        return (edge, edge);
    }

    /// <summary>True when the back edge of a storm at <paramref name="currentPosition"/> nears the act zone's west edge.</summary>
    public static bool BackEdgeNear(uint currentPosition, uint width, int zoneMinX)
    {
        long back = (long) currentPosition - width;

        return back > 0 && back + RestartMarginUnits >= zoneMinX;
    }

    /// <summary>True when the region's weather is this storm (no random weather moves at speed 1).</summary>
    public static bool IsOurs(RegionWeather weather)
    {
        return weather != null && weather.StartTime != 0 && weather.Speed == StormSpeed && weather.Intensity == StormIntensity &&
            weather.FogDiffusion == StormFogDiffusion;
    }

    /// <summary>
    /// The tracker's tick (the player in the world and playing): the storm over the act's region on a hard run while the
    /// player is in that region; stopped otherwise. Never started for a normal run.
    /// </summary>
    public static void Tick(GamePlayer player, ExpeditionRun run, ActPlan plan)
    {
        if (player == null || string.IsNullOrEmpty(player.ObjectId))
        {
            return;
        }

        bool wanted = ExpeditionHardMode.IsHard(run) && plan != null && player.CurrentRegion != null && player.CurrentRegionID == plan.RegionId &&
            !player.CurrentRegion.IsInstance;

        if (!wanted)
        {
            Stop(player);

            return;
        }

        WeatherManager manager = GameServer.Instance?.WorldManager?.WeatherManager;
        ushort regionId = (ushort) plan.RegionId;
        RegionWeather weather = manager?[regionId];

        // A region without weather (a dungeon) has nothing to darken.
        if (weather == null)
        {
            return;
        }

        long now = GameLoop.GameLoopTime;
        Storm storm = Storms.GetOrAdd(player.ObjectId, _ => new Storm { RegionId = regionId });

        if (storm.RegionId != regionId)
        {
            Stop(player);
            storm = Storms.GetOrAdd(player.ObjectId, _ => new Storm { RegionId = regionId });
        }

        if (storm.InRegionSince == 0)
        {
            storm.InRegionSince = now;
        }

        // A weather packet sent while the client still loads a region can crash it.
        if (now - storm.InRegionSince < SettleMs)
        {
            return;
        }

        Zone zone = WorldMgr.GetZone((ushort) plan.ZoneId);
        int zoneMinX = zone?.XOffset ?? 0;
        bool ours = IsOurs(weather);

        if (ours && !(BackEdgeNear(weather.CurrentPosition(SimpleScheduler.Ticks), weather.Width, zoneMinX) && now - storm.StartedAt >= RestartMinMs))
        {
            storm.Started = true;

            return;
        }

        int regionMaxX = player.CurrentRegion.Zones.Count == 0 ? zoneMinX + (zone?.Width ?? 65536) : player.CurrentRegion.Zones.Max(z => z.XOffset + z.Width);
        (uint position, uint width) = FrontFor(regionMaxX);

        if (manager.StartWeather(regionId, position, width, StormSpeed, StormFogDiffusion, StormIntensity))
        {
            storm.Started = true;
            storm.StartedAt = now;
        }
        else if (Log.IsWarnEnabled)
        {
            Log.Warn($"[EXPEDITION] The hard run's storm could not be started in region {regionId} for {player.Name}.");
        }
    }

    /// <summary>
    /// Stops the player's storm (left the region, act change, run over, logout); the region's weather is left alone when
    /// it is no longer this storm, or another hard-run player in that region still has it.
    /// </summary>
    public static void Stop(GamePlayer player)
    {
        if (player == null || string.IsNullOrEmpty(player.ObjectId) || !Storms.TryRemove(player.ObjectId, out Storm storm) || !storm.Started)
        {
            return;
        }

        if (Storms.Values.Any(other => other.RegionId == storm.RegionId && other.Started))
        {
            return;
        }

        WeatherManager manager = GameServer.Instance?.WorldManager?.WeatherManager;

        if (manager != null && IsOurs(manager[storm.RegionId]))
        {
            manager.StopWeather(storm.RegionId);
        }
    }
}
