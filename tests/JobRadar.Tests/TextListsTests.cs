using JobRadar;

namespace JobRadar.Tests;

public class TextListsTests
{
    [Fact]
    public void Split_keeps_commas_inside_brackets()
    {
        var items = TextLists.Split("C# / .NET (ASP.NET Core, Blazor), Go,  Docker ,");
        Assert.Equal(new[] { "C# / .NET (ASP.NET Core, Blazor)", "Go", "Docker" }, items);
    }

    [Fact]
    public void Split_handles_empty_and_newlines()
    {
        Assert.Empty(TextLists.Split(null));
        Assert.Empty(TextLists.Split("  ,  , "));
        Assert.Equal(new[] { "a", "b (x, y)", "c" }, TextLists.Split("a\nb (x, y)\nc"));
    }

    [Fact]
    public void Split_unclosed_bracket_does_not_swallow_next_line()
    {
        Assert.Equal(new[] { "Go (lang", "Rust" }, TextLists.Split("Go (lang\nRust"));
    }

    [Fact]
    public void Repair_rejoins_items_cut_by_the_old_naive_split()
    {
        var broken = new List<string> { "C# / .NET (ASP.NET Core", "Blazor)", "Go", "AI agents & LLM integration (MCP)" };
        Assert.Equal(new[] { "C# / .NET (ASP.NET Core, Blazor)", "Go", "AI agents & LLM integration (MCP)" },
            TextLists.RepairSplitItems(broken));
    }

    [Fact]
    public void Repair_leaves_well_formed_lists_alone_and_keeps_unbalanced_tail()
    {
        var ok = new List<string> { "Porto", "Portugal" };
        Assert.Equal(ok, TextLists.RepairSplitItems(ok));
        Assert.Equal(new[] { "x (never closed, y" }, TextLists.RepairSplitItems(new[] { "x (never closed", "y" }));
    }
}
