using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaseeCtl.Core.Config;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
public partial class SettingsJsonContext : JsonSerializerContext;

public static class SettingsStore
{
    private static string AppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>目录可写性探测：确保目录存在并且真的能写文件。</summary>
    private static string? PickWritable(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            try
            {
                System.IO.Directory.CreateDirectory(c);
                var probe = Path.Combine(c, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return c;
            }
            catch
            {
                // try the next candidate
            }
        }
        return null;
    }

    private static readonly string? ChosenDirectory = PickWritable(
        Environment.GetEnvironmentVariable("HASEECTL_DATA_DIR") ?? "",
        Path.Combine(AppData, "HaseeCtl"),
        Path.Combine(LocalAppData, "HaseeCtl"),
        Path.Combine(Path.GetTempPath(), "HaseeCtl"),
        Path.Combine(AppContext.BaseDirectory, "data"));

    public static string Directory { get; } =
        ChosenDirectory ?? Path.Combine(AppData, "HaseeCtl");

    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    private static readonly string? ChosenLogDirectory = PickWritable(
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HASEECTL_DATA_DIR")) ? "" : Path.Combine(Environment.GetEnvironmentVariable("HASEECTL_DATA_DIR")!, "logs"),
        Path.Combine(LocalAppData, "HaseeCtl", "logs"),
        Path.Combine(Directory, "logs"),
        Path.Combine(Path.GetTempPath(), "HaseeCtl", "logs"));

    public static string LogDirectory { get; } =
        ChosenLogDirectory ?? Path.Combine(LocalAppData, "HaseeCtl", "logs");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
                if (loaded is not null)
                {
                    Normalize(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // fall through to defaults when the file is unreadable or corrupt
        }
        var defaults = AppSettings.CreateDefault();
        TrySave(defaults);
        return defaults;
    }

    private static void Normalize(AppSettings s)
    {
        if (s.Fan.Profiles.Count == 0) s.Fan.Profiles.Add(new FanProfile());
        if (s.Fan.Find(s.Fan.ActiveProfile) is { } first && string.IsNullOrEmpty(s.Fan.ActiveProfile))
            s.Fan.ActiveProfile = first.Name;
        if (s.Fan.Find(s.Fan.ActiveProfile) is null) s.Fan.ActiveProfile = s.Fan.Profiles[0].Name;
        s.Light.Brightness = Math.Clamp(s.Light.Brightness, 1, 4);
        s.Fan.OffsetPercent = Math.Clamp(s.Fan.OffsetPercent, 0, 100);
        s.Charge.Start = Math.Clamp(s.Charge.Start, 0, 100);
        s.Charge.Stop = Math.Clamp(s.Charge.Stop, 0, 100);
    }

    /// <summary>最近一次保存失败的原因（成功时为 null），供界面与日志展示。</summary>
    public static Exception? LastError { get; private set; }

    public static bool TrySave(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(FilePath.Substring(0, FilePath.LastIndexOf(Path.DirectorySeparatorChar)));
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
            File.Move(tmp, FilePath, overwrite: true);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex;
            return false;
        }
    }

    public static void Save(AppSettings settings)
    {
        if (!TrySave(settings)) Log.Write("保存设置失败: " + FilePath);
    }
}

public static class Log
{
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                System.IO.Directory.CreateDirectory(SettingsStore.LogDirectory);
                var path = Path.Combine(SettingsStore.LogDirectory, $"haseectl-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
