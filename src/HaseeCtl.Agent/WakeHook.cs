using System.Diagnostics;
using System.Runtime.InteropServices;
using HaseeCtl.Core.Config;

namespace HaseeCtl.Agent;

/// <summary>
/// 复刻官方 WakePanelUp_Hook.exe：Fn+F2 让 EC 关掉背光后，装一次性低级键鼠钩子。
/// 鼠标一动就调用 wake（SetWMI(94,1) 点亮背光）；按键由 EC 自己点亮，只需卸载钩子。
/// 必须在有消息循环的 UI 线程上调用 Arm()。
/// </summary>
internal sealed class WakeHook : IDisposable
{
    private const int WhKeyboardLl = 13, WhMouseLl = 14;
    private readonly Action _wake;
    private readonly HookProc _mouseProc, _keyProc;
    private IntPtr _mouse, _key;
    private long _armedAt;
    private bool _fired;

    public WakeHook(Action wake)
    {
        _wake = wake;
        _mouseProc = OnMouse;
        _keyProc = OnKey;
    }

    public bool Armed => _mouse != IntPtr.Zero || _key != IntPtr.Zero;

    public void Arm()
    {
        Disarm();
        _armedAt = Environment.TickCount64;
        _fired = false;
        IntPtr mod = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
        _mouse = SetWindowsHookEx(WhMouseLl, _mouseProc, mod, 0);
        _key = SetWindowsHookEx(WhKeyboardLl, _keyProc, mod, 0);
        Log.Write($"关屏唤醒钩子已安装 (mouse={_mouse != IntPtr.Zero} key={_key != IntPtr.Zero})");
    }

    // 忽略按下 Fn+F2 本身的抬键与手抖：前 400ms 的活动不算
    private bool Settled => Environment.TickCount64 - _armedAt > 400;

    private IntPtr OnMouse(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0 && Settled && !_fired)
        {
            _fired = true;
            Log.Write("关屏后检测到鼠标活动，点亮屏幕");
            try { _wake(); } catch (Exception ex) { Log.Write("点亮屏幕失败: " + ex.Message); }
            BeginDisarm();
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    private IntPtr OnKey(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0 && Settled && !_fired)
        {
            _fired = true;
            Log.Write("关屏后检测到按键（EC 自行点亮）");
            BeginDisarm();
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    // 不能在钩子回调里直接卸载自身，延后到消息循环
    private void BeginDisarm() => SynchronizationContext.Current?.Post(_ => Disarm(), null);

    public void Disarm()
    {
        if (_mouse != IntPtr.Zero) { UnhookWindowsHookEx(_mouse); _mouse = IntPtr.Zero; }
        if (_key != IntPtr.Zero) { UnhookWindowsHookEx(_key); _key = IntPtr.Zero; }
    }

    public void Dispose() => Disarm();

    private delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
