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
    // Infinite client-level timeout: the real per-call deadline is enforced per request via
    // CancellationTokenSource.CancelAfter(cfg.TimeoutSeconds). Without this, HttpClient's DEFAULT 100s timeout
    // silently overrides the configured timeout — killing any scoring/plan call that needs >100s at 100s.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Reason for the last failed completion (CLI stderr, HTTP status, timeout…). Null on success.
    /// Surfaced in the UI so the user can tell e.g. a Claude usage-limit from a local-model-down.</summary>
    public static string? LastError { get; private set; }

    private static string? Trim(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    public static Task<string?> CompleteAsync(ClaudeConfig cfg, string prompt, CancellationToken ct = default, bool json = false)
        => (cfg.Provider?.Trim().ToLowerInvariant()) switch
        {
            "openai" or "local" or "http" => OpenAiAsync(cfg, prompt, json, ct),
            _ => ClaudeCliAsync(cfg, prompt, ct),
        };

    /// <summary>
    /// Same as <see cref="CompleteAsync(ClaudeConfig,string,CancellationToken)"/> but, for the OpenAI-compatible
    /// backend, streams the model's live "thinking" (its <c>reasoning_content</c> deltas, and any inline
    /// &lt;think&gt;…&lt;/think&gt; in the content) to <paramref name="onReasoning"/> as it arrives — so the UI can show
    /// what the model is reasoning instead of a bare spinner. The return value is still the final answer text.
    /// For the Claude CLI (no token stream in JSON mode) it falls back to the non-streaming call.
    /// </summary>
    public static Task<string?> CompleteAsync(ClaudeConfig cfg, string prompt, IProgress<string>? onReasoning, CancellationToken ct = default, bool json = false)
        => (onReasoning is not null && (cfg.Provider?.Trim().ToLowerInvariant()) is "openai" or "local" or "http")
            ? OpenAiStreamingAsync(cfg, prompt, onReasoning, json, ct)
            : CompleteAsync(cfg, prompt, ct, json);

    /// <summary>
    /// Multi-turn chat with a system prompt and optional per-message images (the Coach view).
    /// OpenAI-compatible backends get a real messages array (system + transcript, images as
    /// base64 content parts) with answer tokens streamed to <paramref name="onDelta"/>; the
    /// Claude CLI gets the transcript flattened into one -p prompt (no token stream — the CLI
    /// reads attached images itself via absolute paths).
    /// </summary>
    public static Task<string?> ChatAsync(ClaudeConfig cfg, string system,
        IReadOnlyList<ChatMessage> messages, IProgress<string>? onDelta, CancellationToken ct = default)
        => (cfg.Provider?.Trim().ToLowerInvariant()) switch
        {
            "openai" or "local" or "http" => OpenAiChatAsync(cfg, system, messages, onDelta, ct),
            _ => ClaudeCliChatAsync(cfg, system, messages, ct),
        };

    /// <summary>Chat sibling of <see cref="OpenAiStreamingAsync"/> with a different streaming contract:
    /// ANSWER deltas go to <paramref name="onDelta"/> (reasoning is swallowed), and the transcript is a
    /// real messages array instead of a single prompt.</summary>
    private static async Task<string?> OpenAiChatAsync(ClaudeConfig cfg, string system,
        IReadOnlyList<ChatMessage> messages, IProgress<string>? onDelta, CancellationToken ct)
    {
        int cap = cfg.MaxTokens > 0 ? cfg.MaxTokens : 4096;
        int promptChars = system.Length + messages.Sum(m => m.Text.Length);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(cfg.BaseUrl) || string.IsNullOrWhiteSpace(cfg.Model))
            { LastError = "no base URL / model set"; return null; }
            string url = cfg.BaseUrl.TrimEnd('/') + "/chat/completions";

            var msgs = new List<object> { new { role = "system", content = system } };
            foreach (var m in messages) msgs.Add(BuildOpenAiMessage(m));
            var body = new Dictionary<string, object?>
            {
                ["model"] = cfg.Model,
                ["messages"] = msgs,
                ["stream"] = true,
                ["temperature"] = 0.3,   // chat: slightly warmer than the 0 used for scoring
                ["max_tokens"] = cap,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(cfg.TimeoutSeconds * 1000);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode}";
                LogCall("openai-chat", cfg.Model, cap, promptChars, "", "", sw.ElapsedMilliseconds, $"HTTP {(int)resp.StatusCode}");
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            var answer = new StringBuilder();      // streamed to the caller
            var reasoning = new StringBuilder();    // swallowed (kept only for diagnostics/fallback)
            int flushed = 0;
            bool inThink = false;
            string carry = "";
            string finish = "";
            string usage = "";
            string? line;

            void Flush()
            {
                if (onDelta is not null && answer.Length > flushed)
                { onDelta.Report(answer.ToString(flushed, answer.Length - flushed)); flushed = answer.Length; }
            }

            while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string data = line[5..].Trim();
                if (data.Length == 0) continue;
                if (data == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    string u = ParseUsage(doc.RootElement);
                    if (u.Length > 0) usage = u;
                    if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                        choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
                    var ch = choices[0];
                    if (ch.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        finish = fr.GetString() ?? finish;
                    if (ch.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                    {
                        string rc = FirstField(delta, "reasoning_content", "reasoning", "thinking");
                        if (rc.Length > 0) reasoning.Append(rc);
                        string c = Field(delta, "content");   // may carry inline <think>…</think>
                        if (c.Length > 0) RouteThink(c, ref inThink, ref carry, answer, reasoning);
                    }
                }
                catch { /* ignore a partial / non-JSON SSE line */ }
                if (answer.Length - flushed >= 16) Flush();   // coalesce UI updates
            }
            if (carry.Length > 0) (inThink ? reasoning : answer).Append(carry);
            Flush();

            string text = StripThink(answer.ToString());
            if (string.IsNullOrWhiteSpace(text)) text = reasoning.ToString().Trim();
            string sizes = $"think_chars={reasoning.Length} ans_chars={answer.Length}";
            if (!string.IsNullOrWhiteSpace(text))
            {
                LastError = null;
                LogCall("openai-chat", cfg.Model, cap, promptChars, finish, usage, sw.ElapsedMilliseconds, "ok " + sizes);
                return text;
            }

            bool truncated = string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase);
            LastError = truncated ? Loc.Instance.T("llm.truncated") : Loc.Instance.T("llm.empty");
            LogCall("openai-chat", cfg.Model, cap, promptChars, finish, usage, sw.ElapsedMilliseconds, (truncated ? "truncated " : "empty ") + sizes);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            LastError = Loc.Instance.F("llm.timeout", cfg.TimeoutSeconds);
            LogCall("openai-chat", cfg.Model, cap, promptChars, "", "", sw.ElapsedMilliseconds, $"timeout {cfg.TimeoutSeconds}s");
            return null;
        }
        catch (Exception ex)
        {
            LastError = FriendlyConnError(cfg.BaseUrl, ex) ?? Trim(ex.Message);
            LogCall("openai-chat", cfg.Model, cap, promptChars, "", "", sw.ElapsedMilliseconds, "error: " + Trim(ex.Message));
            return null;
        }
    }

    /// <summary>Maps one chat turn to the OpenAI message shape — plain string content, or a content
    /// array with base64 image parts when the turn carries screenshots.</summary>
    private static object BuildOpenAiMessage(ChatMessage m)
    {
        if (!m.HasImages) return new { role = m.Role, content = m.Text };
        var parts = new List<object>();
        if (!string.IsNullOrWhiteSpace(m.Text)) parts.Add(new { type = "text", text = m.Text });
        foreach (var p in m.ImagePaths!)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(p); } catch { continue; }
            string mime = Path.GetExtension(p).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "image/png",
            };
            parts.Add(new { type = "image_url", image_url = new { url = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" } });
        }
        return new { role = m.Role, content = parts };
    }

    /// <summary>Flattens system + transcript into one -p prompt for the Claude CLI. Attached images are
    /// referenced by absolute path with an instruction to read them (the CLI's Read tool handles images).</summary>
    private static Task<string?> ClaudeCliChatAsync(ClaudeConfig cfg, string system,
        IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var sb = new StringBuilder(system.TrimEnd());
        sb.AppendLine().AppendLine().AppendLine("== CONVERSATION SO FAR ==");
        foreach (var m in messages)
        {
            sb.AppendLine(m.Role == "assistant" ? "Assistant:" : "User:");
            if (!string.IsNullOrWhiteSpace(m.Text)) sb.AppendLine(m.Text.Trim());
            if (m.HasImages)
            {
                sb.AppendLine("[The user attached image(s). Read each file with your Read tool before answering:]");
                foreach (var p in m.ImagePaths!) sb.AppendLine(p);
            }
            sb.AppendLine();
        }
        sb.AppendLine("Reply to the LAST user message only, as the coach. Do not prefix your reply with \"Assistant:\".");
        return ClaudeCliAsync(cfg, sb.ToString(), ct);
    }

    /// <summary>Best-effort vision-capability check via Ollama's `POST /api/show` "capabilities" array
    /// (recent Ollama returns e.g. ["completion","vision"]). Returns null = unknown (LM Studio has no
    /// /api/show; older Ollama has no capabilities array; runtime offline). Never throws.</summary>
    public static async Task<bool?> DetectVisionAsync(string baseUrl, string model, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model)) return null;
            using var req = new HttpRequestMessage(HttpMethod.Post, OllamaRoot(baseUrl) + "/api/show")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model = model.Trim() }), Encoding.UTF8, "application/json"),
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(5_000);
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            if (doc.RootElement.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
                return caps.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String &&
                    string.Equals(c.GetString(), "vision", StringComparison.OrdinalIgnoreCase));
            return null;
        }
        catch { return null; }
    }

    /// <summary>Runs `claude -p &lt;prompt&gt; --output-format json` and unwraps the "result" envelope.</summary>
    private static async Task<string?> ClaudeCliAsync(ClaudeConfig cfg, string prompt, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
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
            if (p is null) { LastError = "could not start the Claude CLI"; return null; }
            p.StandardInput.Close(); // signal EOF so the CLI doesn't wait for piped stdin

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(cfg.TimeoutSeconds * 1000);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                LastError = $"timeout ({cfg.TimeoutSeconds}s)";
                LogCall("claude-cli", cfg.Model, 0, prompt.Length, "", "", sw.ElapsedMilliseconds, $"timeout {cfg.TimeoutSeconds}s");
                return null;
            }

            string raw = await stdout;
            string err = await stderr;
            try
            {
                using var env = JsonDocument.Parse(raw);
                if (env.RootElement.ValueKind == JsonValueKind.Object &&
                    env.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                {
                    LastError = null;
                    LogCall("claude-cli", cfg.Model, 0, prompt.Length, "", "", sw.ElapsedMilliseconds, "ok");
                    return r.GetString();
                }
            }
            catch { /* not an envelope — return raw text */ }
            if (string.IsNullOrWhiteSpace(raw))
            {
                LastError = Trim(err) ?? $"exit {p.ExitCode}";
                LogCall("claude-cli", cfg.Model, 0, prompt.Length, "", "", sw.ElapsedMilliseconds, "error: " + (LastError ?? "?"));
                return null;
            }
            LastError = null;
            LogCall("claude-cli", cfg.Model, 0, prompt.Length, "", "", sw.ElapsedMilliseconds, "ok (raw)");
            return raw;
        }
        catch (Exception ex)
        {
            LastError = Trim(ex.Message);
            LogCall("claude-cli", cfg.Model, 0, prompt.Length, "", "", sw.ElapsedMilliseconds, "error: " + Trim(ex.Message));
            return null;
        }
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
    private static async Task<string?> OpenAiAsync(ClaudeConfig cfg, string prompt, bool jsonMode, CancellationToken ct)
    {
        int cap = cfg.MaxTokens > 0 ? cfg.MaxTokens : 4096;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(cfg.BaseUrl) || string.IsNullOrWhiteSpace(cfg.Model))
            { LastError = "no base URL / model set"; return null; }
            string url = cfg.BaseUrl.TrimEnd('/') + "/chat/completions";

            var body = new Dictionary<string, object?>
            {
                ["model"] = cfg.Model,
                ["messages"] = new[] { new { role = "user", content = prompt } },
                ["stream"] = false,
                ["temperature"] = 0,
                // Give reasoning models (Gemma 4, Qwen3, DeepSeek-R1) room to emit the answer AFTER thinking —
                // without a cap they often spend the budget reasoning and return empty/truncated content. User-tunable.
                ["max_tokens"] = cap,
            };
            // Constrain output to a syntactically-valid JSON object (standard OpenAI field, honoured by Ollama,
            // LM Studio and llama.cpp) so weak local models can't reply with prose/markdown instead of the object
            // we parse. Opt-in — only JSON-expecting callers set it.
            if (jsonMode) body["response_format"] = new { type = "json_object" };
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(cfg.TimeoutSeconds * 1000);
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode}";
                LogCall("openai", cfg.Model, cap, prompt.Length, "", "", sw.ElapsedMilliseconds, $"HTTP {(int)resp.StatusCode}");
                return null;
            }

            string json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            string usage = ParseUsage(doc.RootElement);
            string finish = "";
            string text = "";
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var c0 = choices[0];
                if (c0.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    finish = fr.GetString() ?? "";
                if (c0.TryGetProperty("message", out var msg))
                {
                    // Prefer the answer; if empty (reasoning model), fall back to its thinking channel
                    // (reasoning_content on vLLM/DeepSeek, reasoning on Ollama, thinking elsewhere) — often carries the JSON.
                    text = StripThink(Field(msg, "content"));
                    if (string.IsNullOrWhiteSpace(text)) text = StripThink(FirstField(msg, "reasoning_content", "reasoning", "thinking"));
                }
            }
            if (!string.IsNullOrWhiteSpace(text))
            {
                LastError = null;
                LogCall("openai", cfg.Model, cap, prompt.Length, finish, usage, sw.ElapsedMilliseconds, "ok");
                return text;
            }

            // 200 OK but nothing usable — almost always a reasoning model that ran out of budget thinking.
            bool truncated = string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase);
            LastError = truncated ? Loc.Instance.T("llm.truncated") : Loc.Instance.T("llm.empty");
            LogCall("openai", cfg.Model, cap, prompt.Length, finish, usage, sw.ElapsedMilliseconds, truncated ? "truncated" : "empty");
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }  // user/caller cancel → propagate
        catch (OperationCanceledException)  // our timeout
        {
            LastError = Loc.Instance.F("llm.timeout", cfg.TimeoutSeconds);
            LogCall("openai", cfg.Model, cap, prompt.Length, "", "", sw.ElapsedMilliseconds, $"timeout {cfg.TimeoutSeconds}s");
            return null;
        }
        catch (Exception ex)
        {
            LastError = FriendlyConnError(cfg.BaseUrl, ex) ?? Trim(ex.Message);
            LogCall("openai", cfg.Model, cap, prompt.Length, "", "", sw.ElapsedMilliseconds, "error: " + Trim(ex.Message));
            return null;
        }
    }

    /// <summary>Streamed sibling of <see cref="OpenAiAsync"/>: sets <c>stream:true</c>, parses the SSE deltas,
    /// forwards reasoning chunks to <paramref name="onReasoning"/>, and returns the assembled answer.</summary>
    private static async Task<string?> OpenAiStreamingAsync(ClaudeConfig cfg, string prompt, IProgress<string> onReasoning, bool json, CancellationToken ct)
    {
        int cap = cfg.MaxTokens > 0 ? cfg.MaxTokens : 4096;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(cfg.BaseUrl) || string.IsNullOrWhiteSpace(cfg.Model))
            { LastError = "no base URL / model set"; return null; }
            string url = cfg.BaseUrl.TrimEnd('/') + "/chat/completions";

            var body = new Dictionary<string, object?>
            {
                ["model"] = cfg.Model,
                ["messages"] = new[] { new { role = "user", content = prompt } },
                ["stream"] = true,
                ["temperature"] = 0,
                ["max_tokens"] = cap,
            };
            if (json) body["response_format"] = new { type = "json_object" };
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(cfg.TimeoutSeconds * 1000);
            // ResponseHeadersRead: start reading as tokens arrive instead of buffering the whole response.
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode}";
                LogCall("openai-stream", cfg.Model, cap, prompt.Length, "", "", sw.ElapsedMilliseconds, $"HTTP {(int)resp.StatusCode}");
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            var answer = new StringBuilder();      // the real reply (think tags routed out)
            var reasoning = new StringBuilder();    // live thinking, surfaced to the caller
            int flushed = 0;
            bool inThink = false;
            string carry = "";
            string finish = "";
            string usage = "";                      // some servers send a usage object in the final chunk
            string? line;

            void Flush()
            {
                if (reasoning.Length > flushed)
                { onReasoning.Report(reasoning.ToString(flushed, reasoning.Length - flushed)); flushed = reasoning.Length; }
            }

            while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string data = line[5..].Trim();
                if (data.Length == 0) continue;
                if (data == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    string u = ParseUsage(doc.RootElement);
                    if (u.Length > 0) usage = u;
                    if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                        choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
                    var ch = choices[0];
                    if (ch.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        finish = fr.GetString() ?? finish;
                    if (ch.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                    {
                        // Thinking channel varies by runtime: reasoning_content (vLLM/DeepSeek), reasoning (Ollama), thinking.
                        string rc = FirstField(delta, "reasoning_content", "reasoning", "thinking");
                        if (rc.Length > 0) reasoning.Append(rc);
                        string c = Field(delta, "content");               // answer — may carry inline <think>…</think>
                        if (c.Length > 0) RouteThink(c, ref inThink, ref carry, answer, reasoning);
                    }
                }
                catch { /* ignore a partial / non-JSON SSE line */ }
                if (reasoning.Length - flushed >= 24) Flush();   // coalesce UI updates
            }
            if (carry.Length > 0) (inThink ? reasoning : answer).Append(carry);
            Flush();

            string text = StripThink(answer.ToString());
            if (string.IsNullOrWhiteSpace(text)) text = reasoning.ToString().Trim();
            // think_chars/ans_chars give a feel for how much went to reasoning vs answer when usage is absent.
            string sizes = $"think_chars={reasoning.Length} ans_chars={answer.Length}";
            if (!string.IsNullOrWhiteSpace(text))
            {
                LastError = null;
                LogCall("openai-stream", cfg.Model, cap, prompt.Length, finish, usage, sw.ElapsedMilliseconds, "ok " + sizes);
                return text;
            }

            bool truncated = string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase);
            LastError = truncated ? Loc.Instance.T("llm.truncated") : Loc.Instance.T("llm.empty");
            LogCall("openai-stream", cfg.Model, cap, prompt.Length, finish, usage, sw.ElapsedMilliseconds, (truncated ? "truncated " : "empty ") + sizes);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            LastError = Loc.Instance.F("llm.timeout", cfg.TimeoutSeconds);
            LogCall("openai-stream", cfg.Model, cap, prompt.Length, "", "", sw.ElapsedMilliseconds, $"timeout {cfg.TimeoutSeconds}s");
            return null;
        }
        catch (Exception ex)
        {
            LastError = FriendlyConnError(cfg.BaseUrl, ex) ?? Trim(ex.Message);
            LogCall("openai-stream", cfg.Model, cap, prompt.Length, "", "", sw.ElapsedMilliseconds, "error: " + Trim(ex.Message));
            return null;
        }
    }

    /// <summary>Splits a streamed content chunk into answer vs. thinking on &lt;think&gt;/&lt;/think&gt; boundaries that may
    /// straddle chunk edges. <paramref name="carry"/> holds a possible partial tag between calls.</summary>
    private static void RouteThink(string chunk, ref bool inThink, ref string carry, StringBuilder answer, StringBuilder reasoning)
    {
        carry += chunk;
        while (carry.Length > 0)
        {
            if (!inThink)
            {
                int open = carry.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                if (open < 0) { int safe = SafeLen(carry, "<think>"); answer.Append(carry, 0, safe); carry = carry[safe..]; break; }
                answer.Append(carry, 0, open); carry = carry[(open + 7)..]; inThink = true;
            }
            else
            {
                int close = carry.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (close < 0) { int safe = SafeLen(carry, "</think>"); reasoning.Append(carry, 0, safe); carry = carry[safe..]; break; }
                reasoning.Append(carry, 0, close); carry = carry[(close + 8)..]; inThink = false;
            }
        }
    }

    /// <summary>Length of <paramref name="text"/> safe to emit now — i.e. excluding any trailing run that could be
    /// the start of <paramref name="tag"/> still being streamed.</summary>
    private static int SafeLen(string text, string tag)
    {
        int max = Math.Min(text.Length, tag.Length - 1);
        for (int k = max; k > 0; k--)
            if (string.Compare(text, text.Length - k, tag, 0, k, StringComparison.OrdinalIgnoreCase) == 0)
                return text.Length - k;
        return text.Length;
    }

    private static string Field(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>First non-empty string field among <paramref name="keys"/> — used to read the thinking channel
    /// across runtimes that name it differently (reasoning_content, reasoning, thinking).</summary>
    private static string FirstField(JsonElement e, params string[] keys)
    {
        foreach (var k in keys) { string v = Field(e, k); if (v.Length > 0) return v; }
        return "";
    }

    /// <summary>Extracts an OpenAI-style usage object as "prompt/completion/total", or "" if absent/empty.</summary>
    private static string ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return "";
        int p = Int(u, "prompt_tokens"), c = Int(u, "completion_tokens"), t = Int(u, "total_tokens");
        return (p == 0 && c == 0 && t == 0) ? "" : $"{p}/{c}/{t}";
        static int Int(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
    }

    /// <summary>One metadata-only diagnostic line per LLM call — no prompt body, no API key.</summary>
    private static void LogCall(string provider, string model, int cap, int promptChars, string finish, string usage, long ms, string outcome)
    {
        string f = string.IsNullOrEmpty(finish) ? "" : $" finish={finish}";
        string u = string.IsNullOrEmpty(usage) ? "" : $" usage(p/c/t)={usage}";
        Diag.Info($"LLM {provider} model={model} max_tokens={cap} prompt_chars={promptChars}{f}{u} dur={ms}ms → {outcome}");
    }

    /// <summary>Removes a leading &lt;think&gt;…&lt;/think&gt; block so reasoning chatter doesn't crowd the JSON parse.</summary>
    private static string StripThink(string s)
        => string.IsNullOrEmpty(s) ? "" : System.Text.RegularExpressions.Regex.Replace(s, "<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    /// <summary>Turns a raw connection failure into an actionable message naming the runtime and what to do
    /// (LM Studio's server must be started + the model loaded; Ollama must be running). Returns null for
    /// errors that aren't connection failures, so the caller keeps the original message.</summary>
    private static string? FriendlyConnError(string baseUrl, Exception ex)
    {
        string msg = (ex.InnerException?.Message ?? "") + " " + ex.Message;
        bool refused = ex is HttpRequestException
            || msg.Contains("refused", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("target machine", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("No connection", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("unreachable", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection", StringComparison.OrdinalIgnoreCase);
        if (!refused) return null;
        string url = (baseUrl ?? "").Trim();
        string key = url.Contains(":1234") ? "llm.refused.lmstudio"
                   : url.Contains(":11434") ? "llm.refused.ollama"
                   : "llm.refused.generic";
        return Loc.Instance.F(key, url);
    }
}
