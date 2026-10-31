using System.Runtime.InteropServices;

namespace HaseeCtl.Core.Hw;

/// <summary>Minimal wevtapi surface used to follow the DCHU service's OutLog channel.</summary>
internal static class EventLogNative
{
    public const int EvtSubscribeToFutureEvents = 1;
    public const int EvtRenderEventXml = 1;

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr EvtSubscribe(IntPtr session, IntPtr signalEvent, string channelPath,
        string? query, IntPtr bookmark, IntPtr context, IntPtr callback, int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EvtNext(IntPtr resultSet, int eventArraySize, IntPtr[] eventArray,
        int timeout, int flags, out int returned);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EvtRender(IntPtr context, IntPtr fragment, int flags, int bufferSize,
        byte[] buffer, out int bufferUsed, out int propertyCount);

    [DllImport("wevtapi.dll", SetLastError = true)]
    public static extern bool EvtClose(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ResetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}
