using JobRadar;

namespace JobRadar.Tests;

/// <summary>F1: parsing of LinkedIn's public job pages (fixtures captured from the live site, 2026-09-23).</summary>
public class LinkedInBrowserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Search_cards_are_parsed()
    {
        var cards = LinkedInBrowser.ParseCards(Fixture("linkedin-search.html"));
        Assert.Equal(3, cards.Count);
        Assert.All(cards, c =>
        {
            Assert.Matches(@"^\d{6,}$", c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.Title));
            Assert.False(string.IsNullOrWhiteSpace(c.Company));
            Assert.Contains("Porto", c.Location);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", c.Posted);
        });
        Assert.DoesNotContain(cards, c => c.Title.Contains('<') || c.Company.Contains('<'));
    }

    [Fact]
    public void Posting_description_is_plain_text()
    {
        string d = LinkedInBrowser.ParseDescription(Fixture("linkedin-posting.html"));
        Assert.True(d.Length > 200);
        Assert.DoesNotContain("<", d);
        Assert.Contains("Wolters Kluwer", d);
    }

    [Fact]
    public void Search_url_encodes_and_pages()
    {
        string u = LinkedInBrowser.SearchUrl(".NET Developer", "Porto, Portugal", "r604800", 20);
        Assert.StartsWith("https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search?", u);
        Assert.Contains("keywords=.NET%20Developer", u);
        Assert.Contains("location=Porto%2C%20Portugal", u);
        Assert.Contains("start=20", u);
        Assert.Contains("f_TPR=r604800", u);
        Assert.DoesNotContain("f_TPR", LinkedInBrowser.SearchUrl("x", "y", "", 0));
    }

    [Fact]
    public void Playwright_package_matches_the_driver_version_we_download()
    {
        // The on-demand driver (playwright-core + Node) must match the Microsoft.Playwright assembly; bump
        // LinkedInBrowser.PlaywrightVersion / NodeVersion together with the package.
        var v = typeof(Microsoft.Playwright.Playwright).Assembly.GetName().Version!;
        Assert.Equal(LinkedInBrowser.PlaywrightVersion, $"{v.Major}.{v.Minor}.{v.Build}");
    }
}
