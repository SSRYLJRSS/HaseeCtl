using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace HaseeCtl.Core.Config;

/// <summary>项目主页与 GitHub Releases 更新检查。</summary>
public static class UpdateChecker
{
    public const string Owner = "SSRYLJRSS";
    public const string Repo = "HaseeCtl";
    public const string HomeUrl = "https://github.com/" + Owner + "/" + Repo;
    public const string ReleasesUrl = HomeUrl + "/releases";
    private const string ApiLatest = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public sealed record Result(bool HasUpdate, string? LatestTag, string PageUrl, string Message);

    public static async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("HaseeCtl/" + CurrentVersion.ToString(3));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.GetAsync(ApiLatest, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new(false, null, ReleasesUrl, "还没有发布任何版本。");
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            string url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesUrl : ReleasesUrl;
            if (!TryParse(tag, out var latest))
                return new(false, tag, url, $"最新发布 {tag}（无法比较版本号）。");
            bool newer = latest > Normalize(CurrentVersion);
            return new(newer, tag, url, newer ? $"发现新版本 {tag}" : $"已是最新版本（{tag}）。");
        }
        catch (Exception ex)
        {
            Log.Write("检查更新失败: " + ex.Message);
            return new(false, null, ReleasesUrl, "检查更新失败：" + ex.Message);
        }
    }

    private static bool TryParse(string tag, out Version v)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        int dash = s.IndexOfAny(['-', '+']);
        if (dash >= 0) s = s[..dash];
        if (Version.TryParse(s.Contains('.') ? s : s + ".0", out var p)) { v = Normalize(p); return true; }
        v = new Version(0, 0, 0);
        return false;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
}
