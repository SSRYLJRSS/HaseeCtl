using HaseeCtl.Core.Hw;

namespace HaseeCtl.Core.Features;

public enum GpuMode
{
    Integrated = 1,
    Discrete = 2,
    MsHybrid = 3,
    Dynamic = 4,
}

public sealed record GpuState(GpuMode Current, IReadOnlyList<GpuMode> Supported);

/// <summary>Graphics adapter switching (CC40 UC_Box_GPUswitch.cs). Changing modes requires a reboot.</summary>
public sealed class GpuService(Dchu dchu)
{
    public GpuState Read()
    {
        var o = dchu.Ex4(21);
        int current = o.Length > 0 ? o[0] : 0;
        int bits = o.Length > 1 ? o[1] : 0;
        var supported = new List<GpuMode>();
        foreach (var m in new[] { GpuMode.Integrated, GpuMode.Discrete, GpuMode.MsHybrid, GpuMode.Dynamic })
            if ((bits & (1 << ((int)m - 1))) != 0) supported.Add(m);
        return new GpuState((GpuMode)current, supported);
    }

    public void Switch(GpuMode mode) => dchu.Ex4(22, (byte)mode);

    public static string DisplayName(GpuMode m) => m switch
    {
        GpuMode.Integrated => "集显模式",
        GpuMode.Discrete => "独显直连",
        GpuMode.MsHybrid => "混合模式",
        GpuMode.Dynamic => "动态切换",
        _ => m.ToString(),
    };
}
