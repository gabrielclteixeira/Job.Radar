using System.Globalization;

namespace JobRadar;

/// <summary>
/// CSV cells for the exports (jobs, companies). Two problems with the old per-file helpers:
/// numbers were formatted with the UI culture (pt-PT writes "3,9", which splits the column), and text taken
/// from job boards was written raw — a title starting with "=", "+", "-" or "@" is executed as a formula when the
/// file is opened in Excel/LibreOffice (CSV injection).
/// </summary>
public static class CsvText
{
    /// <summary>Text cell: neutralises formula-like starts, then quotes when needed.</summary>
    public static string Cell(string? s)
    {
        s ??= "";
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains(';')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }

    /// <summary>Numeric cell, always with "." as the decimal separator.</summary>
    public static string Num(double? v, string format = "0.0")
        => v?.ToString(format, CultureInfo.InvariantCulture) ?? "";

    public static string Num(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
}
