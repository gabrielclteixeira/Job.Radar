using System.Text.RegularExpressions;
using JobRadar;

namespace JobRadar.Tests;

/// <summary>Guards the i18n rule: every UI string exists in BOTH languages with the same format placeholders.</summary>
public class StringsTests
{
    [Fact]
    public void Pt_and_En_have_the_same_keys()
    {
        Assert.Empty(Strings.Pt.Keys.Except(Strings.En.Keys));
        Assert.Empty(Strings.En.Keys.Except(Strings.Pt.Keys));
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(x => x));
        var mismatched = Strings.Pt.Keys.Where(Strings.En.ContainsKey)
            .Where(k => Holes(Strings.Pt[k]) != Holes(Strings.En[k]))
            .ToList();
        Assert.Empty(mismatched);
    }

    [Fact]
    public void No_empty_values()
        => Assert.Empty(Strings.Pt.Concat(Strings.En).Where(kv => string.IsNullOrWhiteSpace(kv.Value) && kv.Key != "usage.unknown").Select(kv => kv.Key));
}
