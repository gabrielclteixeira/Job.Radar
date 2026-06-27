namespace JobRadar;

/// <summary>
/// Installs a Hugging Face GGUF model into LM Studio's models folder by direct download — no `lms` CLI, no
/// external tool. LM Studio indexes any GGUF placed under <c>&lt;modelsDir&gt;/&lt;owner&gt;/&lt;repo&gt;/&lt;file&gt;.gguf</c>,
/// which is exactly the layout we write. Streamed with progress; downloads to a temp file and moves on success
/// so a partial download is never left where LM Studio would index it.
/// </summary>
public static class LmStudioInstall
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan }; // big files; we cancel via ct

    /// <summary>An LM Studio model found on disk: friendly name (file minus .gguf), its &lt;owner&gt;/&lt;repo&gt;
    /// folder, size in GB, and full path.</summary>
    public record InstalledGguf(string Name, string Repo, double SizeGb, string Path);

    /// <summary>Lists GGUF models already in LM Studio's models dir (recursive; skips mmproj sidecars). Works
    /// even when LM Studio isn't running — it just reads the folder.</summary>
    public static List<InstalledGguf> ListInstalled()
    {
        var list = new List<InstalledGguf>();
        try
        {
            string root = ModelsDir();
            if (!Directory.Exists(root)) return list;
            foreach (var path in Directory.EnumerateFiles(root, "*.gguf", SearchOption.AllDirectories))
            {
                string fn = Path.GetFileName(path);
                if (fn.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)) continue;
                string? dir = Path.GetDirectoryName(path);
                string repo = dir is not null ? Path.GetRelativePath(root, dir).Replace('\\', '/') : "";
                double gb = Math.Round(new FileInfo(path).Length / 1_000_000_000d, 1);
                list.Add(new InstalledGguf(Path.GetFileNameWithoutExtension(fn), repo, gb, path));
            }
        }
        catch { /* best-effort */ }
        return list;
    }

    /// <summary>Deletes an installed GGUF file (and its now-empty folders up to the models dir).</summary>
    public static bool Remove(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            string root = ModelsDir();
            string? dir = Path.GetDirectoryName(path);
            while (dir is not null && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
                   Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>LM Studio's models directory: the LMSTUDIO_MODELS override, else the default ~/.lmstudio/models.</summary>
    public static string ModelsDir()
    {
        string? env = Environment.GetEnvironmentVariable("LMSTUDIO_MODELS");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models");
    }

    /// <summary>Downloads <paramref name="file"/> from the HF <paramref name="repo"/> into the LM Studio models
    /// dir, reporting (status, 0–1 fraction). Returns true on success.</summary>
    public static async Task<bool> DownloadGgufAsync(string repo, string file,
        IProgress<(string status, double frac)>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(file)) return false;
        string dir = Path.Combine(ModelsDir(), repo.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
        string dest = Path.Combine(dir, file);
        string tmp = dest + ".part";
        try
        {
            Directory.CreateDirectory(dir);
            string url = $"https://huggingface.co/{repo}/resolve/main/{Uri.EscapeDataString(file)}?download=true";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "JobRadar/1.0");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) { progress?.Report(($"HTTP {(int)resp.StatusCode}", 0)); return false; }

            long total = resp.Content.Headers.ContentLength ?? -1;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buf = new byte[1 << 20];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report(($"{done / 1_048_576}/{total / 1_048_576} MB", (double)done / total));
                    else progress?.Report(($"{done / 1_048_576} MB", 0));
                }
            }

            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
            progress?.Report(("success", 1));
            return true;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            return false;
        }
    }
}
