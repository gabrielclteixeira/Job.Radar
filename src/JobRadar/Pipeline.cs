using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace JobRadar;

public record PipelineResult(List<JobEntity> Jobs, int NewCount, bool Demo);

/// <summary>
/// Orchestrates the flow as a reusable service: derive search queries from the
/// profile → fetch (Go) → store/dedupe (SQLite) → filter by profile → optionally
/// score with Claude CLI → return ranked jobs. Demo mode loads pre-scored sample
/// data and never calls an LLM. Field-agnostic (works for any profession).
/// </summary>
public static class Pipeline
{
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Bump when JobEntity columns change (or cached data must be discarded) so the DB is recreated.</summary>
    private const string SchemaVersion = "6"; // 6: add deterministic keyword base verdict

    public static async Task<PipelineResult> RunAsync(
        UserProfile profile, AppConfig cfg, string root, bool useAi,
        IProgress<string>? log = null, bool demo = false,
        IProgress<JobEntity>? onJob = null, CancellationToken ct = default)
    {
        string R(string p) => Path.IsPathRooted(p) ? p : Path.Combine(root, p);
        void L(string m) => log?.Report(m);

        if (demo)
        {
            L(Loc.Instance.T("pipe.demoLoading"));
            string sample = R(Path.Combine("samples", "jobs-scored.json"));
            var demoJobs = File.Exists(sample)
                ? JsonSerializer.Deserialize<List<JobEntity>>(File.ReadAllText(sample), J) ?? new()
                : new();
            demoJobs = demoJobs.OrderByDescending(j => j.AiScore ?? j.PreScore).ToList();
            L(Loc.Instance.F("pipe.demoLoaded", demoJobs.Count));
            // Pace the demo like a live scan: a brief sweep, then results cascade in one by one.
            // Demo only — no API calls, no cost — purely to showcase the radar/streaming UI.
            await Task.Delay(900, ct);
            int shown = 0;
            foreach (var j in demoJobs)
            {
                onJob?.Report(j);
                int delay = shown < 14 ? 85 : 25; // ease off after the first screenful
                await Task.Delay(delay, ct);
                shown++;
            }
            return new PipelineResult(demoJobs, demoJobs.Count, true);
        }

        // 1) Derive the fetcher config from the profile (queries + location), then fetch.
        string cfgPath = R("fetcher-config.json");
        WriteFetcherConfig(cfgPath, profile, log);
        L(Loc.Instance.T("pipe.fetching"));
        string rawPath = R(cfg.RawJobsPath);
        await FetcherRunner.EnsureJobsAsync(root, cfgPath, rawPath, log, ct);
        var raw = File.Exists(rawPath)
            ? JsonSerializer.Deserialize<List<RawJob>>(File.ReadAllText(rawPath), J) ?? new()
            : new();
        L(Loc.Instance.F("pipe.collected", raw.Count));

        // Optional manual LinkedIn pass.
        string liPath = R(cfg.LinkedInJobsPath);
        if (File.Exists(liPath))
        {
            var li = JsonSerializer.Deserialize<List<LinkedInJob>>(File.ReadAllText(liPath), J) ?? new();
            foreach (var l in li)
            {
                string loc = l.Location ?? "";
                string remote = loc.Contains("Remot", StringComparison.OrdinalIgnoreCase) ? "remote"
                              : loc.Contains("Híbrid", StringComparison.OrdinalIgnoreCase) || loc.Contains("Hybrid", StringComparison.OrdinalIgnoreCase) ? "hybrid" : "";
                raw.Add(new RawJob(l.Title ?? "", l.Company ?? "", loc, remote, l.Url ?? "", l.Description ?? "", "linkedin", ""));
            }
            L($"{li.Count} vagas do LinkedIn fundidas.");
        }

        // Optional paid LinkedIn connector (Apify). Cost is confirmed in the UI before the search runs.
        if (cfg.Apify.Enabled)
        {
            var apify = await ApifyClient.FetchLinkedInJobsAsync(
                cfg.Apify, profile.SearchQueries(), profile.Locations.FirstOrDefault() ?? "", log, ct);
            raw.AddRange(apify);
        }

        // Optional JSearch (RapidAPI) connector — keyed/quota-limited; also confirmed in the UI.
        if (cfg.JSearch.Enabled)
        {
            var jsearch = await JSearchClient.FetchJobsAsync(
                cfg.JSearch, profile.SearchQueries(), profile.Locations.FirstOrDefault() ?? "", log, ct);
            raw.AddRange(jsearch);
        }

        // The SQLite cache has no migrations; if the entity schema changed, recreate it.
        string dbPath = R(cfg.DbPath);
        string marker = dbPath + ".schema";
        if (File.Exists(dbPath) && (!File.Exists(marker) || File.ReadAllText(marker) != SchemaVersion))
        {
            try { File.Delete(dbPath); } catch { /* in use — EnsureCreated will surface it */ }
        }
        using var db = new RadarDb(dbPath);
        await db.Database.EnsureCreatedAsync(ct);
        try { File.WriteAllText(marker, SchemaVersion); } catch { }

        int added = 0;
        foreach (var r in raw)
        {
            ct.ThrowIfCancellationRequested();
            string key = (string.IsNullOrWhiteSpace(r.Url) ? $"{r.Title}|{r.Company}" : r.Url).Trim().ToLowerInvariant();
            if (await db.Jobs.AnyAsync(x => x.Key == key, ct)) continue;

            var e = new JobEntity
            {
                // Fix HTML entities ("&amp;") and source-side mojibake ("EducaciÃ³n" → "Educación").
                Key = key,
                Title = TextClean.Clean(r.Title),
                Company = TextClean.Clean(r.Company),
                Location = TextClean.Clean(r.Location),
                Remote = r.Remote, Url = r.Url,
                Description = TextClean.Clean(r.Description),
                Source = r.Source, PostedAt = r.PostedAt, FirstSeen = DateTime.UtcNow,
                SalaryMin = r.SalaryMin, SalaryMax = r.SalaryMax, SalaryCurrency = r.SalaryCurrency,
            };
            SalaryParser.Apply(e, cfg.Salary);
            var (relevant, preScore, explanation, baseVerdict) = ProfileFilter.Evaluate(e, profile, cfg);
            e.Relevant = relevant;
            e.PreScore = preScore;
            e.PreScoreExplanation = explanation;
            e.BaseVerdict = baseVerdict;
            db.Jobs.Add(e);
            added++;
        }
        await db.SaveChangesAsync(ct);
        L($"{added} novas · {await db.Jobs.CountAsync(j => j.Relevant, ct)} relevantes.");

        // 2) Decide what to score. In AI mode, only the top unscored candidates go to Claude;
        //    everything already classified is remembered from the DB (no re-scoring, no cost).
        var allRelevant = await db.Jobs.Where(j => j.Relevant)
            .OrderByDescending(j => j.AiScore ?? j.PreScore).ThenByDescending(j => j.PostedAt)
            .ToListAsync(ct);

        List<JobEntity> toScore = new();
        if (useAi && cfg.Claude.Enabled)
            toScore = allRelevant.Where(j => j.AiScore == null)
                .OrderByDescending(j => j.PreScore).Take(cfg.ScoreTopN).ToList();
        else
            L("Modo keywords — sem IA. Ranking pelo pré-score.");

        int cached = allRelevant.Count(j => j.AiScore != null);
        if (cached > 0) L($"{cached} vagas já classificadas reaproveitadas (sem novo custo).");

        // Stream everything we already know right away, so the user has something to interact with
        // while the new candidates are still being scored.
        var pending = toScore.ToHashSet();
        foreach (var j in allRelevant.Where(j => !pending.Contains(j)))
            onJob?.Report(j);

        // 3) Score the remaining candidates with Claude and stream each result as it lands.
        if (toScore.Count > 0)
        {
            int floor = profile.SalaryFloorEur > 0 ? profile.SalaryFloorEur : cfg.Salary.FloorEur;
            int target = profile.SalaryTargetEur > 0 ? profile.SalaryTargetEur : cfg.Salary.TargetEur;
            var scorer = new ClaudeScorer(cfg.Claude, profile.ToScoringText(), floor, target);
            string engine = string.Equals(cfg.Claude.Provider, "openai", StringComparison.OrdinalIgnoreCase)
                ? Loc.Instance.F("engine.local", string.IsNullOrWhiteSpace(cfg.Claude.Model) ? "OpenAI-compatible" : cfg.Claude.Model)
                : Loc.Instance.T("engine.claude");
            L(Loc.Instance.F("scoring.with", toScore.Count, engine));
            int i = 0;
            foreach (var j in toScore)
            {
                ct.ThrowIfCancellationRequested();
                var res = await scorer.ScoreAsync(j);
                if (res is not null)
                {
                    j.AiScore = res.Score; j.AiVerdict = res.Verdict;
                    j.AiReasons = JsonSerializer.Serialize(res.Reasons);
                    j.AiRedFlags = JsonSerializer.Serialize(res.RedFlags);
                }
                else j.AiScore = j.PreScore;
                await db.SaveChangesAsync(ct);
                L($"  [{j.AiScore,3}] {j.Title} @ {j.Company}  ({++i}/{toScore.Count})");
                onJob?.Report(j); // stream the freshly-scored job
            }
        }

        // allRelevant holds the same tracked entities, so Ai scores set above are reflected here.
        var ranked = allRelevant
            .OrderByDescending(j => j.AiScore ?? j.PreScore).ThenByDescending(j => j.PostedAt)
            .ToList();
        return new PipelineResult(ranked, added, false);
    }

