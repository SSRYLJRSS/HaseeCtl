using HaseeCtl.Core.Apply;
using HaseeCtl.Core.Config;

namespace HaseeCtl.Agent;

internal static class Program
{
    private const string MutexName = @"Local\HaseeCtl.Agent.Singleton";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            Log.Write("已有实例在运行，退出");
            return;
        }

        Log.Write("HaseeCtl Agent 启动");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("未处理异常: " + e.ExceptionObject);
        // keep the tray alive when a UI callback throws; the error is logged with its stack
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI 线程异常", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("后台任务异常", e.Exception); e.SetObserved(); };

        var engine = new Engine();
        if (!engine.ReloadSettings()) Log.Write("警告: 配置暂时无法读取，先使用默认设置");
        if (!engine.Connect()) Log.Write("警告: 未能连接 AcpiBridge 驱动，稍后重试");

        Application.Run(new TrayApp(engine));
    }
}
