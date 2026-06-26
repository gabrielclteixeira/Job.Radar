namespace JobRadar;

/// <summary>
/// Resolves where the app keeps its files.
///
/// In a <b>packaged/installed</b> build the install directory is read-only (Program Files, a mounted
/// AppImage, a macOS .app bundle), so all writable state must live in a per-user data directory. We
/// detect that case with a <c>.packaged</c> sentinel dropped next to the executable at publish time,
/// then seed the data dir once from the bundled assets and use it as the app root from then on.
///
/// In a <b>dev</b> build (run from the repo) there's no sentinel, so the caller keeps its previous
/// behaviour of walking up to the repo root — nothing changes for development.
/// </summary>
public static class AppPaths
{
    /// <summary>True when running from an installed package (the publish step dropped a .packaged file).</summary>
    public static bool IsPackaged(string baseDir) => File.Exists(Path.Combine(baseDir, ".packaged"));

    /// <summary>Per-user writable data directory (e.g. %APPDATA%/JobRadar, ~/.config/JobRadar).</summary>
    public static string DataDir
    {
        get
        {
            string b = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(b))
                b = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(b, "JobRadar");
        }
    }

    /// <summary>
    /// Ensures the data dir exists and is seeded (copy-if-missing) with the bundled read-only assets the
    /// app needs at runtime — config, the demo sample, and the Go fetcher binary. Returns the data dir.
    /// User edits are never overwritten (only missing files are copied).
    /// </summary>
    public static string EnsureSeeded(string installDir)
    {
        string data = DataDir;
        Directory.CreateDirectory(data);

        foreach (var name in new[] { "appsettings.json", "fetcher-config.json", "fetcher.exe", "fetcher" })
            CopyIfMissing(Path.Combine(installDir, name), Path.Combine(data, name));

        // demo dataset
        string srcSamples = Path.Combine(installDir, "samples");
        if (Directory.Exists(srcSamples))
        {
            string dstSamples = Path.Combine(data, "samples");
            Directory.CreateDirectory(dstSamples);
            foreach (var f in Directory.GetFiles(srcSamples))
                CopyIfMissing(f, Path.Combine(dstSamples, Path.GetFileName(f)));
        }
        return data;
    }

    private static void CopyIfMissing(string src, string dst)
    {
        try { if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst); }
        catch { /* best-effort seeding */ }
    }
}
