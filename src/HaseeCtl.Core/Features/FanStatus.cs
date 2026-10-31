namespace HaseeCtl.Core.Features;

/// <summary>Live readings from WMI 12.</summary>
public sealed record FanStatus(
    int CpuRpm, int CpuDutyPercent, int CpuTempC,
    int GpuRpm, int GpuDutyPercent, int GpuTempC);

/// <summary>Fan mode values the EC understands (Interface_Fan.cs).</summary>
public enum FanMode
{
    Auto = 0,
    Max = 1,
    Silent = 2,
    MaxQ = 5,
    Custom = 6,
    Slow = 8,
}

/// <summary>Curve information the firmware reports for the installed thermal table.</summary>
public sealed record FanDefaults(
    int FanCount,
    int InitFanMode,
    FanCurve Cpu,
    FanCurve Gpu1,
    FanCurve? Gpu2,
    bool CustomSupported)
{
    public FanTable ToTable() => new(Cpu, Gpu1, Gpu2);
}
