using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using DOL.GS.PacketHandler;
using DOL.GS.Scheduler;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// A hard run's dark sky (owner 2026-10-29: "make sure you get the weather right"; 2026-10-30: the first version showed
/// nothing). Two parts, while the run's player is in the act's region:
/// <para>
/// Rain: a storm of the server's own kind (WeatherManager, RegionWeather), kept inside the numbers the server's random
/// weather uses, which clients are known to draw (RegionWeather.CreateWeather: width 15,000-90,000, speed 100-700,
/// intensity 30-110, fog diffusion 16,000-32,000). The first version laid one front as wide as the whole region (about
/// 600,000 units) at speed 1, which the client did not show. The storm covers the X range (CurrentPosition - Width,
/// CurrentPosition) over the whole Y axis (the manager's own "sky clears" test), so it is laid
/// <see cref="StormWidth"/> wide with the player in the middle, and laid again around the player when the player is
/// within <see cref="EdgeMarginUnits"/> of either edge (the player walked, or the storm drifted east at
/// <see cref="StormSpeed"/> units a second).
/// </para>
/// <para>
/// Darkness: DAoC weather is rain and fog only; the sky's darkness is the time of day, which the server sends to each
/// client on its own (PacketLib168.SendTime). The player's client is told it is <see cref="NightHour"/>:00
/// (<see cref="DarkSky"/>), so only the hard-run player's sky darkens; a co-op partner on a normal run keeps theirs. The
/// day-night timer's resync every 15 minutes goes through the same SendTime, so the evening holds.
/// </para>
/// </summary>
public static class ExpeditionWeather
{
    /// <summary>The storm's width: the widest the server's random weather lays (RegionWeather.CreateWeather).</summary>
    public const uint StormWidth = 90000;

    /// <summary>The slowest the server's random weather moves (100 units a second): about 10 minutes across half the storm.</summary>
    public const ushort StormSpeed = 100;

    /// <summary>Rain and cloud: steady rain, below the top of the random weather's 30-110.</summary>
    public const ushort StormIntensity = 90;

    /// <summary>Fog diffusion: 16,000 is the densest fog of the random weather, 32,000 the thinnest; dark without a white-out.</summary>
    public const ushort StormFogDiffusion = 24000;

    /// <summary>The storm is laid again around the player when they are this close to either of its edges.</summary>
    public const int EdgeMarginUnits = 20000;

    /// <summary>The hour the hard-run player's client is told it is: evening, the sky dark but the ground still readable.</summary>
    public const int NightHour = 21;

    /// <summary>A game time in ms of the day (DayNightCycleTimer: a day is 24 x 60 x 60 x 1000).</summary>
    public const uint NightGameTime = NightHour * 60u * 60u * 1000u;

    /// <summary>Never laid again sooner than this after the last time.</summary>
    private const long RestartMinMs = 60 * 1000;

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

    /// <summary>The storm laid around a player at <paramref name="playerX"/>: its front half a width east of them.</summary>
    public static (uint Position, uint Width) FrontFor(int playerX)
    {
        return ((uint) Math.Max(0, playerX) + StormWidth / 2, StormWidth);
    }

    /// <summary>True when a player at <paramref name="playerX"/> is within <see cref="EdgeMarginUnits"/> of either edge, or outside.</summary>
    public static bool NearAnEdge(uint currentPosition, uint width, int playerX)
    {
        long front = currentPosition;
        long back = front - width;

        return playerX - back < EdgeMarginUnits || front - playerX < EdgeMarginUnits;
    }

    /// <summary>True when the region's weather is this storm.</summary>
    public static bool IsOurs(RegionWeather weather)
    {
        return weather != null && weather.StartTime != 0 && weather.Speed == StormSpeed && weather.Width == StormWidth &&
            weather.Intensity == StormIntensity && weather.FogDiffusion == StormFogDiffusion;
    }

    /// <summary>
    /// True while the player's client should be told it is evening (PacketLib168.SendTime): a hard-run player whose storm
    /// is up, in its region.
    /// </summary>
    public static bool DarkSky(GamePlayer player)
    {
        return player != null && !string.IsNullOrEmpty(player.ObjectId) && Storms.TryGetValue(player.ObjectId, out Storm storm) &&
            storm.Started && player.CurrentRegionID == storm.RegionId;
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

        // A region without weather (a dungeon) has nothing to rain on; the evening still holds there.
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

        // A weather or time packet sent while the client still loads a region can crash it.
        if (now - storm.InRegionSince < SettleMs)
        {
            return;
        }

        if (!storm.Started)
        {
            storm.Started = true;
            player.Out.SendTime();
            player.Out.SendMessage("Dark clouds gather over you. Night falls early on a hard expedition.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        }

        if (weather == null)
        {
            return;
        }

        bool ours = IsOurs(weather);

        if (ours && (!NearAnEdge(weather.CurrentPosition(SimpleScheduler.Ticks), weather.Width, player.X) || now - storm.StartedAt < RestartMinMs))
        {
            return;
        }

        (uint position, uint width) = FrontFor(player.X);

        if (manager.StartWeather(regionId, position, width, StormSpeed, StormFogDiffusion, StormIntensity))
        {
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

        // The evening ends with the storm: the client gets the world's time again.
        if (player.ObjectState == GameObject.eObjectState.Active && player.Client?.ClientState == GameClient.eClientState.Playing)
        {
            player.Out.SendTime();
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