    /// <summary>Returns the jobs already in the local cache (relevant, ranked) without fetching or scoring.</summary>
    /// <summary>Deletes the cached jobs store (SQLite db + schema marker + WAL/SHM). After this,
    /// "View jobs" shows nothing until a fresh search. Best-effort; pools are flushed so the file unlocks.</summary>
    public static void ClearCache(AppConfig cfg, string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();  // release handles so the .db can be deleted
        string dbPath = Path.IsPathRooted(cfg.DbPath) ? cfg.DbPath : Path.Combine(root, cfg.DbPath);
        foreach (var p in new[] { dbPath, dbPath + ".schema", dbPath + "-wal", dbPath + "-shm" })
            try { if (File.Exists(p)) File.Delete(p); } catch { /* may be locked — best-effort */ }
    }

    public static async Task<PipelineResult> LoadCachedAsync(
        AppConfig cfg, string root, IProgress<JobEntity>? onJob = null, CancellationToken ct = default)
    {
        string dbPath = Path.IsPathRooted(cfg.DbPath) ? cfg.DbPath : Path.Combine(root, cfg.DbPath);
        string marker = dbPath + ".schema";
        // No DB yet, or a stale-schema DB → nothing trustworthy to show.
        if (!File.Exists(dbPath) || !File.Exists(marker) || File.ReadAllText(marker) != SchemaVersion)
            return new PipelineResult(new(), 0, false);

        using var db = new RadarDb(dbPath);
        await db.Database.EnsureCreatedAsync(ct);
        var ranked = await db.Jobs.Where(j => j.Relevant)
            .OrderByDescending(j => j.AiScore ?? j.PreScore).ThenByDescending(j => j.PostedAt)
            .ToListAsync(ct);
        foreach (var j in ranked) onJob?.Report(j);
        return new PipelineResult(ranked, ranked.Count, false);
    }

