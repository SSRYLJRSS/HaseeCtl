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
        Application.ThreadException += (_, e) => Log.Write("UI 线程异常: " + e.Exception);

        var engine = new Engine();
        engine.ReloadSettings();
        if (!engine.Connect()) Log.Write("警告: 未能连接 AcpiBridge 驱动，稍后重试");

        Application.Run(new TrayApp(engine));
    }
}
