namespace JobRadar;

/// <summary>
/// Detects LM Studio models already on disk so they can be listed, activated and removed. (Installing/downloading
/// models is left to LM Studio itself — the app's one-click install is Ollama-only.)
/// </summary>
public static class LmStudioInstall
{
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
}
