using HaseeCtl.Core.Hw;

namespace HaseeCtl.Core.Features;

/// <summary>
/// Fan control built from FnKey's FAN.cs / FanSpeedSetting's FAN.cs.
/// App store page 4 mirrors the vendor layout so the official tool and this one stay consistent.
/// </summary>
public sealed class FanService(Dchu dchu, Capabilities caps)
{
    public const int AppPage = 4;

    /// <summary>Last fan mode written to the App store (page 4 offset 5).</summary>
    public FanMode CurrentMode => (FanMode)dchu.ReadAppByte(AppPage, 5);

    public FanStatus ReadStatus()
    {
        var b = dchu.GetWmiPackage(12);
        return new FanStatus(
            CpuRpm: (b[2] << 8) | b[3],
            CpuDutyPercent: b[16] * 100 / 255,
            CpuTempC: b[18],
            GpuRpm: (b[4] << 8) | b[5],
            GpuDutyPercent: b[19] * 100 / 255,
            GpuTempC: b[21]);
    }

    public FanDefaults ReadDefaults()
    {
        var a = dchu.GetWmiPackage(13);
        var cpu = ReadCurve(a, 16);
        var gpu1 = ReadCurve(a, 24);
        var gpu2 = ReadCurve(a, 32);
        int count = a[12];
        bool custom = (a[43] & 0x02) == 0 && count > 1 && caps.CustomFanSupport;
        return new FanDefaults(count, a[14], cpu, gpu1, count > 2 ? gpu2 : null, custom);
    }

    private static FanCurve ReadCurve(byte[] a, int o) => new(
        T1: a[o], D1: FanCurve.DutyToPercent(a[o + 1]),
        T2: a[o + 2], D2: FanCurve.DutyToPercent(a[o + 3]),
        T3: a[o + 4], D3: FanCurve.DutyToPercent(a[o + 5]));

    /// <summary>Write the curve table (WMI 14) and switch the EC into custom mode.</summary>
    public void ApplyTable(FanTable table)
    {
        dchu.SetWmiPackage(14, table.ToWmi14());
        SetMode(FanMode.Custom);
        SaveAppInfo(table);
    }

    /// <summary>Apply a saved curve without switching modes (FnKey Load_FanMode_Info behaviour).</summary>
    public void PushTableOnly(FanTable table) => dchu.SetWmiPackage(14, table.ToWmi14());

    public void SetMode(FanMode mode)
    {
        dchu.SetWmi(121, 1, (uint)mode);
        dchu.WriteAppByte(AppPage, 5, (byte)mode);
        if (mode == FanMode.Slow) dchu.WriteAppByte(AppPage, 9, 1);
        else if (dchu.ReadAppByte(AppPage, 9) != 0) dchu.WriteAppByte(AppPage, 9, 0);
    }

    /// <summary>Global duty offset in percent (WMI 121 sub 14).</summary>
    public void SetOffset(int percent)
    {
        int v = Math.Clamp(percent, 0, 100);
        dchu.SetWmi(121, 14, (uint)Math.Round(v * 255.0 / 100.0, MidpointRounding.AwayFromZero));
        dchu.WriteAppByte(AppPage, 7, (byte)v);
    }

    public void SetTurboFan(bool on)
    {
        dchu.WriteAppByte(AppPage, 8, (byte)(on ? 1 : 0));
        if (!on) return;
        var mode = (FanMode)dchu.ReadAppByte(1, 1);
        if (mode == FanMode.Silent) return;
    }

    public void DisableTurboFan() => dchu.WriteAppByte(AppPage, 8, 0);

    public void RestoreDefaults()
    {
        dchu.SetWmi(121, 34, 1);
        dchu.WriteAppByte(AppPage, 5, 0);
    }

    public void StartAntiDust() => dchu.SetWmi(121, 41, 1);

    public void SetAntiDustSchedule(int hour, bool resume, int day)
    {
        uint v = (uint)(day & 0x7F) | ((resume ? 1u : 0u) << 7) | ((uint)(hour & 0x1F) << 8);
        dchu.SetWmi(121, 40, v);
    }

    /// <summary>Persist the curve in the vendor app store (page 4, vendor layout).</summary>
    public void SaveAppInfo(FanTable table)
    {
        var b = new byte[256];
        b[0] = 0x01; b[1] = 0x00;
        b[4] = dchu.ReadAppByte(AppPage, 4);
        b[5] = (byte)FanMode.Custom;
        b[6] = dchu.ReadAppByte(AppPage, 6);
        b[7] = dchu.ReadAppByte(AppPage, 7);
        PutCurve(b, 16, table.Cpu);
        PutCurve(b, 34, table.Gpu1);
        PutCurve(b, 52, table.Gpu2 ?? table.Gpu1);
        dchu.WriteApp(AppPage, 0, b);

        static void PutCurve(byte[] b, int o, FanCurve c)
        {
            b[o] = c.D1; b[o + 1] = c.D2; b[o + 2] = c.D3; b[o + 3] = 100;
            b[o + 4] = 40; b[o + 5] = 80;
            b[o + 6] = c.T1; b[o + 7] = c.T2; b[o + 8] = c.T3; b[o + 9] = 100;
            b[o + 10] = 60; b[o + 11] = 80;
            int p = o + 12;
            foreach (var r in new[] { c.R12, c.R23, c.R34 })
            {
                b[p] = (byte)(r & 0xFF); b[p + 1] = (byte)(r >> 8); p += 2;
            }
        }
    }
}
