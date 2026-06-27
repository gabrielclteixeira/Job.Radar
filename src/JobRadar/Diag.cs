using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace JobRadar;

/// <summary>
/// Dependency-free diagnostic log. Writes a daily, UTC-timestamped, <b>metadata-only</b> file under
/// <see cref="AppPaths.DataDir"/>/logs (never secrets, prompts, replies or CV text — only what callers pass),
/// and keeps a small in-memory ring buffer the UI can export. Every operation is best-effort: logging must never
/// throw or interfere with the app.
/// </summary>
public static class Diag
{
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<string> Ring = new();
    private const int RingMax = 400;
    private static bool _init;

    /// <summary>Folder where the daily log files live (same in dev and packaged builds).</summary>
    public static string LogDir => Path.Combine(AppPaths.DataDir, "logs");

    private static string CurrentFile => Path.Combine(LogDir, $"jobradar-{DateTime.UtcNow:yyyy-MM-dd}.log");

    /// <summary>Creates the log dir, prunes files older than ~14 days, and writes a session header. Idempotent.</summary>
    public static void Init()
    {
        try
        {
            if (_init) return;
            _init = true;
            Directory.CreateDirectory(LogDir);
            Prune();
            string ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";
            double ramGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024d * 1024 * 1024), 1);
            Write("INFO", "──── session start ────");
            Write("INFO", $"app=Job Radar v{ver} os=\"{RuntimeInformation.OSDescription.Trim()}\" " +
                          $"arch={RuntimeInformation.OSArchitecture} dotnet={Environment.Version} ram={ramGb}GB");
        }
        catch { /* logging never throws */ }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", Compose(msg, ex));
    public static void Fatal(string msg, Exception? ex = null) => Write("FATAL", Compose(msg, ex));

    /// <summary>The most recent <paramref name="n"/> buffered lines (for the export bundle).</summary>
    public static string Tail(int n)
    {
        var all = Ring.ToArray();
        int skip = Math.Max(0, all.Length - n);
        return string.Join(Environment.NewLine, all.Skip(skip));
    }

    private static string Compose(string msg, Exception? ex)
        => ex is null ? msg : $"{msg} | {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}";

    private static void Write(string level, string msg)
    {
        string line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {level} {msg}";
        Ring.Enqueue(line);
        while (Ring.Count > RingMax) Ring.TryDequeue(out _);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* disk full / locked — keep the in-memory copy and move on */ }
    }

    private static void Prune() => PruneDir(LogDir, "jobradar-*.log");

    private static void PruneDir(string dir, string pattern)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-14);
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
                if (File.GetLastWriteTimeUtc(f) < cutoff) { try { File.Delete(f); } catch { } }
        }
        catch { /* best-effort */ }
    }

    /// <summary>Folder of archived plan-generation run records (kept alongside the logs, out of the repo).</summary>
    public static string ReasoningDir => Path.Combine(AppPaths.DataDir, "reasoning");

    /// <summary>Archives one plan run (config + per-call metadata + reasoning transcript) so model performance
    /// can be reviewed later. Timestamped, pruned to ~14 days. Returns the path, or null on failure.</summary>
    public static string? SaveRunRecord(string content)
    {
        try
        {
            Directory.CreateDirectory(ReasoningDir);
            PruneDir(ReasoningDir, "plan-reasoning-*.txt");
            string path = Path.Combine(ReasoningDir, $"plan-reasoning-{DateTime.UtcNow:yyyy-MM-dd-HHmmss}.txt");
            File.WriteAllText(path, content, Encoding.UTF8);
            return path;
        }
        catch { return null; }
    }
}
