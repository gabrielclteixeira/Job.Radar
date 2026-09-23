using JobRadar;

namespace JobRadar.Tests;

public class JobKeysTests
{
    [Theory]
    // real LinkedIn URLs of ONE job, differing only in per-search tracking
    [InlineData("https://pt.linkedin.com/jobs/view/backend-developer-at-deloitte-4302579973?position=39&pageNum=0&refId=jTut%2B")]
    [InlineData("https://pt.linkedin.com/jobs/view/backend-developer-at-deloitte-4302579973?position=37&pageNum=0&refId=v7lDFa5")]
    [InlineData("https://www.linkedin.com/jobs/view/4302579973/")]
    public void LinkedIn_urls_reduce_to_the_job_id(string url)
        => Assert.Equal("linkedin.com/jobs/view/4302579973", JobKeys.CanonicalUrl(url));

    [Fact]
    public void Identifying_query_params_are_kept_tracking_ones_dropped()
    {
        Assert.Equal("careers.feedzai.com/job_description?gh_jid=7960086",
            JobKeys.CanonicalUrl("https://careers.feedzai.com/job_description?gh_jid=7960086&utm_source=x&ref=abc"));
        // different Greenhouse ids stay different
        Assert.NotEqual(JobKeys.CanonicalUrl("https://careers.feedzai.com/job_description?gh_jid=7960086"),
                        JobKeys.CanonicalUrl("https://careers.feedzai.com/job_description?gh_jid=7960085"));
    }

    [Fact]
    public void Storage_key_falls_back_to_title_and_company()
        => Assert.Equal("go engineer|adentis", JobKeys.StorageKey("", "GO Engineer", "Adentis"));

    [Fact]
    public void Fuzzy_key_merges_city_spellings_but_not_different_cities()
    {
        Assert.Equal(JobKeys.FuzzyKey("Senior Backend Engineer", "Condukt", "Porto, Porto, Portugal"),
                     JobKeys.FuzzyKey("Senior  Backend Engineer", "Condukt", "Porto, Portugal (Híbrido)"));
        Assert.NotEqual(JobKeys.FuzzyKey("Backend Developer", "Deloitte", "Porto, Porto, Portugal"),
                        JobKeys.FuzzyKey("Backend Developer", "Deloitte", "Braga, Braga, Portugal"));
    }

    [Fact]
    public void Duplicates_keeps_the_scored_richest_row()
    {
        var a = new JobEntity { Title = "Dev", Company = "X", Location = "Porto", Description = "short", AiScore = null };
        var b = new JobEntity { Title = "Dev", Company = "X", Location = "Porto, Portugal", Description = "longer text", AiScore = 70 };
        var c = new JobEntity { Title = "Dev", Company = "X", Location = "Braga", Description = "other city" };
        var drop = JobKeys.Duplicates(new[] { a, b, c });
        Assert.Equal(new[] { a }, drop);
    }

    [Fact]
    public void Duplicates_keeps_the_higher_score_among_scored_rows()
    {
        var lo = new JobEntity { Title = "Go Dev", Company = "WK", Location = "Porto", AiScore = 78, Description = "a longer description here" };
        var hi = new JobEntity { Title = "Go Dev", Company = "WK", Location = "Porto", AiScore = 82, Description = "short" };
        Assert.Equal(new[] { lo }, JobKeys.Duplicates(new[] { lo, hi }));
    }

    [Fact]
    public void Duplicates_prefers_a_row_the_user_acted_on()
    {
        var applied = new JobEntity { Title = "Dev", Company = "X", Location = "Porto", Status = "applied" };
        var scored = new JobEntity { Title = "Dev", Company = "X", Location = "Porto", AiScore = 90, Description = "much longer description" };
        Assert.Equal(new[] { scored }, JobKeys.Duplicates(new[] { scored, applied }));
    }
}
