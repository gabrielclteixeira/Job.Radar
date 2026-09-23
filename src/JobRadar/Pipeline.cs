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

    /// <summary>Jobs scored per model call. Batching amortizes the profile+rubric input and the model's
    /// per-call "thinking" across several jobs — far faster than one call per job on local reasoning models.
    /// On a small context (Ollama's default num_ctx=4096) a big batch can overflow and truncate the output —
    /// the partial-tolerant parse falls those jobs back to the pre-score. Raise the model's context
    /// (OLLAMA_CONTEXT_LENGTH / a Modelfile num_ctx) so larger batches complete cleanly.</summary>
    private const int ScoreBatchSize = 5;

    /// <summary>Scores <paramref name="toScore"/> in batches, persisting + streaming each result as its batch
    /// lands. Shared by RunAsync / RescoreAsync / ScoreRemainingAsync. A job the model omits keeps
    /// <c>AiScore == null</c> (the UI shows its keyword pre-score, labelled KW) so the next run retries it: writing
    /// the pre-score INTO AiScore used to disguise a failed call as an AI score that was never retried.
    /// A batch that comes back empty because the engine errored (or two empty batches in a row) stops the loop
    /// with an exception the UI shows, instead of burning a timeout per remaining batch. Cancellation is
    /// honoured between batches (a batch in flight finishes first).</summary>
    private static async Task ScoreLoopAsync(RadarDb db, ClaudeScorer scorer, List<JobEntity> toScore,
        IProgress<string>? log, IProgress<JobEntity>? onJob, CancellationToken ct)
    {
        int done = 0, emptyInARow = 0;
        for (int start = 0; start < toScore.Count; start += ScoreBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = toScore.GetRange(start, Math.Min(ScoreBatchSize, toScore.Count - start));
            var results = await scorer.ScoreBatchAsync(batch, ct);
            int got = 0;
            for (int k = 0; k < batch.Count; k++)
            {
                var j = batch[k];
                var res = k < results.Count ? results[k] : null;
                if (res is not null)
                {
                    j.AiScore = res.Score; j.AiVerdict = res.Verdict;
                    j.AiReasons = JsonSerializer.Serialize(res.Reasons);
                    j.AiRedFlags = JsonSerializer.Serialize(res.RedFlags);
                    got++;
                }
                string shown = res is null ? $"KW {j.PreScore}" : $"{j.AiScore}";
                log?.Report($"  [{shown,3}] {j.Title} @ {j.Company}  ({++done}/{toScore.Count})");
            }
            await db.SaveChangesAsync(ct);
            foreach (var j in batch) onJob?.Report(j);

            emptyInARow = got == 0 ? emptyInARow + 1 : 0;
            string? err = got == 0 ? LlmClient.LastError : null;
            if (got == 0 && (!string.IsNullOrWhiteSpace(err) || emptyInARow >= 2))
            {
                // Show the rest with their keyword score (still unscored, so retried next run), then surface why.
                foreach (var j in toScore.Skip(start + batch.Count)) onJob?.Report(j);
                throw new InvalidOperationException(Loc.Instance.F("scoring.aborted",
                    string.IsNullOrWhiteSpace(err) ? Loc.Instance.T("llm.empty") : err));
            }
        }
    }

    /// <summary>Opens the cache DB and repairs rows written by the old failure path (pre-score copied into
    /// AiScore with no reasons at all; a real AI result always serializes its reasons, even as "[]"), so those
    /// jobs are scored for real on the next run. Idempotent and cheap.</summary>
    private static async Task<RadarDb> OpenDbAsync(string dbPath, CancellationToken ct)
    {
        var db = new RadarDb(dbPath);
        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE Jobs SET AiScore = NULL, AiVerdict = NULL WHERE AiScore IS NOT NULL AND AiReasons IS NULL", ct);
        return db;
    }

    /// <summary>Reads a JSON list of jobs, tolerating a truncated/corrupt file (logged, treated as empty) so one
    /// bad file can't block every search until it's deleted by hand.</summary>
    private static List<T> ReadJobsFile<T>(string path, IProgress<string>? log)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), J) ?? new(); }
        catch (Exception ex)
        {
            Diag.Error($"unreadable jobs file ignored: {Path.GetFileName(path)}", ex);
            log?.Report(Loc.Instance.F("pipe.badFile", Path.GetFileName(path)));
            return new();
        }
    }

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
        var raw = ReadJobsFile<RawJob>(rawPath, log);
        L(Loc.Instance.F("pipe.collected", raw.Count));

        // Optional manual LinkedIn pass.
        string liPath = R(cfg.LinkedInJobsPath);
        if (File.Exists(liPath))
        {
            var li = ReadJobsFile<LinkedInJob>(liPath, log);
            foreach (var l in li)
            {
                string loc = l.Location ?? "";
                string remote = loc.Contains("Remot", StringComparison.OrdinalIgnoreCase) ? "remote"
                              : loc.Contains("Híbrid", StringComparison.OrdinalIgnoreCase) || loc.Contains("Hybrid", StringComparison.OrdinalIgnoreCase) ? "hybrid" : "";
                raw.Add(new RawJob(l.Title ?? "", l.Company ?? "", loc, remote, l.Url ?? "", l.Description ?? "", "linkedin", ""));
            }
            L(Loc.Instance.F("pipe.linkedinMerged", li.Count));
        }

        // Optional paid LinkedIn connector (Apify). Cost is confirmed in the UI before the search runs.
        if (cfg.Apify.Enabled)
        {
            var apify = await ApifyClient.FetchLinkedInJobsAsync(
                cfg.Apify, profile.RoleQueries(), profile.Locations.FirstOrDefault() ?? "", log, ct);
            raw.AddRange(apify);
        }

        // Optional JSearch (RapidAPI) connector — keyed/quota-limited; also confirmed in the UI.
        if (cfg.JSearch.Enabled)
        {
            var jsearch = await JSearchClient.FetchJobsAsync(
                cfg.JSearch, profile.RoleQueries(), profile.Locations.FirstOrDefault() ?? "", log, ct);
            raw.AddRange(jsearch);
        }

        // Optional keyless remote-jobs sources (free, no key) — Jobicy and Himalayas.
        if (cfg.Jobicy.Enabled)
            raw.AddRange(await JobicyClient.FetchJobsAsync(cfg.Jobicy, log, ct));
        if (cfg.Himalayas.Enabled)
            raw.AddRange(await HimalayasClient.FetchJobsAsync(cfg.Himalayas, log, ct));

        // The SQLite cache has no migrations; if the entity schema changed, recreate it.
        string dbPath = R(cfg.DbPath);
        string marker = dbPath + ".schema";
        // Only stamp the new schema once the old file is really gone: stamping after a failed (locked) delete
        // left the old schema under a "current" marker, so every later run failed on missing columns.
        if (File.Exists(dbPath) && (!File.Exists(marker) || File.ReadAllText(marker) != SchemaVersion))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                try { if (File.Exists(p)) File.Delete(p); } catch { /* in use — checked below */ }
            if (File.Exists(dbPath))
                throw new IOException(Loc.Instance.F("pipe.dbLocked", Path.GetFileName(dbPath)));
        }
        using var db = await OpenDbAsync(dbPath, ct);
        try { File.WriteAllText(marker, SchemaVersion); } catch { }

        // Re-evaluate what's already stored: relevance/pre-score/salary were computed once at insert, so a profile
        // change (new stack, location, deal-breakers) or a filter fix never reached old rows: irrelevant jobs kept
        // showing and newly-relevant ones stayed hidden. Keyword-only and cheap; AI scores are left untouched.
        int reevaluated = 0;
        foreach (var e in await db.Jobs.ToListAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            e.SalaryAnnualEur = null; e.SalaryText = "";
            SalaryParser.Apply(e, cfg.Salary);
            var (rel, pre, expl, bv) = ProfileFilter.Evaluate(e, profile, cfg);
            if (rel != e.Relevant || pre != e.PreScore) reevaluated++;
            e.Relevant = rel; e.PreScore = pre; e.PreScoreExplanation = expl; e.BaseVerdict = bv;
        }
        if (reevaluated > 0) L(Loc.Instance.F("pipe.reevaluated", reevaluated));

        int added = 0;
        // Dedupe within THIS batch too: AnyAsync only sees rows already in the DB, but SaveChanges runs once
        // at the end — so two raw jobs sharing a key (same URL across sources, or Himalayas pagination overlap)
        // would both be added and then violate the unique Key index. Track keys added this run.
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in raw)
        {
            ct.ThrowIfCancellationRequested();
            string key = (string.IsNullOrWhiteSpace(r.Url) ? $"{r.Title}|{r.Company}" : r.Url).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(key) || key == "|") continue;   // nothing to dedupe/identify on
            if (!seenKeys.Add(key)) continue;                              // duplicate within this batch
            if (await db.Jobs.AnyAsync(x => x.Key == key, ct)) continue;   // already persisted from a prior run

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
        L(Loc.Instance.F("pipe.addedRelevant", added, await db.Jobs.CountAsync(j => j.Relevant, ct)));

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
            L(Loc.Instance.T("pipe.keywordMode"));

        int cached = allRelevant.Count(j => j.AiScore != null);
        if (cached > 0) L(Loc.Instance.F("pipe.reused", cached));

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
            string engine = LlmClient.IsLocal(cfg.Claude)
                ? Loc.Instance.F("engine.local", string.IsNullOrWhiteSpace(cfg.Claude.Model) ? "OpenAI-compatible" : cfg.Claude.Model)
                : Loc.Instance.T("engine.claude");
            L(Loc.Instance.F("scoring.with", toScore.Count, engine));
            await ScoreLoopAsync(db, scorer, toScore, log, onJob, ct);
        }

        // allRelevant holds the same tracked entities, so Ai scores set above are reflected here.
        var ranked = allRelevant
            .OrderByDescending(j => j.AiScore ?? j.PreScore).ThenByDescending(j => j.PostedAt)
            .ToList();
        return new PipelineResult(ranked, added, false);
    }

    /// <summary>Deletes the cached jobs store (SQLite db + schema marker + WAL/SHM). After this,
    /// "View jobs" shows nothing until a fresh search. Best-effort; pools are flushed so the file unlocks.</summary>
    public static void ClearCache(AppConfig cfg, string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();  // release handles so the .db can be deleted
        string dbPath = Path.IsPathRooted(cfg.DbPath) ? cfg.DbPath : Path.Combine(root, cfg.DbPath);
        foreach (var p in new[] { dbPath, dbPath + ".schema", dbPath + "-wal", dbPath + "-shm" })
            try { if (File.Exists(p)) File.Delete(p); } catch { /* may be locked — best-effort */ }
    }

    /// <summary>Returns the jobs already in the local cache (relevant, ranked) without fetching or scoring.</summary>
    public static async Task<PipelineResult> LoadCachedAsync(
        AppConfig cfg, string root, IProgress<JobEntity>? onJob = null, CancellationToken ct = default)
    {
        string dbPath = Path.IsPathRooted(cfg.DbPath) ? cfg.DbPath : Path.Combine(root, cfg.DbPath);
        string marker = dbPath + ".schema";
        // No DB yet, or a stale-schema DB → nothing trustworthy to show.
        if (!File.Exists(dbPath) || !File.Exists(marker) || File.ReadAllText(marker) != SchemaVersion)
            return new PipelineResult(new(), 0, false);

        using var db = await OpenDbAsync(dbPath, ct);
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
            L(Loc.Instance.T("empty.noSavedRescore"));
            return new PipelineResult(new(), 0, false);
        }

        using var db = await OpenDbAsync(dbPath, ct);
        var allRelevant = await db.Jobs.Where(j => j.Relevant)
            .OrderByDescending(j => j.AiScore ?? j.PreScore).ThenByDescending(j => j.PostedAt).ToListAsync(ct);

        if (!cfg.Claude.Enabled)
        {
            L(Loc.Instance.T("pipe.aiOff"));
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
        await ScoreLoopAsync(db, scorer, toScore, log, onJob, ct);

        var ranked = allRelevant.OrderByDescending(j => j.AiScore ?? j.PreScore).ThenByDescending(j => j.PostedAt).ToList();
        return new PipelineResult(ranked, ranked.Count, false);
    }

    /// <summary>Resume scoring after a pause: scores only the still-unscored relevant jobs (no fetch, no
    /// re-scoring of done ones), streaming + persisting each. Same loop as <see cref="RescoreAsync"/> but the
    /// candidate set is <c>AiScore == null</c>.</summary>
    public static async Task<PipelineResult> ScoreRemainingAsync(
        UserProfile profile, AppConfig cfg, string root,
        IProgress<string>? log = null, IProgress<JobEntity>? onJob = null, CancellationToken ct = default)
    {
        void L(string m) => log?.Report(m);
        string dbPath = Path.IsPathRooted(cfg.DbPath) ? cfg.DbPath : Path.Combine(root, cfg.DbPath);
        string marker = dbPath + ".schema";
        if (!File.Exists(dbPath) || !File.Exists(marker) || File.ReadAllText(marker) != SchemaVersion)
            return new PipelineResult(new(), 0, false);

        using var db = await OpenDbAsync(dbPath, ct);
        var allRelevant = await db.Jobs.Where(j => j.Relevant)
            .OrderByDescending(j => j.AiScore ?? j.PreScore).ThenByDescending(j => j.PostedAt).ToListAsync(ct);

        // Stream what's already known, then score only the remaining unscored candidates.
        var toScore = cfg.Claude.Enabled
            ? allRelevant.Where(j => j.AiScore == null).OrderByDescending(j => j.PreScore).Take(cfg.ScoreTopN).ToList()
            : new List<JobEntity>();
        var pending = toScore.ToHashSet();
        foreach (var j in allRelevant.Where(j => !pending.Contains(j))) onJob?.Report(j);

        if (toScore.Count > 0)
        {
            int floor = profile.SalaryFloorEur > 0 ? profile.SalaryFloorEur : cfg.Salary.FloorEur;
            int target = profile.SalaryTargetEur > 0 ? profile.SalaryTargetEur : cfg.Salary.TargetEur;
            var scorer = new ClaudeScorer(cfg.Claude, profile.ToScoringText(), floor, target);
            L(Loc.Instance.F("pipe.rescoring", toScore.Count));
            await ScoreLoopAsync(db, scorer, toScore, log, onJob, ct);
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

            root["queries"] = new JsonArray(profile.RoleQueries().Select(q => JsonValue.Create(q)).ToArray());
            root["location"] = profile.Locations.FirstOrDefault() ?? "";

            // Tech-only boards only make sense for tech profiles.
            bool tech = profile.IsTechField();
            root["remotive"] = tech;
            root["remoteok"] = tech;
            if (root["arbeitnow"] is null) root["arbeitnow"] = true;
            if (root["adzuna"] is null) root["adzuna"] = new JsonObject { ["appId"] = "", ["appKey"] = "", ["country"] = "pt" };

            SafeFile.WriteAllText(cfgPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            log?.Report(Loc.Instance.F("pipe.queries", string.Join(", ", profile.RoleQueries())));
        }
        catch (Exception ex) { log?.Report(Loc.Instance.F("pipe.configWarn", ex.Message)); }
    }
}
