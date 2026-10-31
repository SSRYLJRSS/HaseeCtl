namespace HaseeCtl.Core.Features;

/// <summary>Clevo ModeType (Clevo/CleDef.cs:257).</summary>
public enum PowerModeKind
{
    Quiet = 0,
    PowerSaving = 1,
    Performance = 2,
    Entertainment = 3,
}

public static class PowerModes
{
    /// <summary>Cycle order used when the firmware exposes no explicit support bits (Form1.cs default 0→2→3→1).</summary>
    private static readonly PowerModeKind[] CycleOrder =
    [
        PowerModeKind.Quiet, PowerModeKind.Performance, PowerModeKind.Entertainment, PowerModeKind.PowerSaving,
    ];

    public static IReadOnlyList<PowerModeKind> Supported(Capabilities caps)
    {
        var list = new List<PowerModeKind>();
        foreach (var m in CycleOrder)
        {
            bool ok = m switch
            {
                PowerModeKind.Quiet => caps.QuietMode,
                PowerModeKind.PowerSaving => caps.PowerSavingMode,
                PowerModeKind.Performance => caps.PerformanceMode,
                PowerModeKind.Entertainment => caps.EntertainmentMode,
                _ => false,
            };
            if (ok) list.Add(m);
        }
        // No support bits set means the firmware exposes the full fixed cycle (Form1.cs NextMode fallback).
        return list.Count > 0 ? list : CycleOrder.ToList();
    }

    public static PowerModeKind Next(PowerModeKind current, Capabilities caps)
    {
        var list = Supported(caps);
        int i = list.ToList().IndexOf(current);
        return i < 0 || i == list.Count - 1 ? list[0] : list[i + 1];
    }

    public static string DisplayName(PowerModeKind m) => m switch
    {
        PowerModeKind.Quiet => "静音",
        PowerModeKind.PowerSaving => "省电",
        PowerModeKind.Performance => "性能",
        PowerModeKind.Entertainment => "娱乐",
        _ => m.ToString(),
    };
}
