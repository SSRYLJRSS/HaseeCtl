using System.Runtime.InteropServices;
using HaseeCtl.Core.Config;
using HaseeCtl.Core.Hw;
using Microsoft.Win32;

namespace HaseeCtl.Core.Features;

/// <summary>
/// 官方控制中心「偏好/系统」页的移植：触控板、Fn 锁 / Win 键锁 / Win-Fn 互换、飞行模式、
/// 电池低刷新率、关屏。所有寄存器与按键序列均来自反编译的 CC40 / FnKey 源码。
/// </summary>
public sealed class SystemService(Dchu dchu, Capabilities caps)
{
    private const string TouchpadKey = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status";
    private const string RadioKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState";

    // ---------- 触控板 ----------
    /// <summary>Windows 精确式触控板是否启用（null = 读不到）。</summary>
    public static bool? TouchpadEnabled()
    {
        try { return Registry.GetValue(TouchpadKey, "Enabled", null) is int v ? v != 0 : null; }
        catch { return null; }
    }

    /// <summary>与官方一致：仅在状态不同时发送 Ctrl+Win+F24（Windows 触控板切换快捷键）。</summary>
    public bool SetTouchpad(bool enable)
    {
        if (TouchpadEnabled() != enable)
        {
            SendCtrlWinF24();
            for (int i = 0; i < 15 && TouchpadEnabled() != enable; i++) Thread.Sleep(100);
        }
        bool now = TouchpadEnabled() ?? enable;
        SyncTouchpadToEc(now);
        return now;
    }

    /// <summary>把触控板状态写回 EC 存储页 1/10（官方 UpdateTouchPadStatus）。</summary>
    public void SyncTouchpadToEc(bool enabled) => Try(() => dchu.WriteAppByte(1, 10, (byte)(enabled ? 1 : 0)));

    /// <summary>官方 CheckI2C_TP：WMI96 bit4=1 的机型需让 EC 接管 I2C 触控板，Fn+F1 才能切换。</summary>
    public void EnableEcTouchpadControl()
    {
        if (!Capabilities.Has(caps.Wmi96, 4)) return;
        Try(() => SetH2Ram(219, 4, 4));
        Log.Write("已启用 EC 控制 I2C 触控板 (H2RAM 219)");
    }

    private void SetH2Ram(uint index, uint data, uint mask)
    {
        dchu.SetWmi(107, index);
        uint cur = dchu.GetWmiPackage(115)[0];
        uint next = data == 0 ? cur & ~mask : data | (cur & ~mask);
        dchu.SetWmi(117, next);
    }

    // ---------- 键盘设置 ----------
    public sealed record KeyboardOptions(bool FnLock, bool WinKeyLock, bool FnWinSwap);

    public KeyboardOptions ReadKeyboard()
    {
        var b = dchu.ReadApp(1, 36, 3);
        return new KeyboardOptions(b[0] == 1, b[1] == 2, b[2] == 1);
    }

    public void WriteKeyboard(KeyboardOptions o)
    {
        byte fn = (byte)(o.FnLock ? 1 : 2), win = (byte)(o.WinKeyLock ? 2 : 1), swap = (byte)(o.FnWinSwap ? 1 : 2);
        dchu.Ex4(25, fn, win, swap);
        dchu.WriteApp(1, 36, [fn, win, swap]);
        Log.Write($"键盘设置: Fn锁={o.FnLock} Win锁={o.WinKeyLock} 互换={o.FnWinSwap}");
    }

    // ---------- 飞行模式 ----------
    public static bool AirplaneOn()
    {
        try { return Registry.GetValue(RadioKey, "", 0) is int v && v != 0; }
        catch { return false; }
    }

    /// <summary>官方 Insyde_AirplaneMode_Control：EC 位 + Insyde HID 飞行模式开关。</summary>
    public bool ToggleAirplane()
    {
        bool target = !AirplaneOn();
        Try(() => dchu.SetWmi(121, 26, (uint)(target ? 1 : 0)));
        try { SetAirplaneMode(target ? 1 : 0); }
        catch (Exception ex) { Log.Write("InsydeAirplanemodehid 调用失败: " + ex.Message); }
        Log.Write("飞行模式 -> " + (target ? "开" : "关"));
        return target;
    }

    // ---------- 摄像头 ----------
    /// <summary>官方 GetCCD：EC 存储页 1/6，1=开 0=关。</summary>
    public bool CameraOn
    {
        get { try { return dchu.ReadAppByte(1, 6) != 0; } catch { return false; } }
    }

