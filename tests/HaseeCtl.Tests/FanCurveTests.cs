using HaseeCtl.Core.Features;

namespace HaseeCtl.Tests;

public class FanCurveTests
{
    [Fact]
    public void Normalize_makes_temperatures_strictly_increasing_and_duties_non_decreasing()
    {
        var c = new FanCurve(50, 60, 40, 30, 40, 10).Normalize();
        Assert.True(c.T1 < c.T2 && c.T2 < c.T3 && c.T3 < FanCurve.T4);
        Assert.True(c.D1 <= c.D2 && c.D2 <= c.D3);
        Assert.Null(c.Validate());
    }

    [Theory]
    [InlineData(40, 20, 78, 24, 95, 24)]   // the flat "均衡" curve found in the user's settings
    [InlineData(40, 0, 60, 0, 80, 0)]      // everything at 0 %
    [InlineData(30, 10, 84, 10, 86, 10)]   // a low flat segment across 85 °C
    [InlineData(86, 0, 90, 0, 95, 0)]      // every point above 85 °C
    public void Normalize_never_adds_a_software_minimum_duty(int t1, int d1, int t2, int d2, int t3, int d3)
    {
        // The user has extra external cooling, so the app must not raise duties on its own;
        // only the EC/BIOS may enforce a hardware minimum.
        var c = new FanCurve((byte)t1, (byte)d1, (byte)t2, (byte)d2, (byte)t3, (byte)d3).Normalize();
        Assert.Equal(d1, (int)c.D1);
        Assert.Equal(d2, (int)c.D2);
        Assert.Equal(d3, (int)c.D3);
        Assert.Null(c.Validate());
    }

    [Fact]
    public void Normalize_leaves_a_safe_curve_untouched_and_is_idempotent()
    {
        var safe = new FanCurve(40, 25, 60, 40, 80, 80);
        Assert.Equal(safe, safe.Normalize());
        var once = new FanCurve(40, 20, 78, 24, 95, 24).Normalize();
        Assert.Equal(once, once.Normalize());
    }

    [Fact]
    public void Validate_accepts_a_low_duty_curve()
    {
        var c = new FanCurve(40, 20, 78, 24, 95, 24);
        Assert.Null(c.Validate());
    }

    [Fact]
    public void Wmi14_package_layout_matches_the_vendor_encoding()
    {
        var cpu = new FanCurve(40, 25, 60, 40, 80, 80);
        var gpu = new FanCurve(40, 25, 60, 40, 85, 80);
        var b = new FanTable(cpu, gpu, null).ToWmi14();
        Assert.Equal(256, b.Length);
        Assert.Equal([60, FanCurve.PercentToDuty(40), 80, FanCurve.PercentToDuty(80)], b[2..6]);
        Assert.Equal([60, FanCurve.PercentToDuty(40), 85, FanCurve.PercentToDuty(80)], b[6..10]);
        Assert.Equal(b[6..10], b[10..14]); // missing GPU2 mirrors GPU1
        Assert.Equal(cpu.R12, (b[14] << 8) | b[15]);
        Assert.Equal(cpu.R23, (b[16] << 8) | b[17]);
        Assert.Equal(cpu.R34, (b[18] << 8) | b[19]);
        // round(15 / 20 * 2.55 * 16) = 31
        Assert.Equal(31, cpu.R12);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 128)]
    [InlineData(100, 255)]
    public void Percent_duty_conversion(int percent, int duty)
    {
        Assert.Equal(duty, FanCurve.PercentToDuty(percent));
        Assert.Equal(percent, FanCurve.DutyToPercent(duty));
    }
}

public class HotkeyParsingTests
{
    [Theory]
    [InlineData("<Data>Arg001^149</Data>", 149)]
    [InlineData("xx Arg001^89 yy", 89)]
    [InlineData("<Event><Data>Arg001^252</Data></Event>", 252)]
    public void Parses_codes(string xml, int expected)
    {
        Assert.True(HaseeCtl.Core.Features.HotkeyListener.TryParseCode(xml, out int code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Arg001^")]
    [InlineData("Arg002^5")]
    [InlineData("Arg001^99999999999999")]
    public void Rejects_bad_messages(string xml) =>
        Assert.False(HaseeCtl.Core.Features.HotkeyListener.TryParseCode(xml, out _));
}
