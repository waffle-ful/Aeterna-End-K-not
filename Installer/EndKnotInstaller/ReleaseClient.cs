using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace EndKnotInstaller;

public class ReleaseInfo(string tag, string name, Dictionary<GamePlatform, string> assetUrls)
{
    public string Tag { get; } = tag;
    public string Name { get; } = name;
    public Dictionary<GamePlatform, string> AssetUrls { get; } = assetUrls;
}

public static class ReleaseClient
{
    public const string Repo = "waffle-ful/Aeterna-End-K-not";
    public const string ReleasesPageUrl = $"https://github.com/{Repo}/releases";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        // GitHub API は User-Agent 必須
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EndKnotInstaller");
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    /// <summary>
    /// 最新リリースを取得する。当リポジトリのリリースは全て Pre-release のため
    /// /releases/latest は使えない (prerelease 除外仕様で 404) — 一覧の先頭を採用する。
    /// </summary>
    public static async Task<ReleaseInfo> GetLatestReleaseAsync()
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=5");
        using var doc = JsonDocument.Parse(json);
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;

            Dictionary<GamePlatform, string> urls = [];
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var assetName = asset.GetProperty("name").GetString() ?? "";
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (assetName.EndsWith("_Steam.zip", StringComparison.OrdinalIgnoreCase)) urls[GamePlatform.Steam] = url;
                if (assetName.EndsWith("_Epic.zip", StringComparison.OrdinalIgnoreCase)) urls[GamePlatform.Epic] = url;
            }

            if (urls.Count < 2) continue; // zip が揃っていないリリースは飛ばす

            return new ReleaseInfo(
                release.GetProperty("tag_name").GetString() ?? "?",
                release.GetProperty("name").GetString() ?? "",
                urls);
        }

        throw new InvalidOperationException("配布用 zip を含むリリースが見つかりませんでした。");
    }

    public static async Task DownloadAsync(string url, string destPath, IProgress<double> progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = File.Create(destPath);
        var buffer = new byte[1 << 16];
        long readTotal = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            if (total > 0) progress.Report((double)readTotal / total);
        }
    }
}