    /// <summary>Re-scores the cached relevant jobs with the current LLM settings (overwriting prior AI scores),
    /// without fetching again. Use after switching the model/engine in Settings.</summary>
    public static async Task<PipelineResult> RescoreAsync(
        UserProfile profile, AppConfig cfg, string root,
        IProgress<string>? log = null, IProgress<JobEntity>? onJob = null, CancellationToken ct = default)
    {
        void L(string m) => log?.Report(m);
        string dbPath = Path.IsPathRooted(cfg.DbPath) ? cfg.DbPath : Path.Combine(root, cfg.DbPath);
        string marker = dbPath + ".schema";
        if (!File.Exists(dbPath) || !File.Exists(marker) || File.ReadAllText(marker) != SchemaVersion)
        {
            L("Sem vagas guardadas para reclassificar — usa \"Procurar vagas\" primeiro.");
            return new PipelineResult(new(), 0, false);
        }

        using var db = new RadarDb(dbPath);
        await db.Database.EnsureCreatedAsync(ct);
        var allRelevant = await db.Jobs.Where(j => j.Relevant)
            .OrderByDescending(j => j.AiScore ?? j.PreScore).ThenByDescending(j => j.PostedAt).ToListAsync(ct);

        if (!cfg.Claude.Enabled)
        {
            L("IA desativada — nada a reclassificar.");
            foreach (var j in allRelevant) onJob?.Report(j);
            return new PipelineResult(allRelevant, allRelevant.Count, false);
        }

        var toScore = allRelevant.OrderByDescending(j => j.PreScore).Take(cfg.ScoreTopN).ToList();
        var pending = toScore.ToHashSet();
        foreach (var j in allRelevant.Where(j => !pending.Contains(j))) onJob?.Report(j);

        int floor = profile.SalaryFloorEur > 0 ? profile.SalaryFloorEur : cfg.Salary.FloorEur;
        int target = profile.SalaryTargetEur > 0 ? profile.SalaryTargetEur : cfg.Salary.TargetEur;
        var scorer = new ClaudeScorer(cfg.Claude, profile.ToScoringText(), floor, target);
        L(Loc.Instance.F("pipe.rescoring", toScore.Count));
        int i = 0;
        foreach (var j in toScore)
        {
            ct.ThrowIfCancellationRequested();
            var res = await scorer.ScoreAsync(j);
            if (res is not null)
            {
                j.AiScore = res.Score; j.AiVerdict = res.Verdict;
                j.AiReasons = JsonSerializer.Serialize(res.Reasons);
                j.AiRedFlags = JsonSerializer.Serialize(res.RedFlags);
            }
            else j.AiScore = j.PreScore;
            await db.SaveChangesAsync(ct);
            L($"  [{j.AiScore,3}] {j.Title} @ {j.Company}  ({++i}/{toScore.Count})");
            onJob?.Report(j);
        }

        var ranked = allRelevant.OrderByDescending(j => j.AiScore ?? j.PreScore).ThenByDescending(j => j.PostedAt).ToList();
        return new PipelineResult(ranked, ranked.Count, false);
    }

    /// <summary>Overrides queries + location in the fetcher config from the profile, preserving keys/sources.</summary>
    private static void WriteFetcherConfig(string cfgPath, UserProfile profile, IProgress<string>? log)
    {
        try
        {
            JsonObject root = File.Exists(cfgPath)
                ? (JsonNode.Parse(File.ReadAllText(cfgPath)) as JsonObject ?? new JsonObject())
                : new JsonObject();

            root["queries"] = new JsonArray(profile.SearchQueries().Select(q => JsonValue.Create(q)).ToArray());
            root["location"] = profile.Locations.FirstOrDefault() ?? "";

            // Tech-only boards only make sense for tech profiles.
            bool tech = profile.IsTechField();
            root["remotive"] = tech;
            root["remoteok"] = tech;
            if (root["arbeitnow"] is null) root["arbeitnow"] = true;
            if (root["adzuna"] is null) root["adzuna"] = new JsonObject { ["appId"] = "", ["appKey"] = "", ["country"] = "pt" };

            File.WriteAllText(cfgPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            log?.Report($"Pesquisa: {string.Join(", ", profile.SearchQueries())}");
        }
        catch (Exception ex) { log?.Report($"(aviso) não consegui gerar a config do fetcher: {ex.Message}"); }
    }
}
