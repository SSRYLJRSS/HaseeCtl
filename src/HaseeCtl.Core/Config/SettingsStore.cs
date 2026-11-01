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

    /// <summary>Identity of the settings file on disk (last write time + length) to detect external rewrites.</summary>
    public readonly record struct FileStamp(long Ticks, long Length);

    public static FileStamp Stamp()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            return fi.Exists ? new FileStamp(fi.LastWriteTimeUtc.Ticks, fi.Length) : default;
        }
        catch { return default; }
    }

    /// <summary>Result of <see cref="Load"/>. <see cref="Settings"/> is null only when the file exists but could
    /// not be read at the moment (locked / IO error); callers should then keep what they have.</summary>
    public sealed record LoadResult(AppSettings? Settings, FileStamp Stamp, string? Error);

    /// <summary>
    /// Load settings. Missing file → defaults (saved). Corrupt JSON → the bad file is kept as
    /// <c>settings.json.corrupt-yyyyMMdd-HHmmss.bak</c> and defaults are used, so user profiles can be recovered.
    /// Transient IO errors (file being replaced by the other process) are retried briefly and never overwrite the file.
    /// </summary>
    public static LoadResult Load()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    var fresh = AppSettings.CreateDefault();
                    return new LoadResult(fresh, Save(fresh) ?? default, null);
                }
                var stamp = Stamp();
                var json = File.ReadAllText(FilePath);
                AppSettings? loaded;
                try { loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings); }
                catch (JsonException ex) { return Recover("配置文件格式错误: " + ex.Message); }
                if (loaded is null) return Recover("配置文件内容为空");
                Normalize(loaded);
                return new LoadResult(loaded, stamp, null);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                return new LoadResult(null, default, ex.Message);
            }
        }

        static LoadResult Recover(string reason)
        {
            string? backup = null;
            try
            {
                backup = FilePath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
                File.Copy(FilePath, backup, overwrite: true);
            }
            catch (Exception ex) { Log.Error("备份损坏的配置文件失败", ex); backup = null; }
            Log.Write(reason + (backup is null ? "" : "；原文件已备份到 " + backup) + "；已使用默认设置");
            var defaults = AppSettings.CreateDefault();
            return new LoadResult(defaults, Save(defaults) ?? default, reason);
        }
    }

    /// <summary>Clamp every value into the range the hardware accepts. Idempotent.</summary>
    public static void Normalize(AppSettings s)
    {
        s.Fan ??= new FanSettings();
        s.Performance ??= new PerformanceSettings();
        s.Charge ??= new ChargeSettings();
        s.Gpu ??= new GpuSettings();
        s.Light ??= new LightSettings();
        s.Fan.Profiles ??= [];
        s.Fan.Profiles.RemoveAll(p => p is null);
        foreach (var p in s.Fan.Profiles)
        {
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? "未命名" : p.Name.Trim();
            p.Cpu = NormalizeCurve(p.Cpu);
            p.Gpu1 = NormalizeCurve(p.Gpu1);
            if (p.Gpu2 is not null) p.Gpu2 = NormalizeCurve(p.Gpu2);
        }
        if (s.Fan.Profiles.Count == 0) s.Fan.Profiles.Add(new FanProfile { Cpu = NormalizeCurve(new()), Gpu1 = NormalizeCurve(new() { T3 = 85 }) });
        if (s.Fan.Find(s.Fan.ActiveProfile) is null || s.Fan.Profiles.All(p => p.Name != s.Fan.ActiveProfile))
            s.Fan.ActiveProfile = s.Fan.Profiles[0].Name;
        if (Apply.Engine.ParseFanMode(s.Fan.Mode) is null) s.Fan.Mode = "custom";
        s.Fan.OffsetPercent = Math.Clamp(s.Fan.OffsetPercent, 0, 100);
        s.Fan.PerPowerMode ??= [];
        s.Performance.Mode = Apply.Engine.ParsePowerMode(s.Performance.Mode).ToString().ToLowerInvariant();
        s.Charge.Start = Math.Clamp(s.Charge.Start, 0, 99);
        s.Charge.Stop = Math.Clamp(s.Charge.Stop, 1, 100);
        if (s.Charge.Start >= s.Charge.Stop) s.Charge.Start = s.Charge.Stop - 1;
        s.Light.Brightness = Math.Clamp(s.Light.Brightness, Features.KeyboardLightService.MinBrightness, Features.KeyboardLightService.MaxBrightness);
        s.Light.Left = NormalizeColor(s.Light.Left);
        s.Light.Mid = NormalizeColor(s.Light.Mid);
        s.Light.Right = NormalizeColor(s.Light.Right);

        static CurveSettings NormalizeCurve(CurveSettings? c) => CurveSettings.From((c ?? new CurveSettings()).ToCurve());
        static int[] NormalizeColor(int[]? c) =>
            c is { Length: >= 3 } ? [Math.Clamp(c[0], 0, 255), Math.Clamp(c[1], 0, 255), Math.Clamp(c[2], 0, 255)] : [0, 0, 200];
    }

    /// <summary>最近一次保存失败的原因（成功时为 null），供界面与日志展示。</summary>
    public static Exception? LastError { get; private set; }

    private static readonly object SaveSync = new();

    public static bool TrySave(AppSettings settings) => TrySaveCore(settings) is not null;

    /// <summary>Save; returns the new file stamp, or null when saving failed (logged).</summary>
    public static FileStamp? Save(AppSettings settings)
    {
        var stamp = TrySaveCore(settings);
        if (stamp is null) Log.Write("保存设置失败: " + FilePath + " · " + LastError?.Message);
        return stamp;
    }

    private static FileStamp? TrySaveCore(AppSettings settings)
    {
        lock (SaveSync)
        {
            // tmp name is per process: App and Agent may save at the same moment
            var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
                    File.Move(tmp, FilePath, overwrite: true);
                    LastError = null;
                    return Stamp();
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(50); // the other process holds the file for a moment
                }
                catch (Exception ex)
                {
                    LastError = ex;
                    try { File.Delete(tmp); } catch { /* best effort */ }
                    return null;
                }
            }
        }
    }
}

public static class Log
{
    /// <summary>Log files older than this are deleted on the first write of a process.</summary>
    public const int KeepDays = 14;

    private static readonly object Sync = new();
    private static bool _pruned;

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                System.IO.Directory.CreateDirectory(SettingsStore.LogDirectory);
                if (!_pruned) { _pruned = true; Prune(); }
                var path = Path.Combine(SettingsStore.LogDirectory, $"haseectl-{DateTime.Now:yyyyMMdd}.log");
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{Environment.ProcessId}] {message}{Environment.NewLine}";
                // App and Agent append to the same file; share it so neither write fails
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }

    /// <summary>Message plus the full exception (type, message, stack, inner exceptions).</summary>
    public static void Error(string message, Exception ex) => Write(message + ": " + ex);

    private static void Prune()
    {
        try
        {
            var limit = DateTime.Now.AddDays(-KeepDays);
            foreach (var f in System.IO.Directory.EnumerateFiles(SettingsStore.LogDirectory, "haseectl-*.log"))
                if (File.GetLastWriteTime(f) < limit) File.Delete(f);
        }
        catch
        {
            // ignore
        }
    }
}
