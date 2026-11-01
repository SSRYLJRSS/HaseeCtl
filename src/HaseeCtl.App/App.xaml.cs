using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using HaseeCtl.Core.Config;

namespace HaseeCtl.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // self-test mode: render every page to PNG and exit (no screen capture involved)
        var shotIndex = Array.FindIndex(e.Args, a => a == "--shot");
        if (shotIndex >= 0 && shotIndex + 1 < e.Args.Length)
        {
            RunShotMode(e.Args[shotIndex + 1]);
            return;
        }

        // only one control-centre window at a time; a second launch brings the first one to the front
        _singleInstance = new Mutex(true, @"Local\HaseeCtl.App.Singleton", out bool isFirst);
        if (!isFirst)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            ActivateExistingWindow();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write("App 未处理异常: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("App 后台任务异常", args.Exception); args.SetObserved(); };
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void ActivateExistingWindow()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(self.ProcessName))
            {
                using (p)
                {
                    if (p.Id == self.Id) continue;
                    var hwnd = p.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) continue;
                    if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                    return;
                }
            }
        }
        catch (Exception ex) { Log.Error("激活已打开的窗口失败", ex); }
    }

    private const int SW_RESTORE = 9;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    private void RunShotMode(string dir)
    {
        Log.Write("截图模式启动: " + dir);
        Directory.CreateDirectory(dir);
        var window = new MainWindow();
        MainWindow = window;

        int index = -1;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(900),
        };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (index >= 0 && index < window.PageCount)
                    Log.Write($"保存页面 {index} 结果={window.SaveVisual(Path.Combine(dir, $"{index}-{window.PageKey(index)}.png"))}");
                index++;
                if (index >= window.PageCount)
                {
                    timer.Stop();
                    Log.Write("截图模式完成");
                    Shutdown();
                    return;
                }
                window.SelectPage(index);
            }
            catch (Exception ex)
            {
                Log.Write("截图失败: " + ex);
                timer.Stop();
                Shutdown();
            }
        };
        // start the timer before Show(): Loaded fires inside Show and a handler attached
        // afterwards would never run
        timer.Start();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("App UI 异常", e.Exception);
        e.Handled = true;
        MessageBox.Show(e.Exception.Message, "HaseeCtl 出错", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
