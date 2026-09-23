using System.Text;

namespace JobRadar;

/// <summary>
/// Crash-safe persistence for the machine-local JSON stores (CV, coach history, plan, profile, caches).
/// A plain File.WriteAllText truncates first, so a crash or power cut mid-write left half a JSON file; the
/// loaders then swallowed the parse error and returned "empty", and the next save wrote that empty state over
/// the user's data for good. Writes now go to a temp file that atomically replaces the target, and a file that
/// fails to parse is moved aside (never silently overwritten).
/// </summary>
public static class SafeFile
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> via a temp file + atomic replace.</summary>
    public static void WriteAllText(string path, string content, Encoding? encoding = null)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, encoding ?? Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Moves an unreadable file to "&lt;name&gt;.corrupt-&lt;stamp&gt;" so the next save can't destroy it,
    /// and logs where it went. Best-effort; returns the new path or null.</summary>
    public static string? Quarantine(string? path, Exception? why = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            string dest = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, dest, overwrite: true);
            Diag.Error($"unreadable file moved aside: {Path.GetFileName(path)} → {Path.GetFileName(dest)}", why);
            return dest;
        }
        catch { return null; }
    }
}
