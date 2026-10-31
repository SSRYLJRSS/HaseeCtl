using Microsoft.Win32;

namespace HaseeCtl.Core.Features;

/// <summary>Port of FnKey's CheckDrivers.isInstall_DTT_Driver (uninstall entries containing "Dynamic Tuning").</summary>
public static class DttCheck
{
    private static bool? _cached;

    public static bool IsInstalled => _cached ??= Probe();

    private static bool Probe()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using var key = root.OpenSubKey(path);
                    if (key is null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(name);
                        if (sub?.GetValue("DisplayName") is string dn && dn.Contains("Dynamic Tuning", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch
                {
                    // registry access is best effort, same as the original
                }
            }
        return false;
    }
}
