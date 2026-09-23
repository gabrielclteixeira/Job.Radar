using JobRadar;

namespace JobRadar.Tests;

public class ClaudeCliTests
{
    private static readonly ClaudeConfig Cfg = new() { Provider = "claude-cli", Exe = "claude" };

    [Fact]
    public void Lean_args_disable_tools_mcp_settings_and_sessions()
    {
        var a = LlmClient.BuildCliArgs(Cfg, "hello", null, lean: true);
        Assert.Equal(new[] { "-p", "hello", "--output-format", "json" }, a.Take(4));
        int t = a.IndexOf("--tools");
        Assert.True(t > 0);
        Assert.Equal("", a[t + 1]);                       // no tools at all
        Assert.Contains("--strict-mcp-config", a);
        Assert.Contains("--no-session-persistence", a);
        Assert.Equal("", a[a.IndexOf("--setting-sources") + 1]);
        Assert.DoesNotContain("--add-dir", a);
        Assert.DoesNotContain("--model", a);              // empty model → CLI default
    }

    [Fact]
    public void Images_allow_only_Read_on_their_folders()
    {
        var a = LlmClient.BuildCliArgs(Cfg, "look", new[] { @"C:\data\coach-images" }, lean: true);
        Assert.Equal("Read", a[a.IndexOf("--tools") + 1]);
        Assert.Equal(@"C:\data\coach-images", a[a.IndexOf("--add-dir") + 1]);
    }

    [Fact]
    public void Plain_args_for_old_cli_keep_only_the_basics_and_model()
    {
        var cfg = new ClaudeConfig { Provider = "claude-cli", Exe = "claude", Model = "sonnet" };
        Assert.Equal(new[] { "-p", "x", "--output-format", "json", "--model", "sonnet" }, LlmClient.BuildCliArgs(cfg, "x", null, lean: false));
    }

    [Fact]
    public void Success_envelope_returns_the_result()
    {
        var (text, err) = LlmClient.ParseCliOutput("""{"type":"result","subtype":"success","is_error":false,"result":"ok"}""", "", 0);
        Assert.Equal("ok", text);
        Assert.Null(err);
    }

    [Fact]
    public void Error_envelope_is_an_error_not_an_answer()
    {
        // e.g. subscription usage limit: the message arrives in "result" with is_error=true
        var (text, err) = LlmClient.ParseCliOutput("""{"type":"result","subtype":"success","is_error":true,"result":"Claude AI usage limit reached"}""", "", 1);
        Assert.Null(text);
        Assert.Contains("usage limit", err);
    }

    [Fact]
    public void Nonzero_exit_without_envelope_uses_stderr()
    {
        var (text, err) = LlmClient.ParseCliOutput("", "error: unknown option '--x'", 1);
        Assert.Null(text);
        Assert.Contains("unknown option", err);
    }

    [Fact]
    public void Plain_text_output_is_still_accepted()
    {
        var (text, err) = LlmClient.ParseCliOutput("just text", "", 0);
        Assert.Equal("just text", text);
        Assert.Null(err);
    }
}
