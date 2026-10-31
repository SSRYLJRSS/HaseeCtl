using HaseeCtl.Core.Features;

namespace HaseeCtl.Core.Config;

public sealed class CurveSettings
{
    public int T1 { get; set; } = 40;
    public int D1 { get; set; } = 25;
    public int T2 { get; set; } = 60;
    public int D2 { get; set; } = 40;
    public int T3 { get; set; } = 80;
    public int D3 { get; set; } = 80;

    public FanCurve ToCurve() => new FanCurve((byte)T1, (byte)D1, (byte)T2, (byte)D2, (byte)T3, (byte)D3).Normalize();

    public static CurveSettings From(FanCurve c) => new()
    {
        T1 = c.T1, D1 = c.D1, T2 = c.T2, D2 = c.D2, T3 = c.T3, D3 = c.D3,
    };
}

public sealed class FanProfile
{
    public string Name { get; set; } = "默认";
    public CurveSettings Cpu { get; set; } = new();
    public CurveSettings Gpu1 { get; set; } = new() { T3 = 85 };
    public CurveSettings? Gpu2 { get; set; }

    public FanTable ToTable() => new(Cpu.ToCurve(), Gpu1.ToCurve(), Gpu2?.ToCurve());
}

public sealed class FanSettings
{
    /// <summary>auto | max | silent | custom | slow | maxq</summary>
    public string Mode { get; set; } = "custom";
    public int OffsetPercent { get; set; }
    public List<FanProfile> Profiles { get; set; } = [];
    public string ActiveProfile { get; set; } = "默认";
    /// <summary>Optional per power mode profile name; empty means "keep the active profile".</summary>
    public Dictionary<string, string> PerPowerMode { get; set; } = [];

    public FanProfile? Find(string? name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal))
        ?? Profiles.FirstOrDefault();
}

public sealed class PerformanceSettings
{
    /// <summary>quiet | pwrsaving | performance | entertainment</summary>
    public string Mode { get; set; } = "performance";
    public bool TurboFan { get; set; }
}

public sealed class ChargeSettings
{
    public bool Enabled { get; set; }
    public int Start { get; set; } = 70;
    public int Stop { get; set; } = 80;
}

public sealed class GpuSettings
{
    public int Mode { get; set; } = 2;
}

public sealed class LightSettings
{
    public bool Enabled { get; set; } = true;
    public int Brightness { get; set; } = 4;
    public int Effect { get; set; } = 1;
    public int[] Left { get; set; } = [0, 0, 200];
    public int[] Mid { get; set; } = [0, 0, 200];
    public int[] Right { get; set; } = [0, 0, 200];
    public int PresetIndex { get; set; }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public FanSettings Fan { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public ChargeSettings Charge { get; set; } = new();
    public GpuSettings Gpu { get; set; } = new();
    public LightSettings Light { get; set; } = new();
    public bool ApplyOnStartup { get; set; } = true;
    public bool ApplyOnResume { get; set; } = true;
    public bool ShowOsd { get; set; } = true;

    public static AppSettings CreateDefault()
    {
        var s = new AppSettings();
        s.Fan.Profiles =
        [
            new FanProfile { Name = "安静", Cpu = new() { T1 = 40, D1 = 20, T2 = 55, D2 = 25, T3 = 70, D3 = 45 }, Gpu1 = new() { T1 = 40, D1 = 20, T2 = 60, D2 = 28, T3 = 75, D3 = 50 } },
            new FanProfile { Name = "均衡", Cpu = new() { T1 = 40, D1 = 25, T2 = 60, D2 = 40, T3 = 80, D3 = 80 }, Gpu1 = new() { T1 = 40, D1 = 25, T2 = 60, D2 = 40, T3 = 85, D3 = 80 } },
            new FanProfile { Name = "性能", Cpu = new() { T1 = 40, D1 = 40, T2 = 55, D2 = 60, T3 = 70, D3 = 90 }, Gpu1 = new() { T1 = 40, D1 = 40, T2 = 60, D2 = 65, T3 = 75, D3 = 95 } },
        ];
        s.Fan.ActiveProfile = "均衡";
        return s;
    }
}
