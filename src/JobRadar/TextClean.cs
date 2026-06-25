using System.Net;
using System.Text;

namespace JobRadar;

/// <summary>
/// Cleans up text coming from job sources: HTML entities and "mojibake" — UTF-8 that was
/// decoded as Windows-1252 somewhere upstream (e.g. "Educación" → "EducaciÃ³n",
/// "Engineer — Backend" → "Engineer â€" Backend").
/// </summary>
public static class TextClean
{
    private static readonly Encoding Cp1252;

    static TextClean()
    {
        // Code page 1252 isn't built into modern .NET; register the provider once.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1252 = Encoding.GetEncoding(1252,
            EncoderFallback.ExceptionFallback,   // throw if a char isn't a real 1252 char → not mojibake
            DecoderFallback.ReplacementFallback);
    }

    /// <summary>Decodes HTML entities, then repairs mojibake. Safe on already-correct text.</summary>
    public static string Clean(string? s)
        => string.IsNullOrEmpty(s) ? "" : FixMojibake(WebUtility.HtmlDecode(s)).Trim();

    /// <summary>
    /// Reverses a UTF-8→Windows-1252 misread. Only rewrites when the reverse round-trip yields
    /// clean UTF-8; genuinely-correct text (e.g. German "ö") round-trips to invalid bytes and is
    /// therefore left untouched. Real Unicode beyond cp1252 (CJK, emoji) is also left alone.
    /// </summary>
    public static string FixMojibake(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        // Nothing above ASCII → no mojibake possible.
        bool hasHigh = false;
        foreach (var c in s) if (c >= 0x80) { hasHigh = true; break; }
        if (!hasHigh) return s;

        try
        {
            byte[] bytes = Cp1252.GetBytes(s);          // throws if s has chars cp1252 can't represent
            string decoded = Encoding.UTF8.GetString(bytes);
            // If the reverse produced replacement chars, the bytes weren't valid UTF-8 →
            // the original text was already correct, so keep it.
            return decoded.Contains('�') ? s : decoded;
        }
        catch (EncoderFallbackException) { return s; }  // real Unicode beyond cp1252 — leave as-is
        catch { return s; }
    }
}
