using System.Globalization;
using System.Text.RegularExpressions;

namespace JobRadar;

/// <summary>
/// Resolves an annual-EUR salary for a job: first from the fetcher's structured
/// fields (Adzuna/RemoteOK), else by regex-scanning the title + description.
/// Best-effort — leaves SalaryAnnualEur null when nothing reliable is found.
/// </summary>
public static class SalaryParser
{
    public static void Apply(JobEntity j, SalaryConfig cfg)
    {
        // 1) Structured (from the fetcher) — treated as annual.
        if (j.SalaryMin > 0 || j.SalaryMax > 0)
        {
            double min = ToEur(j.SalaryMin, j.SalaryCurrency, cfg);
            double max = ToEur(j.SalaryMax, j.SalaryCurrency, cfg);
            double annual = (min > 0 && max > 0) ? (min + max) / 2 : Math.Max(min, max);
            j.SalaryAnnualEur = (int)Math.Round(annual);
            j.SalaryText = FormatRange(min, max);
            return;
        }

        // 2) Regex from free text.
        var found = ExtractFromText($"{j.Title}  {j.Description}", cfg);
        if (found is not null)
        {
            j.SalaryAnnualEur = found.Value.annualEur;
            j.SalaryText = found.Value.text;
        }
    }

    private static double ToEur(double amount, string? cur, SalaryConfig cfg)
        => amount <= 0 ? 0 : (cur ?? "").ToUpperInvariant() switch
        {
            "USD" => amount * cfg.UsdToEur,
            "GBP" => amount * cfg.GbpToEur,
            _ => amount, // EUR or unknown
        };

    private static string FormatRange(double min, double max)
    {
        static string K(double v) => "€" + Math.Round(v / 1000) + "k";
        if (min > 0 && max > 0 && Math.Abs(max - min) > 1) return $"{K(min)}–{K(max)}";
        return K(Math.Max(min, max));
    }

    // Currency symbol/code, a number (with thousands separators), optional 'k', optional trailing currency.
    private static readonly Regex Money = new(
        @"(?<c1>[€$£]|EUR|USD|GBP)?\s?(?<n>\d{1,3}(?:[.,]\d{3})+|\d{2,6})\s?(?<k>[kK])?\s?(?<c2>€|EUR|USD|GBP)?",
        RegexOptions.Compiled);

    private static readonly Regex MonthlyHint = new(
        @"month|mês|mensal|/mo\b|per month|por mês", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (int annualEur, string text)? ExtractFromText(string text, SalaryConfig cfg)
    {
        bool monthly = MonthlyHint.IsMatch(text);
        var amounts = new List<double>();

        foreach (Match m in Money.Matches(text))
        {
            string cur = (m.Groups["c1"].Value + m.Groups["c2"].Value).Trim();
            if (cur.Length == 0) continue; // require an explicit currency to avoid matching random numbers

            string raw = m.Groups["n"].Value.Replace(".", "").Replace(",", "");
            if (!double.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out double val)) continue;
            if (m.Groups["k"].Value.Length > 0) val *= 1000;

            string code = cur switch { "€" => "EUR", "$" => "USD", "£" => "GBP", _ => cur.ToUpperInvariant() };
            double eur = ToEur(val, code, cfg);

            if (eur < 10000)
            {
                if (monthly) eur *= cfg.MonthsPerYear; // e.g. "€2500/month"
                else continue;                          // sub-10k annual with currency but no monthly context → noise
            }
            if (eur is >= 8000 and <= 500000) amounts.Add(eur);
        }

        if (amounts.Count == 0) return null;
        double min = amounts.Min(), max = amounts.Max();
        return ((int)Math.Round((min + max) / 2), FormatRange(min, max));
    }
}
