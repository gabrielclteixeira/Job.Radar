using JobRadar;

namespace JobRadar.Tests;

public class ProfileFilterTests
{
    [Theory]
    [InlineData("asp.net core developer", ".net", true)]   // symbol-led token inside a word
    [InlineData("c#/.net engineer", "c#", true)]
    [InlineData("c#/.net engineer", ".net", true)]
    [InlineData("good category", "go", false)]              // alphanumeric edges keep the boundary
    [InlineData("go developer", "go", true)]
    [InlineData("golang", "go", false)]
    [InlineData("international sales", "intern", false)]
    [InlineData("software intern", "intern", true)]
    [InlineData("javascript engineer", "java", false)]
    [InlineData("gogo go", "go", true)]                     // a rejected hit mustn't skip a later valid one
    public void WordIn_respects_word_boundaries(string text, string token, bool expected)
        => Assert.Equal(expected, ProfileFilter.WordIn(text, token));

    [Fact]
    public void SkillTerms_expands_phrase_skills_and_drops_generic_parts()
    {
        Assert.Equal(new[] { "c#", ".net", "asp.net core", "blazor" }, ProfileFilter.SkillTerms("C# / .NET (ASP.NET Core, Blazor)"));
        Assert.Equal(new[] { "ai agents", "llm integration", "mcp" }, ProfileFilter.SkillTerms("AI agents & LLM integration (MCP)"));
        Assert.Equal(new[] { "go" }, ProfileFilter.SkillTerms("Go"));
        // an all-generic phrase falls back to itself rather than vanishing
        Assert.Equal(new[] { "backend development" }, ProfileFilter.SkillTerms("Backend development"));
    }

    private static UserProfile Gabriel() => new()
    {
        Field = "Software Engineering",
        JobTitles = new() { "Backend Developer", ".NET Developer" },
        CoreSkills = new() { "C# / .NET (ASP.NET Core, Blazor)", "Go", "Backend development" },
        Skills = new() { "PostgreSQL", "Docker" },
        Locations = new() { "Porto", "Portugal" },
        Remote = true, Hybrid = true, Onsite = false,
        SeniorityTarget = "mid",
    };

    private static AppConfig Cfg() => new() { SeniorityExclude = new() { "intern", "internship", "principal", "head of" } };

    private static JobEntity Job(string title, string desc = "", string loc = "Porto", string remote = "") =>
        new() { Title = title, Description = desc, Location = loc, Remote = remote };

    [Fact]
    public void Phrase_core_skill_now_counts_as_a_stack_hit()
    {
        var (relevant, _, explanation, _) = ProfileFilter.Evaluate(
            Job("Software Engineer", "We build APIs in C# and ASP.NET Core with PostgreSQL."), Gabriel(), Cfg());
        Assert.True(relevant);
        // Language-independent: the explanation follows the UI language (Loc).
        Assert.Contains(Loc.Instance.F("filter.core", "c#", Cfg().StackBonus), explanation);
        Assert.DoesNotContain(Loc.Instance.F("filter.noCore", Cfg().OffStackPenalty), explanation);
    }

    [Fact]
    public void Seniority_exclusion_matches_whole_words_only()
    {
        Assert.True(ProfileFilter.Evaluate(Job("International Backend Developer (.NET)"), Gabriel(), Cfg()).relevant);
        Assert.False(ProfileFilter.Evaluate(Job("Backend Developer Intern"), Gabriel(), Cfg()).relevant);
    }

    [Fact]
    public void Deal_breaker_java_does_not_drop_javascript_jobs()
    {
        var p = Gabriel();
        p.DealBreakers = new() { "java" };
        Assert.True(ProfileFilter.Evaluate(Job("Backend Developer", "Node, JavaScript and C#"), p, Cfg()).relevant);
        Assert.False(ProfileFilter.Evaluate(Job("Backend Developer", "Java 17 and Spring, some C#"), p, Cfg()).relevant);
    }

    [Fact]
    public void Aspnet_title_is_not_rejected_as_off_stack()
    {
        var p = Gabriel();
        p.CoreSkills = new() { ".NET" };
        Assert.True(ProfileFilter.Evaluate(Job("ASP.NET Developer"), p, Cfg()).relevant);
    }
}
