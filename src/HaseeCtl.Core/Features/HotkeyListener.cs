using System.Text;
using HaseeCtl.Core.Hw;

namespace HaseeCtl.Core.Features;

/// <summary>
/// Follows the Arg001 hotkey stream. When the ACPI bridge service owns the wait (App page 1 offset 12 == 1)
/// it writes "Arg001^&lt;code&gt;" records into the OutLog channel; otherwise we drive the driver's
/// wait-event IOCTLs ourselves, exactly like FnKey's Arg001Event class.
/// </summary>
public sealed class HotkeyListener : IDisposable
{
    private readonly Dchu _dchu;
    private bool? _lastSource;
    private CancellationTokenSource? _cts;

    public HotkeyListener(Dchu dchu) => _dchu = dchu;

    public bool ServiceOwnsEvents
    {
        get
        {
            try { return _dchu.ReadAppByte(1, 12) == 1; }
            catch { return false; }
        }
    }

    /// <summary>Standalone check of the driver notify channel; used by the probe tool.</summary>
    public static int DriverProbe(int seconds, Action<string> log)
    {
        var bridge = new AcpiBridge();
        using var handle = bridge.OpenEventHandle();
        IntPtr hsa = EventLogNative.CreateEventW(IntPtr.Zero, true, false, null);
        if (hsa == IntPtr.Zero) throw new InvalidOperationException("CreateEvent 失败");
        bridge.RegisterWaitEvent(handle, hsa);
        log($"驱动热键通道已注册，等待 {seconds} 秒…（请按 Fn 组合键）");
        var stop = DateTime.UtcNow.AddSeconds(seconds);
        int n = 0;
        while (DateTime.UtcNow < stop)
        {
            if (EventLogNative.WaitForSingleObject(hsa, 1000) != 0) continue;
            EventLogNative.ResetEvent(hsa);
            try { log("键码 " + bridge.ReadNotifyValue(handle)); n++; }
            catch (Exception ex) { log("读取失败: " + ex.Message); }
        }
        return n;
    }

    /// <summary>
    /// The vendor service (CCDCHUService) writes "Arg001^code" records into the OutLog channel
    /// and takes the driver notifications for itself. Once that service is gone we have to read
    /// the notifications straight from the ACPI driver, so the choice has to be re-evaluated.
    /// </summary>
    public bool UseServiceChannel
    {
        get
        {
            if (Environment.GetEnvironmentVariable("HASEECTL_FORCE_DRIVER_HOTKEYS") == "1") return false;
            try { return ServiceOwnsEvents && ServiceCheck.IsRunning("CCDCHUService"); }
            catch { return false; }
        }
    }

    public event Action<int>? HotkeyPressed;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var thread = new Thread(() => Run(token)) { IsBackground = true, Name = "hasee-hotkeys" };
        thread.Start();
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                bool service = UseServiceChannel;
                if (service != _lastSource)
                {
                    _lastSource = service;
                    Config.Log.Write(service
                        ? "热键来源: 服务日志 OutLog (CCDCHUService)"
                        : "热键来源: ACPI 驱动通知 (驱动等待事件)");
                }
                if (service) FollowEventLog(token);
                else WaitDriverEvent(token);
            }
            catch (Exception)
            {
                if (token.IsCancellationRequested) return;
                Thread.Sleep(1000);
            }
        }
    }

    private void FollowEventLog(CancellationToken token)
    {
        IntPtr signal = EventLogNative.CreateEventW(IntPtr.Zero, false, false, null);
        if (signal == IntPtr.Zero) throw new InvalidOperationException("CreateEvent 失败");
        IntPtr subscription = EventLogNative.EvtSubscribe(IntPtr.Zero, signal, "OutLog", null,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, EventLogNative.EvtSubscribeToFutureEvents);
        if (subscription == IntPtr.Zero || subscription == new IntPtr(-1))
        {
            EventLogNative.CloseHandle(signal);
            throw new InvalidOperationException("无法订阅 OutLog 日志");
        }
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (EventLogNative.WaitForSingleObject(signal, 500) != 0) continue;
                DrainEvents(subscription);
            }
        }
        finally
        {
            EventLogNative.EvtClose(subscription);
            EventLogNative.CloseHandle(signal);
        }
    }

    private void DrainEvents(IntPtr subscription)
    {
        var handles = new IntPtr[8];
        while (EventLogNative.EvtNext(subscription, handles.Length, handles, 0, 0, out int returned) && returned > 0)
        {
            for (int i = 0; i < returned; i++)
            {
                try
                {
                    string? xml = Render(handles[i]);
                    if (xml is not null) Dispatch(xml);
                }
                finally
                {
                    EventLogNative.EvtClose(handles[i]);
                }
            }
        }
    }

    private static string? Render(IntPtr evt)
    {
        var buffer = new byte[4096];
        if (EventLogNative.EvtRender(IntPtr.Zero, evt, EventLogNative.EvtRenderEventXml, buffer.Length, buffer, out int used, out _))
            return Encoding.Unicode.GetString(buffer, 0, Math.Max(0, used - 2));

        int need = Math.Max(used, 8192);
        var bigger = new byte[need];
        if (!EventLogNative.EvtRender(IntPtr.Zero, evt, EventLogNative.EvtRenderEventXml, bigger.Length, bigger, out used, out _))
            return null;
        return Encoding.Unicode.GetString(bigger, 0, Math.Max(0, used - 2));
    }

    private void Dispatch(string xml)
    {
        if (TryParseCode(xml, out int code)) HotkeyPressed?.Invoke(code);
    }

    /// <summary>Extracts the key code from an OutLog event message such as "Arg001^149".</summary>
    public static bool TryParseCode(string xml, out int code)
    {
        code = 0;
        const string marker = "Arg001^";
        int at = xml.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return false;
        int start = at + marker.Length;
        int end = start;
        while (end < xml.Length && char.IsDigit(xml[end])) end++;
        if (end == start) return false;
        return int.TryParse(xml.AsSpan(start, end - start), out code);
    }

    /// <summary>Fallback: drive the driver's wait-event protocol ourselves (FnKey.Arg001Event).</summary>
    private void WaitDriverEvent(CancellationToken token)
    {
        var bridge = new AcpiBridge();
        using var device = bridge.OpenEventHandle();
        IntPtr hsa = EventLogNative.CreateEventW(IntPtr.Zero, true, false, null);
        if (hsa == IntPtr.Zero) throw new InvalidOperationException("CreateEvent 失败");
        try
        {
            bridge.RegisterWaitEvent(device, hsa);
            while (!token.IsCancellationRequested)
            {
                if (EventLogNative.WaitForSingleObject(hsa, 500) != 0) continue;
                EventLogNative.ResetEvent(hsa); // manual-reset event: clear it before reading the code
                int code = bridge.ReadNotifyValue(device);
                if (code > 0) HotkeyPressed?.Invoke(code);
            }
        }
        finally
        {
            EventLogNative.CloseHandle(hsa);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
