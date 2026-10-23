using System;
using System.Collections.Generic;
using DOL.Events;
using DOL.GS.Scheduler;
using DOL.GS.ServerProperties;

namespace DOL.GS.Expedition;

/// <summary>
/// The gloomy sky of an expedition: a storm that covers the whole region (rain, dark clouds, heavy fog), sent with the
/// weather packet (PacketLib168.SendWeather, 0x92) to the run player only. Nobody else in the region sees it and the
/// region's own weather (<see cref="WeatherManager"/>) is not changed.
/// <para>
/// A storm is a band on the region's X axis whose east edge is at <c>x</c> and which reaches <c>width</c> units west of
/// it, moving east at <c>speed</c> units a second (RegionWeather.CurrentPosition, the storm checks in
/// WeatherManager.StopWeather and AlluvianGlobuleBrain.CheckStorm). This one puts the east edge past the region's east
/// end and makes it wide enough to reach past the west end, so the whole region is inside it.
/// </para>
/// <para>
/// The region's weather can overwrite it on the client: WeatherManager sends the region's storm (or a clear sky) to
/// every player of the region when a storm starts or stops, and to a player entering a region (RegionEvent.PlayerEnter
/// from PlayerInitRequestHandler). So the storm is sent again on <see cref="GamePlayerEvent.RegionChanged"/> (fired
/// right after PlayerEnter), and the objective tracker's tick calls <see cref="Reassert"/>, which sends it again when
/// the region, the region's storm or the settings changed, or every <see cref="RefreshMs"/>.
/// </para>
/// </summary>
public static class ExpeditionWeather
{
    /// <summary>Even with nothing changed, the storm is sent again this often (covers a region storm sent between two checks).</summary>
    public const long RefreshMs = 60_000;

    /// <summary>Extra units past each end of the region, so its edges are inside the storm too.</summary>
    public const uint EdgeMargin = 8192;

    public const int MaxIntensity = 120;
    public const int MinFogDiffusion = 16000;

    private const string StampKey = "EXPEDITION_WEATHER_STAMP";

    private static readonly object HandlerSync = new();
    private static bool _handlerRegistered;

    /// <summary>The storm settings after clamping (see <see cref="ClampSettings"/>).</summary>
    public readonly record struct Settings(ushort Intensity, ushort FogDiffusion, ushort Speed);

    /// <summary>What was last sent to a player: the region, the region storm's start time at that moment, the settings and when.</summary>
    public sealed record Stamp(ushort RegionId, long RegionStormStart, Settings Sent, long SentAt);

    #region Run hooks

    /// <summary>Sends the expedition storm now (run start, act start, login with a run). Does nothing when it is off.</summary>
    public static void Apply(GamePlayer player)
    {
        EnsureHandler();
        Send(player, CurrentSettings());
    }

    /// <summary>
    /// Called by the objective tracker's tick: sends the storm again when the player is in another region, the region's
    /// storm started or stopped, the settings changed, or <see cref="RefreshMs"/> has passed.
    /// </summary>
    public static void Reassert(GamePlayer player)
    {
        Region region = player?.CurrentRegion;

        if (region == null || player.Client == null)
        {
            return;
        }

        Settings settings = CurrentSettings();
        Stamp last = player.TempProperties.GetProperty<Stamp>(StampKey);

        if (!NeedsResend(last, region.ID, RegionStormStart(region.ID), settings, GameLoop.GameLoopTime))
        {
            return;
        }

        Send(player, settings);
    }

    /// <summary>The run is over: the player gets the region's own weather back (its storm, or a clear sky).</summary>
    public static void Clear(GamePlayer player)
    {
        if (player == null)
        {
            return;
        }

        player.TempProperties.RemoveProperty(StampKey);

        if (player.Client == null || player.CurrentRegion == null)
        {
            return;
        }

        SendRegionWeather(player, player.CurrentRegionID);
    }

    #endregion

    #region Pure helpers (unit tested)

    /// <summary>Intensity 0..120 (0 = off; above 120 the client shows odd results, RegionWeather), fog diffusion 16000..65535, speed 0..65535.</summary>
    public static Settings ClampSettings(int intensity, int fogDiffusion, int speed)
    {
        return new Settings(
            (ushort)Math.Clamp(intensity, 0, MaxIntensity),
            (ushort)Math.Clamp(fogDiffusion, MinFogDiffusion, ushort.MaxValue),
            (ushort)Math.Clamp(speed, 0, ushort.MaxValue));
    }

    /// <summary>
    /// The region's extent on the X axis, as RegionWeather computes it: the smallest zone X offset to the largest
    /// X offset + width (game units; WorldMgr multiplies the zone data by 8192). False when there is no zone.
    /// </summary>
    public static bool TryRegionSpan(IEnumerable<(int XOffset, int Width)> zones, out uint west, out uint east)
    {
        long min = long.MaxValue;
        long max = long.MinValue;

        foreach ((int xOffset, int width) in zones)
        {
            min = Math.Min(min, xOffset);
            max = Math.Max(max, (long)xOffset + width);
        }

        if (min == long.MaxValue)
        {
            west = 0;
            east = 0;

            return false;
        }

        west = (uint)Math.Clamp(min, 0L, uint.MaxValue);
        east = (uint)Math.Clamp(max, 0L, uint.MaxValue);

        return east > west;
    }

