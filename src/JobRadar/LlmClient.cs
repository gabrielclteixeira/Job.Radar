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
                // The CLI emits UTF-8; without this it's decoded with the console codepage and
                // mangles accents/em-dashes in verdicts, reasons and company research.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(prompt);
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("json");
            if (!string.IsNullOrWhiteSpace(cfg.Model)) // empty → CLI's configured default
            {
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(cfg.Model);
            }

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

    /// <summary>Lists model ids from an OpenAI-compatible endpoint (`GET {baseUrl}/models`).
    /// Works for Ollama and LM Studio. Returns an empty list if the runtime isn't reachable.</summary>
    public static async Task<List<string>> ListOpenAiModelsAsync(string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        var models = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return models;
            string url = baseUrl.TrimEnd('/') + "/models";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(10_000);
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return models;
            string json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var m in data.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        models.Add(id.GetString()!);
        }
        catch { /* runtime down / unreachable — return what we have */ }
        return models;
    }

    /// <summary>
    /// Downloads a model via Ollama's native API (`POST {root}/api/pull`, streamed progress).
    /// `baseUrl` is the OpenAI-compatible URL (…/v1); we strip `/v1` to reach the Ollama root.
    /// Ollama-only (LM Studio has no pull API). Reports (status, 0–1 fraction); returns true on success.
    /// </summary>
    public static async Task<bool> PullModelAsync(string baseUrl, string model,
        IProgress<(string status, double frac)>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model)) return false;
        try
        {
            string url = OllamaRoot(baseUrl) + "/api/pull";

            var payload = new { name = model.Trim(), stream = true };
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            // ResponseHeadersRead so HttpClient.Timeout doesn't bound the (long) streamed download.
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) { progress?.Report(($"HTTP {(int)resp.StatusCode}", 0)); return false; }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            bool success = false;
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var el = doc.RootElement;
                    if (el.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                    { progress?.Report((err.GetString() ?? "error", 0)); return false; }
                    string status = el.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
                    double frac = 0;
                    if (el.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.Number &&
                        el.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number && t.GetDouble() > 0)
                        frac = c.GetDouble() / t.GetDouble();
                    if (status.Equals("success", StringComparison.OrdinalIgnoreCase)) success = true;
                    progress?.Report((status, frac));
                }
                catch { /* ignore a partial / non-JSON line */ }
            }
            return success;
        }
        catch { return false; }
    }

    /// <summary>Ollama's native API root (strip the OpenAI-compatible "/v1" suffix from the base URL).</summary>
    private static string OllamaRoot(string baseUrl)
    {
        string root = (baseUrl ?? "").Trim().TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root = root[..^3].TrimEnd('/');
        return root;
    }

    /// <summary>Lists locally installed Ollama models with metadata (`GET {root}/api/tags`). Ollama-only.</summary>
    public static async Task<List<OllamaModel>> ListOllamaModelsAsync(string baseUrl, CancellationToken ct = default)
    {
        var list = new List<OllamaModel>();
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return list;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(8_000);
            using var resp = await Http.GetAsync(OllamaRoot(baseUrl) + "/api/tags", cts.Token);
            if (!resp.IsSuccessStatusCode) return list;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var m in models.EnumerateArray())
            {
                string name = Get(m, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                double gb = m.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number
                    ? Math.Round(sz.GetDouble() / 1_000_000_000d, 1) : 0;
                string param = "", quant = "", family = "";
                if (m.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    param = Get(d, "parameter_size");
                    quant = Get(d, "quantization_level");
                    family = Get(d, "family");
                }
                list.Add(new OllamaModel(name, param, quant, gb, family));
            }
        }
        catch { /* runtime down — return what we have */ }
        return list;

        static string Get(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    /// <summary>Removes a locally installed Ollama model (`DELETE {root}/api/delete`). Ollama-only.</summary>
    public static async Task<bool> DeleteOllamaModelAsync(string baseUrl, string name, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(name)) return false;
            using var req = new HttpRequestMessage(HttpMethod.Delete, OllamaRoot(baseUrl) + "/api/delete")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { name = name.Trim() }), Encoding.UTF8, "application/json"),
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(15_000);
            using var resp = await Http.SendAsync(req, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
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
