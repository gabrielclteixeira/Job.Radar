using System.Text.Json;

namespace JobRadar;

/// <summary>Latest release info from GitHub: version (no leading v), the release page, and the Windows installer asset.</summary>
public sealed record UpdateInfo(string Latest, string HtmlUrl, string? WinInstallerUrl);

/// <summary>Checks GitHub Releases for a newer version and downloads the installer. Best-effort (returns null on failure).</summary>
public static class UpdateChecker
{
    private const string LatestApi = "https://api.github.com/repos/gabrielclteixeira/Job.Radar/releases/latest";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static UpdateChecker()
    {
        // GitHub's API rejects requests without a User-Agent.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("JobRadar-Updater");
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(LatestApi, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            string html = root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : "";
            string latest = tag.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(latest)) return null;

            string? win = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains("win", StringComparison.OrdinalIgnoreCase))
                        win = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                }
            return new UpdateInfo(latest, html, win);
        }
        catch { return null; }
    }

    /// <summary>True when <paramref name="latest"/> is a higher dotted version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string latest, string current) => ToVersion(latest).CompareTo(ToVersion(current)) > 0;

    private static Version ToVersion(string s)
    {
        var parts = (s ?? "").TrimStart('v', 'V').Split('.', '-', '+');
        int Get(int i) => parts.Length > i && int.TryParse(parts[i], out var v) ? v : 0;
        return new Version(Get(0), Get(1), Get(2));
    }

    /// <summary>Downloads a file (e.g. the installer) reporting a 0–1 progress fraction.</summary>
    public static async Task<bool> DownloadAsync(string url, string destPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return false;
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(destPath);
            var buf = new byte[81920];
            long read = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total is > 0) progress?.Report((double)read / total.Value);
            }
            return true;
        }
        catch { return false; }
    }
}
