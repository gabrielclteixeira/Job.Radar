using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace JobRadar;

/// <summary>Settings of the browser-driven LinkedIn source (F1). Machine-local: linkedin-browser-settings.json.</summary>
public class LinkedInBrowserConfig
{
    public bool Enabled { get; set; }
    /// <summary>Max jobs read per search title (the public endpoint serves 10 per request).</summary>
    public int MaxPerQuery { get; set; } = 50;
    /// <summary>How many of the profile's job titles are searched (1–4).</summary>
    public int Queries { get; set; } = 2;
    /// <summary>"" any time · "r86400" 24 h · "r604800" week · "r2592000" month (LinkedIn's f_TPR values).</summary>
    public string Posted { get; set; } = "r604800";
    /// <summary>Base pause between requests, in seconds (a random ±40 % jitter is added).</summary>
    public int PaceSeconds { get; set; } = 4;
    /// <summary>Open each posting to read its full description (slower, much better scoring).</summary>
    public bool ReadDescriptions { get; set; } = true;
    /// <summary>Show the browser window instead of running headless.</summary>
    public bool ShowBrowser { get; set; }
}

/// <summary>
/// F1 — LinkedIn jobs through a real browser driven by Playwright.
/// <list type="bullet">
/// <item>Only LinkedIn's PUBLIC job pages are read (the same ones anyone sees logged out): no login, the user's
/// account is never involved, so it can't be restricted. It is still automated access, which LinkedIn's terms
/// don't allow — the source is opt-in, capped and paced like a person, and the settings say so.</item>
/// <item>Playwright's driver (Node + playwright-core, ~40 MB) is NOT shipped in the installer: it's installed on
/// demand from Settings, from the official sources (nodejs.org, registry.npmjs.org), with their checksums verified,
/// into the app data folder. The browser is the Edge/Chrome/Chromium already installed; only if there is none is
/// Playwright's own Chromium downloaded.</item>
/// </list>
/// </summary>
public static class LinkedInBrowser
{
    /// <summary>Node.js version bundled by Microsoft.Playwright <see cref="PlaywrightVersion"/> — bump both together
    /// (a unit test checks the package version).</summary>
    public const string NodeVersion = "v24.18.1";
    public const string PlaywrightVersion = "1.62.0";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Folder that holds ".playwright" (what PLAYWRIGHT_DRIVER_SEARCH_PATH points at). Settable so tests and
    /// tools can use a scratch folder instead of the user's data dir.</summary>
    public static string DriverRoot { get; set; } =
        Environment.GetEnvironmentVariable("JOBRADAR_PLAYWRIGHT_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppPaths.DataDir, "playwright");
    private static string DotPlaywright => Path.Combine(DriverRoot, ".playwright");

    private static string NodePlatform =>
        OperatingSystem.IsWindows() ? "win32_x64"
        : OperatingSystem.IsMacOS() ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "darwin-arm64" : "darwin-x64")
        : (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64");

    private static string NodeExePath => Path.Combine(DotPlaywright, "node", NodePlatform, OperatingSystem.IsWindows() ? "node.exe" : "node");

    /// <summary>True when the on-demand driver is in place.</summary>
    public static bool IsInstalled => File.Exists(NodeExePath) && File.Exists(Path.Combine(DotPlaywright, "package", "cli.js"));

    /// <summary>The installed Chromium browser to drive (Edge, Chrome or Chromium), or null.</summary>
    public static string? SystemBrowser => Reports.FindEdge();

    /// <summary>
    /// Downloads and verifies the Playwright driver: the Node.js runtime (SHA-256 from nodejs.org's SHASUMS256.txt)
    /// and the playwright-core package (sha512 integrity from the npm registry), laid out as Microsoft.Playwright
    /// expects. Reports progress text. Throws on failure (the caller shows the message).
    /// </summary>
    public static async Task InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "JobRadar-pw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // --- playwright-core (npm) ---
            progress?.Report(Loc.Instance.T("lib.install.package"));
            string metaJson = await Http.GetStringAsync($"https://registry.npmjs.org/playwright-core/{PlaywrightVersion}", ct);
            string integrity;
            using (var meta = JsonDocument.Parse(metaJson))
                integrity = meta.RootElement.GetProperty("dist").GetProperty("integrity").GetString() ?? "";
            string tgz = Path.Combine(tmp, "playwright-core.tgz");
            await DownloadAsync($"https://registry.npmjs.org/playwright-core/-/playwright-core-{PlaywrightVersion}.tgz", tgz, progress, ct);
            if (!integrity.StartsWith("sha512-") ||
                Convert.ToBase64String(SHA512.HashData(await File.ReadAllBytesAsync(tgz, ct))) != integrity["sha512-".Length..])
                throw new InvalidOperationException("playwright-core: checksum mismatch");

            // --- Node.js (nodejs.org) ---
            string nodeName = OperatingSystem.IsWindows() ? $"node-{NodeVersion}-win-x64.zip"
                : OperatingSystem.IsMacOS() ? $"node-{NodeVersion}-darwin-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")}.tar.gz"
                : $"node-{NodeVersion}-linux-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")}.tar.gz";
            string sums = await Http.GetStringAsync($"https://nodejs.org/dist/{NodeVersion}/SHASUMS256.txt", ct);
            string? expected = sums.Split('\n').Select(l => l.Trim().Split("  ")).FirstOrDefault(p => p.Length == 2 && p[1] == nodeName)?[0];
            if (expected is null) throw new InvalidOperationException("node: no checksum for " + nodeName);
            string nodeArchive = Path.Combine(tmp, nodeName);
            progress?.Report(Loc.Instance.T("lib.install.node"));
            await DownloadAsync($"https://nodejs.org/dist/{NodeVersion}/{nodeName}", nodeArchive, progress, ct);
            using (var fs = File.OpenRead(nodeArchive))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("node: checksum mismatch");

            // --- lay out .playwright/{package, node/<platform>/node} ---
            progress?.Report(Loc.Instance.T("lib.install.extract"));
            if (Directory.Exists(DotPlaywright)) Directory.Delete(DotPlaywright, true);
            string pkgDir = Path.Combine(DotPlaywright, "package");
            await ExtractTarGzAsync(tgz, "package/", pkgDir, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(NodeExePath)!);
            if (nodeName.EndsWith(".zip"))
            {
                using var zip = ZipFile.OpenRead(nodeArchive);
                var entry = zip.Entries.First(e => e.FullName.EndsWith("/node.exe", StringComparison.OrdinalIgnoreCase));
                entry.ExtractToFile(NodeExePath, true);
            }
            else
            {
                await ExtractTarGzAsync(nodeArchive, "/bin/node", NodeExePath, ct, singleFile: true);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(NodeExePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            if (!IsInstalled) throw new InvalidOperationException("install incomplete");
            Diag.Info($"linkedin-browser: driver installed (playwright-core {PlaywrightVersion}, node {NodeVersion})");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>Downloads Playwright's own Chromium (only needed when no Edge/Chrome/Chromium is installed).</summary>
    public static Task<int> InstallChromiumAsync()
    {
        Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", DriverRoot);
        return Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
    }

    /// <summary>Removes the on-demand driver (frees the ~100 MB it takes once extracted).</summary>
    public static void Uninstall()
    {
        try { if (Directory.Exists(DotPlaywright)) Directory.Delete(DotPlaywright, true); } catch { }
    }

    private static async Task DownloadAsync(string url, string path, IProgress<string>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buf = new byte[81920];
        int n, lastPct = -1;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            if (total > 0 && (int)(done * 100 / total) is var pct && pct != lastPct && pct % 5 == 0)
            {
                lastPct = pct;
                progress?.Report(Loc.Instance.F("lib.install.progress", Path.GetFileName(path), pct, total / 1_048_576));
            }
        }
    }

    /// <summary>Extracts a .tgz/.tar.gz: every entry under <paramref name="prefix"/> into <paramref name="dest"/>, or
    /// (singleFile) the first entry whose name ends with <paramref name="prefix"/> to the file <paramref name="dest"/>.</summary>
    private static async Task ExtractTarGzAsync(string archive, string prefix, string dest, CancellationToken ct, bool singleFile = false)
    {
        await using var fs = File.OpenRead(archive);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        TarEntry? e;
        while ((e = await tar.GetNextEntryAsync(copyData: false, ct)) is not null)
        {
            if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || e.DataStream is null) continue;
            string name = e.Name.Replace('\\', '/');
            if (singleFile)
            {
                if (!name.EndsWith(prefix, StringComparison.Ordinal)) continue;
                await using var outFile = File.Create(dest);
                await e.DataStream.CopyToAsync(outFile, ct);
                return;
            }
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rel = name[prefix.Length..];
            string target = Path.GetFullPath(Path.Combine(dest, rel));
            if (!target.StartsWith(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) continue;   // zip-slip guard
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var o = File.Create(target);
            await e.DataStream.CopyToAsync(o, ct);
        }
        if (singleFile) throw new InvalidOperationException("not found in archive: " + prefix);
    }

    // ------------------------------------------------------------------ search

    /// <summary>One card of the public search results.</summary>
    internal sealed record Card(string Id, string Title, string Company, string Location, string Posted);

    /// <summary>Public job-search fragment URL (10 results per request, paged by <paramref name="start"/>).</summary>
    internal static string SearchUrl(string keywords, string location, string posted, int start)
    {
        var q = $"keywords={Uri.EscapeDataString(keywords)}&location={Uri.EscapeDataString(location)}&start={start}";
        if (!string.IsNullOrWhiteSpace(posted)) q += "&f_TPR=" + Uri.EscapeDataString(posted);
        return "https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search?" + q;
    }

    /// <summary>Parses the public search-results HTML fragment into cards (pure, unit-tested).</summary>
    internal static List<Card> ParseCards(string html)
    {
        var cards = new List<Card>();
        foreach (Match li in Regex.Matches(html, @"<li>(.*?)</li>", RegexOptions.Singleline))
        {
            string h = li.Groups[1].Value;
            string id = Regex.Match(h, @"urn:li:jobPosting:(\d+)").Groups[1].Value;
            if (id.Length == 0) continue;
            string title = Text(Regex.Match(h, @"base-search-card__title[^>]*>(.*?)</h3>", RegexOptions.Singleline).Groups[1].Value);
            string company = Text(Regex.Match(h, @"base-search-card__subtitle[^>]*>(.*?)</h4>", RegexOptions.Singleline).Groups[1].Value);
            string loc = Text(Regex.Match(h, @"job-search-card__location[^>]*>(.*?)</span>", RegexOptions.Singleline).Groups[1].Value);
            string posted = Regex.Match(h, @"<time[^>]*datetime=""([^""]+)""").Groups[1].Value;
            if (title.Length > 0) cards.Add(new Card(id, title, company, loc, posted));
        }
        return cards;
    }

    /// <summary>Extracts the description text from a public job-posting page (pure, unit-tested).</summary>
    internal static string ParseDescription(string html)
    {
        var m = Regex.Match(html, @"show-more-less-html__markup[^>]*>(.*?)</div>", RegexOptions.Singleline);
        if (!m.Success) return "";
        string s = Regex.Replace(m.Groups[1].Value, @"<\s*(br|/p|/li|/ul|/h\d)\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase);
        return Regex.Replace(Text(s, keepNewlines: true), @"\n{3,}", "\n\n").Trim();
    }

    private static string Text(string html, bool keepNewlines = false)
    {
        string s = WebUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]+>", " "));
        return keepNewlines
            ? Regex.Replace(Regex.Replace(s, @"[ \t\r\f\v]+", " "), @" *\n *", "\n").Trim()
            : Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Runs the searches in a real browser and returns the jobs found (source "linkedin"). Stops early — keeping what
    /// it already has — when LinkedIn answers with a block/rate-limit (HTTP 429/999, auth wall) or on cancel.
    /// </summary>
    public static async Task<List<RawJob>> FetchJobsAsync(LinkedInBrowserConfig cfg, IReadOnlyList<string> queries,
        string location, IProgress<string>? log, CancellationToken ct)
    {
        var jobs = new List<RawJob>();
        if (!IsInstalled) { log?.Report(Loc.Instance.T("lib.notInstalled")); return jobs; }
        Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", DriverRoot);
        var rnd = new Random();
        async Task Pause()
        {
            double baseMs = Math.Clamp(cfg.PaceSeconds, 1, 30) * 1000;
            await Task.Delay(TimeSpan.FromMilliseconds(baseMs * (0.6 + rnd.NextDouble() * 0.8)), ct);
        }

        using var pw = await Playwright.CreateAsync();
        var opts = new BrowserTypeLaunchOptions { Headless = !cfg.ShowBrowser };
        if (SystemBrowser is { } exe) opts.ExecutablePath = exe;   // else: Playwright's own Chromium (installed on demand)
        await using var browser = await pw.Chromium.LaunchAsync(opts);
        await using var ctx = await browser.NewContextAsync(new BrowserNewContextOptions { Locale = "pt-PT" });
        var page = await ctx.NewPageAsync();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cards = new List<Card>();
        bool blocked = false;
        foreach (var q in queries.Where(q => !string.IsNullOrWhiteSpace(q)).Take(Math.Clamp(cfg.Queries, 1, 4)))
        {
            for (int start = 0; start < Math.Clamp(cfg.MaxPerQuery, 10, 200) && !blocked; start += 10)
            {
                ct.ThrowIfCancellationRequested();
                var resp = await page.GotoAsync(SearchUrl(q, location, cfg.Posted, start), new PageGotoOptions { Timeout = 30_000 });
                if (resp is null || resp.Status is 429 or 999 || page.Url.Contains("authwall", StringComparison.OrdinalIgnoreCase))
                {
                    blocked = true;
                    log?.Report(Loc.Instance.F("lib.blocked", resp?.Status ?? 0));
                    break;
                }
                var found = ParseCards(await page.ContentAsync());
                if (found.Count == 0) break;                         // no more results for this title
                foreach (var c in found) if (seen.Add(c.Id)) cards.Add(c);
                log?.Report(Loc.Instance.F("lib.progress", q, cards.Count));
                await Pause();
            }
            if (blocked) break;
        }

        foreach (var c in cards)
        {
            ct.ThrowIfCancellationRequested();
            string desc = "";
            if (cfg.ReadDescriptions && !blocked)
            {
                var resp = await page.GotoAsync($"https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/{c.Id}", new PageGotoOptions { Timeout = 30_000 });
                if (resp is null || resp.Status is 429 or 999) { blocked = true; log?.Report(Loc.Instance.F("lib.blocked", resp?.Status ?? 0)); }
                else desc = ParseDescription(await page.ContentAsync());
                await Pause();
            }
            string remote = Regex.IsMatch(c.Location + " " + c.Title, @"\b(remote|remoto)\b", RegexOptions.IgnoreCase) ? "remote"
                          : Regex.IsMatch(c.Location, @"h[ií]brido|hybrid", RegexOptions.IgnoreCase) ? "hybrid" : "";
            jobs.Add(new RawJob(c.Title, c.Company, c.Location, remote, $"https://www.linkedin.com/jobs/view/{c.Id}",
                desc, "linkedin", c.Posted));
        }
        log?.Report(Loc.Instance.F("lib.done", jobs.Count));
        Diag.Info($"linkedin-browser: {jobs.Count} jobs ({(blocked ? "stopped early: blocked" : "ok")})");
        return jobs;
    }
}
