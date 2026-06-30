namespace JobRadar;

/// <summary>
/// Downloads the official Ollama runtime installer for Windows on demand (key-free, from ollama.com which
/// redirects to the GitHub release asset). The app then runs it silently and waits for the local server —
/// keeping our own installer small while still offering a one-click AI engine. Big file (~700&nbsp;MB), so
/// this uses its OWN HttpClient with no per-request timeout (the update checker's 20&nbsp;s would abort it).
/// </summary>
public static class OllamaInstaller
{
    public const string WindowsSetupUrl = "https://ollama.com/download/OllamaSetup.exe";

    // No timeout: a 700 MB download must not be cut off at 20 s. Cancellation is via the token.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Streams the Windows installer to <paramref name="destPath"/>, reporting a 0–1 fraction.</summary>
    public static async Task<bool> DownloadWindowsSetupAsync(
        string destPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(WindowsSetupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return false;
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(destPath);
            var buf = new byte[1 << 20];
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