    /// <summary>
    /// The storm band that covers [west, east]: its east edge <see cref="EdgeMargin"/> past the region's east end, wide
    /// enough to reach <see cref="EdgeMargin"/> past the west end. A moving storm (speed above 0) gets the distance it
    /// travels in two refresh periods on top, so its west edge has not crossed the region before it is sent again.
    /// </summary>
    public static (uint Position, uint Width) StormBand(uint west, uint east, ushort speed)
    {
        long position = (long)east + EdgeMargin;
        long width = (long)east - west + 2L * EdgeMargin + speed * (RefreshMs * 2 / 1000);

        return ((uint)Math.Min(position, uint.MaxValue), (uint)Math.Min(width, uint.MaxValue));
    }

    /// <summary>True when the storm must be sent (again): nothing sent yet, another region, the region storm changed, new settings, or the refresh is due.</summary>
    public static bool NeedsResend(Stamp last, ushort regionId, long regionStormStart, Settings settings, long now)
    {
        if (last == null)
        {
            return true;
        }

        if (last.RegionId != regionId || last.RegionStormStart != regionStormStart || last.Sent != settings)
        {
            return true;
        }

        return now - last.SentAt >= RefreshMs;
    }

    #endregion

    #region Sending

    private static Settings CurrentSettings()
    {
        return ClampSettings(Properties.EXPEDITION_WEATHER_INTENSITY, Properties.EXPEDITION_WEATHER_FOG, Properties.EXPEDITION_WEATHER_SPEED);
    }

    private static void Send(GamePlayer player, Settings settings)
    {
        Region region = player?.CurrentRegion;

        if (region == null || player.Client == null)
        {
            return;
        }

        long stormStart = RegionStormStart(region.ID);

        // Turned off (intensity 0) after a storm was sent: the region's own weather comes back once.
        if (settings.Intensity == 0)
        {
            if (player.TempProperties.GetProperty<Stamp>(StampKey)?.Sent.Intensity > 0)
            {
                SendRegionWeather(player, region.ID);
            }

            player.TempProperties.SetProperty(StampKey, new Stamp(region.ID, stormStart, settings, GameLoop.GameLoopTime));

            return;
        }

        var zones = new List<(int XOffset, int Width)>();

        foreach (Zone zone in region.Zones)
        {
            zones.Add((zone.XOffset, zone.Width));
        }

        // A dungeon region has no weather of its own (WeatherManager registers only regions that are not dungeons).
        if (region.IsDungeon || !TryRegionSpan(zones, out uint west, out uint east))
        {
            player.TempProperties.SetProperty(StampKey, new Stamp(region.ID, stormStart, settings, GameLoop.GameLoopTime));

            return;
        }

        (uint position, uint width) = StormBand(west, east, settings.Speed);
        player.Out.SendWeather(position, width, settings.Speed, settings.FogDiffusion, settings.Intensity);
        player.TempProperties.SetProperty(StampKey, new Stamp(region.ID, stormStart, settings, GameLoop.GameLoopTime));
    }

    /// <summary>As WeatherManager.SendWeatherUpdate: the region's storm where it is now, or all zeros for a clear sky.</summary>
    private static void SendRegionWeather(GamePlayer player, ushort regionId)
    {
        RegionWeather weather = GameServer.Instance?.WorldManager?.WeatherManager?[regionId];

        if (weather == null || weather.StartTime == 0)
        {
            player.Out.SendWeather(0, 0, 0, 0, 0);

            return;
        }

        player.Out.SendWeather(weather.CurrentPosition(SimpleScheduler.Ticks), weather.Width, weather.Speed, weather.FogDiffusion, weather.Intensity);
    }

    /// <summary>The region storm's start time (0 when there is none or the region has no weather).</summary>
    private static long RegionStormStart(ushort regionId)
    {
        return GameServer.Instance?.WorldManager?.WeatherManager?[regionId]?.StartTime ?? 0;
    }

    #endregion

    #region Region change

    private static void EnsureHandler()
    {
        lock (HandlerSync)
        {
            if (_handlerRegistered)
            {
                return;
            }

            GameEventMgr.AddHandler(GamePlayerEvent.RegionChanged, OnRegionChanged);
            _handlerRegistered = true;
        }
    }

    /// <summary>
    /// PlayerInitRequestHandler fires RegionChanged right after RegionEvent.PlayerEnter, where WeatherManager has just
    /// sent the new region's weather: the storm goes out again at once instead of on the next tracker tick.
    /// </summary>
    private static void OnRegionChanged(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is not GamePlayer player || player.TempProperties.GetProperty<Stamp>(StampKey) == null)
        {
            return;
        }

        if (ExpeditionManager.GetRun(player) == null)
        {
            player.TempProperties.RemoveProperty(StampKey);

            return;
        }

        Send(player, CurrentSettings());
    }

    #endregion
}
