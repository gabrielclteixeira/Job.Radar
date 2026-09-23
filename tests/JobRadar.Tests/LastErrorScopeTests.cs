using JobRadar;

namespace JobRadar.Tests;

/// <summary>B4: LlmClient.LastError must belong to the operation that made the call, not to whichever finished last.</summary>
public class LastErrorScopeTests
{
    private static string FakeCli(string name, string message, int delayMs)
    {
        string path = Path.Combine(Path.GetTempPath(), $"jr-fake-{name}-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path,
            "@echo off\r\n" +
            $"ping -n 1 -w {delayMs} 10.255.255.1 >nul\r\n" +     // crude sleep so the two calls overlap
            $"echo {{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"{message}\"}}\r\n" +
            "exit /b 1\r\n");
        return path;
    }

    [Fact]
    public async Task Concurrent_calls_each_see_their_own_error()
    {
        if (!OperatingSystem.IsWindows()) return;   // the fake CLI is a .cmd script
        string a = FakeCli("a", "error-A", 1500), b = FakeCli("b", "error-B", 200);
        try
        {
            async Task<string?> Run(string exe, int readAfterMs)
            {
                var r = await LlmClient.CompleteAsync(new ClaudeConfig { Provider = "claude-cli", Exe = exe, TimeoutSeconds = 30 }, "x");
                Assert.Null(r);
                await Task.Delay(readAfterMs);
                return LlmClient.LastError;
            }
            // B fails first but reads its error only AFTER A has failed too — with one shared static it saw "error-A".
            var results = await Task.WhenAll(Run(a, 0), Run(b, 3000));
            Assert.Equal("error-A", results[0]);
            Assert.Equal("error-B", results[1]);
        }
        finally { File.Delete(a); File.Delete(b); }
    }
}
