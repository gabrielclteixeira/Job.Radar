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
    /// <summary>Plausible annual EUR band; anything outside is a rate, a budget or noise, not a salary.</summary>
    private const double MinAnnual = 8000, MaxAnnual = 500000;

    public static void Apply(JobEntity j, SalaryConfig cfg)
    {
        // 1) Structured (from the fetcher) — treated as annual, but only when it looks like one (an hourly or
        // monthly figure in these fields would otherwise read as a tiny annual salary and sink the job).
        if (j.SalaryMin > 0 || j.SalaryMax > 0)
        {
            double min = ToEur(j.SalaryMin, j.SalaryCurrency, cfg);
            double max = ToEur(j.SalaryMax, j.SalaryCurrency, cfg);
            double annual = (min > 0 && max > 0) ? (min + max) / 2 : Math.Max(min, max);
            if (annual is >= MinAnnual and <= MaxAnnual)
            {
                j.SalaryAnnualEur = (int)Math.Round(annual);
                j.SalaryText = FormatRange(min, max);
                return;
            }
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

    // A number: "52.5" only when a 'k' follows; grouped thousands ("45.000", "45,000", "45 000"); or plain digits.
    private const string Num = @"\d{1,3}[.,]\d{1,2}(?=\s?[kK])|\d{1,3}(?:[.,   ]\d{3})+(?!\d)|\d{2,6}";

    // Currency, a number with optional 'k', optionally a range to a second number ("€40-50k", "40.000 – 50.000 €",
    // "$90k to $110k"), then an optional trailing currency. Both ends share the currency and the 'k'.
    private static readonly Regex Money = new(
        $@"(?<c1>[€$£]|EUR|USD|GBP)?\s?(?<n1>{Num})\s?(?<k1>[kK])?" +
        $@"(?:\s?(?:-|–|—|to|a|até)\s?(?:[€$£])?\s?(?<n2>{Num})\s?(?<k2>[kK])?)?" +
        @"\s?(?<c2>€|EUR|USD|GBP)?",
        RegexOptions.Compiled);

    private static readonly Regex MonthlyHint = new(
        @"month|/mo\b|\bmês\b|\bmes\b|mensa(l|is)|mensual|p\.?\s?m\.?\b|/m[eê]s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RateHint = new(
        @"/\s?h(ou)?r?\b|per hour|hourly|/\s?hora|por hora|\bà hora|/\s?day|per day|daily rate|/\s?dia|por dia",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Characters around an amount searched for a "per month"/"per hour" cue. The cue must sit next to
    /// the figure: scanning the whole text let "6-month contract" turn every amount in the posting monthly.</summary>
    private const int CueWindow = 25;

    private static (int annualEur, string text)? ExtractFromText(string text, SalaryConfig cfg)
    {
        var amounts = new List<double>();

        foreach (Match m in Money.Matches(text))
        {
            string cur = (m.Groups["c1"].Value + m.Groups["c2"].Value).Trim();
            if (cur.Length == 0) continue; // require an explicit currency to avoid matching random numbers

            string around = Window(text, m.Index, m.Length);
            if (RateHint.IsMatch(around)) continue;  // hourly/day rates aren't salaries
            bool monthly = MonthlyHint.IsMatch(around);

            string code = cur.Contains('$') || cur.Contains("USD") ? "USD"
                        : cur.Contains('£') || cur.Contains("GBP") ? "GBP" : "EUR";

            bool k1 = m.Groups["k1"].Success, k2 = m.Groups["k2"].Success;
            var vals = new List<double>();
            if (ParseNum(m.Groups["n1"].Value) is double a)
                vals.Add(a * (k1 || (k2 && a < 1000) ? 1000 : 1)); // "€40-50k": the 'k' covers both ends
            if (m.Groups["n2"].Success && ParseNum(m.Groups["n2"].Value) is double b)
                vals.Add(b * (k2 || (k1 && b < 1000) ? 1000 : 1));

            foreach (var v in vals)
            {
                double eur = ToEur(v, code, cfg);
                if (monthly && eur < 30000) eur *= cfg.MonthsPerYear;           // "€2.500/mês"
                else if (eur < 10000) continue;                                   // sub-10k with no monthly cue → noise
                if (eur is >= MinAnnual and <= MaxAnnual) amounts.Add(eur);
            }
        }

        if (amounts.Count == 0) return null;
        double min = amounts.Min(), max = amounts.Max();
        return ((int)Math.Round((min + max) / 2), FormatRange(min, max));
    }

    private static string Window(string text, int index, int length)
    {
        int from = Math.Max(0, index - CueWindow);
        int to = Math.Min(text.Length, index + length + CueWindow);
        return text[from..to];
    }

    /// <summary>"52.5" / "52,5" (decimal, used with 'k') → 52.5; "45.000" / "45,000" / "45 000" → 45000.</summary>
    private static double? ParseNum(string raw)
    {
        raw = raw.Trim();
        var dec = Regex.Match(raw, @"^(\d{1,3})[.,](\d{1,2})$");
        if (dec.Success)
            return double.Parse($"{dec.Groups[1].Value}.{dec.Groups[2].Value}", CultureInfo.InvariantCulture);
        string digits = Regex.Replace(raw, @"[.,   ]", "");
        return double.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
