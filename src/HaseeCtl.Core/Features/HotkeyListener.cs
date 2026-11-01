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

    /// <summary>Raised on the background thread "hasee-hotkeys"; handlers must marshal to their UI thread.</summary>
    public event Action<int>? HotkeyPressed;

    /// <summary>How often the active channel re-checks whether the other channel should be used.</summary>
    public static readonly TimeSpan ChannelCheckInterval = TimeSpan.FromSeconds(5);

    private Thread? _thread;

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Start listening. Calling it again while running does nothing.</summary>
    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Run(token)) { IsBackground = true, Name = "hasee-hotkeys" };
        _thread.Start();
    }

    private void Run(CancellationToken token)
    {
        int failures = 0;
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
                // both loops return when the channel choice changes, so we switch over automatically
                if (service) FollowEventLog(token);
                else WaitDriverEvent(token);
                failures = 0;
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;
                failures++;
                // log the first failure and then every 60th, so a dead channel is visible but cannot flood the log
                if (failures == 1 || failures % 60 == 0)
                    Config.Log.Error($"热键监听出错（第 {failures} 次），稍后重试", ex);
                _lastSource = null; // re-log the channel when it comes back
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(failures, 5)));
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
            var nextCheck = DateTime.UtcNow + ChannelCheckInterval;
            while (!token.IsCancellationRequested)
            {
                if (EventLogNative.WaitForSingleObject(signal, 500) == 0) DrainEvents(subscription);
                if (DateTime.UtcNow < nextCheck) continue;
                nextCheck = DateTime.UtcNow + ChannelCheckInterval;
                if (!UseServiceChannel) return; // CCDCHUService stopped: read the driver directly
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
        if (TryParseCode(xml, out int code)) Raise(code);
    }

    /// <summary>A failing handler must not tear down the subscription.</summary>
    private void Raise(int code)
    {
        try { HotkeyPressed?.Invoke(code); }
        catch (Exception ex) { Config.Log.Error($"处理热键 {code} 失败", ex); }
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
            var nextCheck = DateTime.UtcNow + ChannelCheckInterval;
            while (!token.IsCancellationRequested)
            {
                if (EventLogNative.WaitForSingleObject(hsa, 500) == 0)
                {
                    EventLogNative.ResetEvent(hsa); // manual-reset event: clear it before reading the code
                    int code = bridge.ReadNotifyValue(device);
                    if (code > 0) Raise(code);
                }
                if (DateTime.UtcNow < nextCheck) continue;
                nextCheck = DateTime.UtcNow + ChannelCheckInterval;
                if (UseServiceChannel) return; // vendor service took the channel back
            }
        }
        finally
        {
            EventLogNative.CloseHandle(hsa);
        }
    }

    public void Dispose()
    {
        // cancel only: the worker thread still observes the token while it winds down
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }
}
