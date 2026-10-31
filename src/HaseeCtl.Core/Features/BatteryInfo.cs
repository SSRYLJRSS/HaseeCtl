using System.Runtime.InteropServices;

namespace HaseeCtl.Core.Features;

/// <summary>Battery level and AC state straight from the OS (no EC involvement).</summary>
public static class BatteryInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>Battery percentage, or -1 when unknown.</summary>
    public static int Percent()
    {
        if (!GetSystemPowerStatus(out var s)) return -1;
        return s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent;
    }

    /// <summary>True when the machine is on AC power.</summary>
    public static bool OnAc()
    {
        if (!GetSystemPowerStatus(out var s)) return true;
        return s.ACLineStatus == 1;
    }
}
