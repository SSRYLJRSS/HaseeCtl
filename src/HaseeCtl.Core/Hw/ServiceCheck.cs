using System.Runtime.InteropServices;

namespace HaseeCtl.Core.Hw;

/// <summary>
/// Tells whether a Windows service is currently running. Used to decide who publishes the
/// Fn hotkey events: while the vendor "Control Center Hotkey Service" is alive it owns the
/// events, and once it is uninstalled / stopped we must read them straight from the driver.
/// </summary>
public static class ServiceCheck
{
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_RUNNING = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public static bool IsRunning(string serviceName)
    {
        IntPtr manager = IntPtr.Zero, service = IntPtr.Zero;
        try
        {
            manager = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (manager == IntPtr.Zero) return false;
            service = OpenServiceW(manager, serviceName, SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero) return false;
            var status = default(SERVICE_STATUS);
            if (!QueryServiceStatus(service, ref status)) return false;
            return status.dwCurrentState == SERVICE_RUNNING;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (manager != IntPtr.Zero) CloseServiceHandle(manager);
        }
    }
}
