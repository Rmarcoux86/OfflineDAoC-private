using System.Collections.Generic;
using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_ExpeditionWeather
{
    private static readonly ExpeditionWeather.Settings Default = ExpeditionWeather.ClampSettings(100, 16000, 0);

    [Test]
    public void TryRegionSpan_RunsFromSmallestOffsetToLargestEnd()
    {
        var zones = new List<(int XOffset, int Width)>
        {
            (8 * 8192, 8 * 8192),
            (0, 8 * 8192),
            (16 * 8192, 4 * 8192)
        };

        Assert.That(ExpeditionWeather.TryRegionSpan(zones, out uint west, out uint east), Is.True);
        Assert.That(west, Is.EqualTo(0u));
        Assert.That(east, Is.EqualTo(20u * 8192));
    }

    [Test]
    public void TryRegionSpan_NoZoneOrNegativeOnly_IsFalse()
    {
        Assert.That(ExpeditionWeather.TryRegionSpan(new List<(int, int)>(), out _, out _), Is.False);
        Assert.That(ExpeditionWeather.TryRegionSpan(new List<(int, int)> { (-50000, 10000) }, out _, out _), Is.False);
    }

    [Test]
    public void TryRegionSpan_NegativeOffset_ClampsWestToZero()
    {
        var zones = new List<(int XOffset, int Width)> { (-8192, 3 * 8192) };

        Assert.That(ExpeditionWeather.TryRegionSpan(zones, out uint west, out uint east), Is.True);
        Assert.That(west, Is.EqualTo(0u));
        Assert.That(east, Is.EqualTo(2u * 8192));
    }

    [Test]
    public void StormBand_StillStorm_CoversRegionWithMargins()
    {
        (uint position, uint width) = ExpeditionWeather.StormBand(100_000, 300_000, 0);

        // The band is (position - width, position): its east edge past the east end, its west edge past the west end.
        Assert.That(position, Is.EqualTo(300_000u + ExpeditionWeather.EdgeMargin));
        Assert.That((long)position - width, Is.EqualTo(100_000L - ExpeditionWeather.EdgeMargin));
    }

    [Test]
    public void StormBand_MovingStorm_StillCoversWestEndAfterTwoRefreshPeriods()
    {
        const ushort speed = 500;
        (uint position, uint width) = ExpeditionWeather.StormBand(100_000, 300_000, speed);
        long travelled = speed * (ExpeditionWeather.RefreshMs * 2 / 1000);

        Assert.That((long)position - width + travelled, Is.LessThanOrEqualTo(100_000L - ExpeditionWeather.EdgeMargin));
    }

    [Test]
    public void ClampSettings_KeepsValuesInTheClientRange()
    {
        Assert.That(ExpeditionWeather.ClampSettings(500, 100, -5), Is.EqualTo(new ExpeditionWeather.Settings(120, 16000, 0)));
        Assert.That(ExpeditionWeather.ClampSettings(-1, 70000, 70000), Is.EqualTo(new ExpeditionWeather.Settings(0, 65535, 65535)));
        Assert.That(ExpeditionWeather.ClampSettings(100, 20000, 150), Is.EqualTo(new ExpeditionWeather.Settings(100, 20000, 150)));
    }

    [Test]
    public void NeedsResend_NothingSentYet_IsTrue()
    {
        Assert.That(ExpeditionWeather.NeedsResend(null, 1, 0, Default, 1000), Is.True);
    }

    [Test]
    public void NeedsResend_SameRegionStormAndSettings_WaitsForTheRefresh()
    {
        var last = new ExpeditionWeather.Stamp(1, 0, Default, 10_000);

        Assert.That(ExpeditionWeather.NeedsResend(last, 1, 0, Default, 12_000), Is.False);
        Assert.That(ExpeditionWeather.NeedsResend(last, 1, 0, Default, 10_000 + ExpeditionWeather.RefreshMs - 1), Is.False);
        Assert.That(ExpeditionWeather.NeedsResend(last, 1, 0, Default, 10_000 + ExpeditionWeather.RefreshMs), Is.True);
    }

    [Test]
    public void NeedsResend_RegionChanged_IsTrue()
    {
        var last = new ExpeditionWeather.Stamp(1, 0, Default, 10_000);

        Assert.That(ExpeditionWeather.NeedsResend(last, 100, 0, Default, 10_001), Is.True);
    }

    [Test]
    public void NeedsResend_RegionStormStartedOrStopped_IsTrue()
    {
        var clear = new ExpeditionWeather.Stamp(1, 0, Default, 10_000);
        var stormy = new ExpeditionWeather.Stamp(1, 5000, Default, 10_000);

        Assert.That(ExpeditionWeather.NeedsResend(clear, 1, 9000, Default, 10_001), Is.True);
        Assert.That(ExpeditionWeather.NeedsResend(stormy, 1, 0, Default, 10_001), Is.True);
    }

    [Test]
    public void NeedsResend_SettingsChanged_IsTrue()
    {
        var last = new ExpeditionWeather.Stamp(1, 0, Default, 10_000);
        ExpeditionWeather.Settings lighter = ExpeditionWeather.ClampSettings(60, 16000, 0);

        Assert.That(ExpeditionWeather.NeedsResend(last, 1, 0, lighter, 10_001), Is.True);
    }
}
