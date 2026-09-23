using JobRadar;

namespace JobRadar.Tests;

public class SmallFixesTests
{
    [Theory]
    [InlineData("€52.5k–€60k", 52500)]
    [InlineData("€52,5k", 52500)]
    [InlineData("€35,000–€45,000/yr", 35000)]
    [InlineData("35.000 €", 35000)]
    [InlineData("€48k", 48000)]
    public void FirstEur_reads_decimals_before_k(string band, int expected)
        => Assert.Equal(expected, PlanText.FirstEur(band));

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+351 912", "'+351 912")]
    [InlineData("@evil", "'@evil")]
    [InlineData("Backend, Porto", "\"Backend, Porto\"")]
    [InlineData("plain", "plain")]
    public void Csv_cells_are_formula_safe(string input, string expected)
        => Assert.Equal(expected, CsvText.Cell(input));

    [Fact]
    public void Csv_numbers_use_a_dot_regardless_of_culture()
    {
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("pt-PT");
            Assert.Equal("3.9", CsvText.Num(3.9));
            Assert.Equal("", CsvText.Num((double?)null));
        }
        catch (System.Globalization.CultureNotFoundException) { /* invariant-globalization host: nothing to check */ }
        finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
    }

    private static UserProfile Profile() => new()
    {
        Field = "Software Engineering",
        JobTitles = new() { "Backend Developer", "Go Developer" },
        CoreSkills = new() { "C# / .NET (ASP.NET Core, Blazor)", "Go", "AI agents & LLM integration (MCP)" },
        Locations = new() { "Porto", "Portugal" },
        Remote = true, Hybrid = true, Onsite = false,
    };

    private static JobEntity Job(string title, string loc, string remote = "", string desc = "")
        => new() { Title = title, Location = loc, Remote = remote, Description = desc };

    [Fact]
    public void Porto_Alegre_is_not_Porto()
    {
        Assert.False(ProfileFilter.Evaluate(Job("Backend Developer", "Porto Alegre, Brazil"), Profile(), new AppConfig()).relevant);
        Assert.True(ProfileFilter.Evaluate(Job("Backend Developer", "Porto, Portugal"), Profile(), new AppConfig()).relevant);
    }

    [Fact]
    public void A_lone_short_title_token_is_not_relevance()
    {
        Assert.False(ProfileFilter.Evaluate(Job("AI Cinematic Video Editor", "Worldwide", "remote"), Profile(), new AppConfig()).relevant);
        Assert.True(ProfileFilter.Evaluate(Job("Go Developer", "Porto, Portugal"), Profile(), new AppConfig()).relevant);
    }
}
