using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace JobRadar;

/// <summary>
/// Single entry point for all LLM calls, so the backend is pluggable. Returns the model's text
/// (callers extract the JSON object they need). Two providers:
///   - "claude-cli": shells out to the local Claude CLI (BYOK, no API key).
///   - "openai":     POSTs to any OpenAI-compatible /chat/completions endpoint — Ollama,
///                   LM Studio, llama.cpp server, etc. Fully local & offline.
/// Any failure returns null so callers can fall back gracefully.
/// </summary>
public static class LlmClient
{
    private static readonly HttpClient Http = new();

    public static Task<string?> CompleteAsync(ClaudeConfig cfg, string prompt, CancellationToken ct = default)
        => (cfg.Provider?.Trim().ToLowerInvariant()) switch
        {
            "openai" or "local" or "http" => OpenAiAsync(cfg, prompt, ct),
            _ => ClaudeCliAsync(cfg, prompt, ct),
        };

    /// <summary>Runs `claude -p &lt;prompt&gt; --output-format json` and unwraps the "result" envelope.</summary>
    private static async Task<string?> ClaudeCliAsync(ClaudeConfig cfg, string prompt, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cfg.Exe,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(prompt);
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("json");

            using var p = Process.Start(psi);
            if (p is null) return null;
            p.StandardInput.Close(); // signal EOF so the CLI doesn't wait for piped stdin

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(cfg.TimeoutSeconds * 1000);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return null; }

            string raw = await stdout;
            _ = await stderr;
            try
            {
                using var env = JsonDocument.Parse(raw);
                if (env.RootElement.ValueKind == JsonValueKind.Object &&
                    env.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                    return r.GetString();
            }
            catch { /* not an envelope — return raw text */ }
            return raw;
        }
        catch { return null; }
    }

    /// <summary>POSTs to an OpenAI-compatible chat-completions endpoint and returns the message content.</summary>
    private static async Task<string?> OpenAiAsync(ClaudeConfig cfg, string prompt, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cfg.BaseUrl) || string.IsNullOrWhiteSpace(cfg.Model)) return null;
            string url = cfg.BaseUrl.TrimEnd('/') + "/chat/completions";

            var body = new
            {
                model = cfg.Model,
                messages = new[] { new { role = "user", content = prompt } },
                stream = false,
                temperature = 0,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(cfg.TimeoutSeconds * 1000);
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString();
            return null;
        }
        catch { return null; }
    }
}
