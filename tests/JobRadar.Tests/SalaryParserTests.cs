using JobRadar;

namespace JobRadar.Tests;

public class SalaryParserTests
{
    private static readonly SalaryConfig Cfg = new();

    private static JobEntity Parse(string desc, double min = 0, double max = 0, string cur = "")
    {
        var j = new JobEntity { Title = "Backend Developer", Description = desc, SalaryMin = min, SalaryMax = max, SalaryCurrency = cur };
        SalaryParser.Apply(j, Cfg);
        return j;
    }

    [Theory]
    [InlineData("Salary: €40-50k per year", 45000)]
    [InlineData("Salário 40.000 – 50.000 € brutos", 45000)]
    [InlineData("Pay: €45 000 gross", 45000)]
    [InlineData("€52.5k base", 52500)]
    [InlineData("EUR 60,000", 60000)]
    [InlineData("€2.500 brutos mensais", 30000)]
    [InlineData("€3000/month", 36000)]
    public void Extracts_annual_eur(string text, int expected)
        => Assert.Equal(expected, Parse(text).SalaryAnnualEur);

    [Fact]
    public void Monthly_cue_far_from_the_amount_is_ignored()
    {
        // "6-month contract" used to turn every figure in the posting monthly (×12).
        var j = Parse("This is a 6-month contract with a great team and many perks. Budget: €1,200 laptop. Salary €50k.");
        Assert.Equal(50000, j.SalaryAnnualEur);
    }

    [Theory]
    [InlineData("€35/hour freelance")]
    [InlineData("Rate: €400 per day")]
    [InlineData("We have 250 employees and 12 offices")]  // no currency → nothing
    public void Rates_and_bare_numbers_are_not_salaries(string text)
        => Assert.Null(Parse(text).SalaryAnnualEur);

    [Fact]
    public void Structured_hourly_figure_is_not_taken_as_annual()
    {
        var j = Parse("", min: 25, max: 35, cur: "USD");
        Assert.Null(j.SalaryAnnualEur);
    }

    [Fact]
    public void Structured_annual_range_uses_midpoint()
    {
        var j = Parse("", min: 40000, max: 60000, cur: "EUR");
        Assert.Equal(50000, j.SalaryAnnualEur);
        Assert.Equal("€40k–€60k", j.SalaryText);
    }
}
