namespace HaseeCtl.Core.Features;

/// <summary>
/// One fan's 4-point curve as the EC understands it. T in °C, D in percent (0..100).
/// Point 1 comes from the BIOS default table and point 4 is fixed at 100°C / 100 %.
/// Only points 2 and 3 plus the three slopes are sent to the EC (WMI 14).
/// </summary>
public sealed record FanCurve(byte T1, byte D1, byte T2, byte D2, byte T3, byte D3)
{
    public const byte T4 = 100;
    public const byte D4 = 100;

    /// <summary>Slope encoding used by FAN.Load_Rxx: round(ΔD/ΔT · 2.55 · 16).</summary>
    public static ushort Slope(int tA, int dA, int tB, int dB)
    {
        if (tB <= tA) return 0;
        double r = (double)(dB - dA) / (tB - tA) * 2.55 * 16.0;
        return (ushort)Math.Clamp(Math.Round(r, MidpointRounding.AwayFromZero), 0, ushort.MaxValue);
    }

    public ushort R12 => Slope(T1, D1, T2, D2);
    public ushort R23 => Slope(T2, D2, T3, D3);
    public ushort R34 => Slope(T3, D3, T4, D4);

    public static byte PercentToDuty(int percent) =>
        (byte)Math.Clamp(Math.Round(percent / 100.0 * 255.0, MidpointRounding.AwayFromZero), 0, 255);

    public static byte DutyToPercent(int duty) =>
        (byte)Math.Clamp(Math.Round(duty / 255.0 * 100.0, MidpointRounding.AwayFromZero), 0, 100);

    /// <summary>Returns a copy forced into a valid shape: T strictly increasing, D non-decreasing, ranges respected.</summary>
    public FanCurve Normalize()
    {
        int t1 = Math.Clamp((int)T1, 20, 97);
        int t2 = Math.Clamp((int)T2, t1 + 1, 98);
        int t3 = Math.Clamp((int)T3, t2 + 1, 99);
        int d1 = Math.Clamp((int)D1, 0, 100);
        int d2 = Math.Clamp((int)D2, d1, 100);
        int d3 = Math.Clamp((int)D3, d2, 100);
        return new FanCurve((byte)t1, (byte)d1, (byte)t2, (byte)d2, (byte)t3, (byte)d3);
    }

    /// <summary>
    /// Shape/range checks only. Deliberately no software minimum duty: the EC and the BIOS table already
    /// enforce whatever the hardware requires, and a user with extra cooling is free to run the fans slower.
    /// </summary>
    public string? Validate()
    {
        if (!(T1 < T2 && T2 < T3 && T3 < T4)) return "温度点必须依次递增且小于 100°C";
        if (!(D1 <= D2 && D2 <= D3 && D3 <= D4)) return "转速百分比必须依次不减";
        if (T1 < 20) return "起始温度过低";
        return null;
    }

    /// <summary>Duty (%) the curve yields at temperature t; linear interpolation between points.</summary>
    public double DutyAt(double t)
    {
        ReadOnlySpan<(double T, double D)> p = [(T1, D1), (T2, D2), (T3, D3), (T4, D4)];
        if (t <= p[0].T) return p[0].D;
        for (int i = 1; i < p.Length; i++)
            if (t <= p[i].T)
                return p[i - 1].D + (p[i].D - p[i - 1].D) * (t - p[i - 1].T) / (p[i].T - p[i - 1].T);
        return 100;
    }
}

/// <summary>Curves for every fan the EC exposes (CPU, GPU1, optional GPU2).</summary>
public sealed record FanTable(FanCurve Cpu, FanCurve Gpu1, FanCurve? Gpu2)
{
    /// <summary>Build the 256-byte WMI 14 package exactly like FAN.Write_WMI14 / SetCustomFanTable.</summary>
    public byte[] ToWmi14()
    {
        var b = new byte[256];
        var g2 = Gpu2 ?? Gpu1;
        Put(b, 2, Cpu); Put(b, 6, Gpu1); Put(b, 10, g2);
        Slopes(b, 14, Cpu); Slopes(b, 20, Gpu1); Slopes(b, 26, g2);
        return b;

        static void Put(byte[] b, int o, FanCurve c)
        {
            b[o] = c.T2; b[o + 1] = FanCurve.PercentToDuty(c.D2);
            b[o + 2] = c.T3; b[o + 3] = FanCurve.PercentToDuty(c.D3);
        }
        static void Slopes(byte[] b, int o, FanCurve c)
        {
            foreach (var r in new[] { c.R12, c.R23, c.R34 })
            {
                b[o] = (byte)(r >> 8); b[o + 1] = (byte)r; o += 2;
            }
        }
    }
}