    /// <summary>官方 SetCCD：先写 App(1,6) 再 SetWMI(34,v)，EC 据此给摄像头上/断电。</summary>
    public void SetCamera(bool on)
    {
        Try(() => dchu.WriteAppByte(1, 6, (byte)(on ? 1 : 0)));
        Try(() => dchu.SetWmi(34, (uint)(on ? 1 : 0)));
        Log.Write("摄像头 -> " + (on ? "开" : "关"));
    }

    // ---------- 电池低刷新率 ----------
    /// <summary>与官方共用 EC 存储页 1/28 的勾选标志。</summary>
    public bool LowRefreshEnabled
    {
        get { try { return dchu.ReadAppByte(1, 28) == 1; } catch { return false; } }
        set => Try(() => dchu.WriteAppByte(1, 28, (byte)(value ? 1 : 0)));
    }

    /// <summary>电池供电且勾选 → 切到 ≥60Hz 的最低刷新率；否则恢复最高刷新率。</summary>
    public void ApplyRefreshRate()
    {
        bool onBattery = GetSystemPowerStatus(out var ps) && ps.ACLineStatus == 0;
        Display.SetRefresh(LowRefreshEnabled && onBattery ? Display.Pick.LowestAtLeast60 : Display.Pick.Highest);
    }

    // ---------- 关屏 ----------
    /// <summary>WM_SYSCOMMAND/SC_MONITORPOWER=2 关闭显示器（官方 MonitorControler 同法）。</summary>
    public static void ScreenOff() => PostMessage(new IntPtr(0xffff), 0x0112, new IntPtr(0xF170), new IntPtr(2));

    /// <summary>官方 WakePanelUp_Hook 的鼠标唤醒：SetWMI(94,1) 点亮 EC 关掉的背光。</summary>
    public void WakePanel() => Try(() => dchu.SetWmi(94, 1));

    private static void Try(Action a)
    {
        try { a(); } catch (Exception ex) { Log.Write("系统功能调用失败: " + ex.Message); }
    }

    /// <summary>Windows 内置的触控板开关快捷键 Ctrl+Win+F24（官方用 keybd_event 发同一组合）。</summary>
    private static void SendCtrlWinF24()
    {
        (ushort vk, ushort scan, bool ext)[] keys = [(0x11, 0x1D, false), (0x5B, 0x5B, true), (0x87, 0x76, false)];
        var inputs = new INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++)
        {
            uint ext = keys[i].ext ? 1u : 0u;
            inputs[i] = Key(keys[i].vk, keys[i].scan, ext);
            inputs[inputs.Length - 1 - i] = Key(keys[i].vk, keys[i].scan, ext | 2u);
        }
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length) Log.Write($"SendInput 只发送了 {sent}/{inputs.Length} (err {Marshal.GetLastWin32Error()})");
    }

    private static INPUT Key(ushort vk, ushort scan, uint flags) =>
        new() { type = 1, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } };

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    // INPUT 的联合体按最大成员 MOUSEINPUT 对齐（x64 下 32 字节），用 Explicit 布局补齐尺寸
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus s);
    [DllImport("InsydeAirplanemodehid.dll")] private static extern bool SetAirplaneMode(int status);
}

/// <summary>主显示器刷新率切换（EnumDisplaySettings / ChangeDisplaySettings）。</summary>
public static class Display
{
    public enum Pick { Highest, LowestAtLeast60 }

    public static int CurrentHz()
    {
        var dm = NewMode();
        return EnumDisplaySettings(null, -1, ref dm) ? dm.dmDisplayFrequency : 0;
    }

    public static void SetRefresh(Pick pick)
    {
        var cur = NewMode();
        if (!EnumDisplaySettings(null, -1, ref cur)) return;
        int best = pick == Pick.Highest ? 0 : int.MaxValue;
        var m = NewMode();
        for (int i = 0; EnumDisplaySettings(null, i, ref m); i++)
        {
            if (m.dmPelsWidth != cur.dmPelsWidth || m.dmPelsHeight != cur.dmPelsHeight || m.dmBitsPerPel != cur.dmBitsPerPel) continue;
            int f = m.dmDisplayFrequency;
            if (pick == Pick.Highest ? f > best : f >= 60 && f < best) best = f;
        }
        if (best == 0 || best == int.MaxValue || best == cur.dmDisplayFrequency) return;
        cur.dmDisplayFrequency = best;
        cur.dmFields = 0x400000; // DM_DISPLAYFREQUENCY
        int r = ChangeDisplaySettings(ref cur, 1); // CDS_UPDATEREGISTRY
        Log.Write($"刷新率 -> {best}Hz (结果 {r})");
    }

    private static DEVMODE NewMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string? dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
}
