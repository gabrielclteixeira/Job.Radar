using JobRadar;

namespace JobRadar.Tests;

/// <summary>"Ask coach" from a job card: the posting the coach receives as context.</summary>
public class CoachJobContextTests
{
    private static JobEntity Job() => new()
    {
        Title = "Team Lead Software Engineer",
        Company = "Capital on Tap",
        Location = "Matosinhos, Porto, Portugal",
        Remote = "hybrid",
        Url = "https://www.linkedin.com/jobs/view/123",
        SalaryText = "€70k–€85k",
        AiScore = 48,
        PreScore = 65,
        AiVerdict = "Well-paid hybrid Porto role, but a people-first team lead position.",
        AiReasons = "[\"Pay exceeds target\",\"Software engineering field matches\"]",
        AiRedFlags = "[\"Seniority well above the candidate's target\"]",
        Description = "<p>We&#39;re hiring a <b>Team Lead</b>.</p><ul><li>Lead 6 engineers</li><li>C# &amp; .NET</li></ul>",
    };

    [Fact]
    public void Job_context_carries_facts_score_reasons_and_plain_description()
    {
        string c = Coach.FormatJobContext(Job());
        Assert.Contains("Title: Team Lead Software Engineer", c);
        Assert.Contains("Company: Capital on Tap", c);
        Assert.Contains("Matosinhos, Porto, Portugal · hybrid", c);
        Assert.Contains("Salary: €70k–€85k", c);
        Assert.Contains("Fit score: 48/100 (AI)", c);
        Assert.Contains("+ Pay exceeds target", c);
        Assert.Contains("! Seniority well above the candidate's target", c);
        Assert.Contains("We're hiring a Team Lead .", c.Replace("  ", " "));
        Assert.Contains("Lead 6 engineers", c);
        Assert.Contains("C# & .NET", c);
        Assert.DoesNotContain("<", c);
    }

    [Fact]
    public void Long_descriptions_are_capped()
    {
        var j = Job();
        j.Description = new string('x', Coach.JobDescriptionCap * 3);
        string c = Coach.FormatJobContext(j);
        Assert.True(c.Length < Coach.JobDescriptionCap + 1_000, $"context was {c.Length} chars");
        Assert.EndsWith("…", c);
    }

    [Fact]
    public void Keyword_only_job_uses_the_keyword_score_and_verdict()
    {
        var j = Job();
        j.AiScore = null; j.AiVerdict = null; j.AiReasons = null; j.AiRedFlags = null;
        j.BaseVerdict = "Keyword match on C#.";
        string c = Coach.FormatJobContext(j);
        Assert.Contains("Fit score: 65/100 (keyword-only)", c);
        Assert.Contains("Verdict: Keyword match on C#.", c);
    }

    [Fact]
    public void System_prompt_includes_the_job_block_only_when_pinned()
    {
        var profile = new UserProfile { Name = "John Doe", Field = "Software Engineering" };
        string with = Coach.BuildSystemPrompt(profile, "", null, Coach.FormatJobContext(Job()));
        string without = Coach.BuildSystemPrompt(profile, "", null);
        Assert.Contains("== JOB UNDER DISCUSSION", with);
        Assert.Contains("Team Lead Software Engineer", with);
        Assert.DoesNotContain("JOB UNDER DISCUSSION", without);
    }
}
