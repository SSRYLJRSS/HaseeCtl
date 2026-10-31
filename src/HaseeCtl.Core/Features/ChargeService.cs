using HaseeCtl.Core.Hw;

namespace HaseeCtl.Core.Features;

/// <summary>FlexiCharger battery threshold control (CC40 PageFlexiCharger.cs).</summary>
public sealed record ChargeLimit(bool Enabled, int StartPercent, int StopPercent, int[] StartOptions, int[] StopOptions)
{
    public bool IsPreset7080 => Enabled && StartPercent == 70 && StopPercent == 80;
}

public sealed class ChargeService(Dchu dchu)
{
    public bool Supported { get; private set; } = true;

    public ChargeLimit? Read()
    {
        var o = dchu.Ex4(30);
        if (o.Length < 48) { Supported = false; return null; }
        var starts = new List<int>();
        var stops = new List<int>();
        for (int i = 16; i < 32; i++) if (o[i] != 0) starts.Add(o[i]);
        for (int i = 32; i < 48; i++) if (o[i] != 0) stops.Add(o[i]);
        if (starts.Count == 0 && stops.Count == 0) { Supported = false; return null; }
        return new ChargeLimit(o[0] != 0, o[1], o[2], [.. starts], [.. stops]);
    }

    /// <summary>status 0 disables the limit; 1 enables with the given window.</summary>
    public void Set(bool enabled, int start, int stop) =>
        dchu.Ex4(31, (byte)(enabled ? 1 : 0), (byte)start, (byte)stop);
}
