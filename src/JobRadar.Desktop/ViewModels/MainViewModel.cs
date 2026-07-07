using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JobRadar.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly string _root;
    private readonly string _profilePath;
    private readonly string _llmSettingsPath;
    private readonly string _uiSettingsPath;
    private readonly string _apifySettingsPath;
    private readonly string _jsearchSettingsPath;
    private readonly string _jobicySettingsPath;
    private readonly string _himalayasSettingsPath;
    private readonly string _planPath;
    private readonly string _planHistoryPath;
    private readonly string _planPartsPath;
    private readonly string _planReasoningPath;
    private readonly string _careerResearchPath;
    private readonly string _companyCachePath;
    private readonly string _briefCachePath;
    private readonly string _coachHistoryPath;
    private readonly string _cvPath;
    private readonly string _cvChatPath;
    private AppConfig _cfg;

    /// <summary>Set by the view: shows a cost-confirmation dialog before a paid (Apify) search.</summary>
    public Func<Task<bool>>? ConfirmCostAsync;
    private UserProfile _profile = new();
    private List<JobVm> _all = new();
    private bool _isDemoProfile; // true while the John Doe sample is loaded → never persisted

    public MainViewModel()
    {
        _root = FindRoot();
        _profilePath = Path.Combine(_root, "profile.json");
        _llmSettingsPath = Path.Combine(_root, "llm-settings.json");
        _uiSettingsPath = Path.Combine(_root, "ui-settings.json");
        _apifySettingsPath = Path.Combine(_root, "apify-settings.json");
        _jsearchSettingsPath = Path.Combine(_root, "jsearch-settings.json");
        _jobicySettingsPath = Path.Combine(_root, "jobicy-settings.json");
        _himalayasSettingsPath = Path.Combine(_root, "himalayas-settings.json");
        _planPath = Path.Combine(_root, "career-plan.json");
        _planHistoryPath = Path.Combine(_root, "career-plan-history.json");
        _planPartsPath = Path.Combine(_root, "career-plan-parts.json");
        _planReasoningPath = Path.Combine(_root, "career-plan.reasoning.txt");
        _careerResearchPath = Path.Combine(_root, "career-research.json");
        _companyCachePath = Path.Combine(_root, "company-reports.json");
        _briefCachePath = Path.Combine(_root, "company-briefs.json");
        _coachHistoryPath = Path.Combine(_root, "coach-history.json");
        _cvPath = Path.Combine(_root, "cv-data.json");
        _cvChatPath = Path.Combine(_root, "cv-chat.json");
        _cfg = LoadConfig();
        ApplyLlmOverride();
        ApplyApifyOverride();
        ApplyJSearchOverride();
        ApplyJobicyOverride();
        ApplyHimalayasOverride();
        LoadUiSettings();
        Loc.Instance.SetPreference(LangModes[Math.Clamp(_languageIndex, 0, 2)]);
        ApplyTheme();
        LoadSavedProfile();
        LoadPlan();
        LoadPlanHistory();
        _companyCache = CompanyCache.Load(_companyCachePath);
        _briefCache = BriefCache.Load(_briefCachePath);
        _coachThreads = CoachHistory.Load(_coachHistoryPath);
        LinkedInImportedCount = LinkedInImport.CountSaved(LinkedInJobsFile);
        _langInitialised = true;
    }

    // ---- navigation (sidebar) ----
    [ObservableProperty] private string _nav = "home";
    public bool IsNavHome => Nav == "home";
    public bool IsNavProfile => Nav == "profile";
    public bool IsNavResults => Nav == "results";
    public bool IsNavResearcher => Nav == "researcher";
    public bool IsNavImprove => Nav == "improve";
    public bool IsNavCoach => Nav == "coach";
    public bool IsNavCv => Nav == "cv";
    public bool IsNavSettings => Nav == "settings";
    partial void OnNavChanged(string value)
    {
        OnPropertyChanged(nameof(IsNavHome)); OnPropertyChanged(nameof(IsNavProfile));
        OnPropertyChanged(nameof(IsNavResults)); OnPropertyChanged(nameof(IsNavResearcher));
        OnPropertyChanged(nameof(IsNavImprove)); OnPropertyChanged(nameof(IsNavCoach));
        OnPropertyChanged(nameof(IsNavCv)); OnPropertyChanged(nameof(IsNavSettings));
    }

    [RelayCommand]
    private async Task Navigate(string page)
    {
        // Guard: leaving Settings with unsaved Save-backed changes → prompt save/discard/cancel.
        if (page != "settings" && SettingsDirty() && ConfirmLeaveSettingsAsync is not null)
        {
            int choice = await ConfirmLeaveSettingsAsync();
            if (choice == 0) return;                                            // cancel — stay put
            if (choice == 1) SaveSettings();                                    // save (persists + re-snapshots)
            else { LoadSettingsFields(); _settingsSnapshot = SettingsSignature(); } // discard — revert fields
        }

        // Leaving the CV editor implicitly commits + saves (profile-form convention).
        if (Nav == "cv" && page != "cv" && _cvDoc is not null) { CommitCvEditors(); SaveCvDoc(false); }

        switch (page)
        {
            case "profile": EditProfile(); break;          // loads the form + shows profile
            case "results":                                  // show loaded jobs, or load from cache if none yet
                if (_all.Count == 0 && !IsScoring) _ = ViewJobs();
                else ShowOnly(results: true);
                break;
            case "improve": ShowOnly(improve: true); _ = RefreshPlanGroundingAsync(); break;  // career-growth area
            case "researcher": OpenResearcher(); break;       // employer-health signals across matched jobs
            case "coach": OpenCoach(); break;                  // grounded chat (applications/salary/interviews)
            case "cv": OpenCvStudio(); break;                  // structured CV editor + templates + assistant
            case "settings": OpenSettings(); break;            // loads settings fields + shows
            default: ShowOnly(welcome: true); break;          // home
        }
    }

    private static string L(string key) => Loc.Instance[key];

    // ---- language ----
    private static readonly string[] LangModes = { "Auto", "Português", "English" };
    public string[] LanguageOptions => new[] { L("opt.lang.auto"), "Português", "English" };
    [ObservableProperty] private int _languageIndex; // 0 Auto, 1 PT, 2 EN
    private bool _langInitialised;

    /// <summary>Raised when the user changes the language — the App rebuilds the window (same VM,
    /// state preserved) so the static {l:T} labels re-render in the new language.</summary>
    public event Action? LanguageChanged;

    partial void OnLanguageIndexChanged(int value)
    {
        Loc.Instance.SetPreference(LangModes[Math.Clamp(value, 0, 2)]);
        SaveUiSettings();
        RefreshLocalizedLists();
        if (_langInitialised) LanguageChanged?.Invoke();
    }

    /// <summary>Re-raise localized list/label properties so they re-read after a language switch.</summary>
    private void RefreshLocalizedLists()
    {
        OnPropertyChanged(nameof(LanguageOptions));
        OnPropertyChanged(nameof(ThemeOptions));
        OnPropertyChanged(nameof(TextSizeOptions));
        OnPropertyChanged(nameof(ThemeToggleLabel));
    }

    // ---- theme ----
    private static readonly string[] ThemeValues = { "System", "Light", "Dark" };
    public string[] ThemeOptions => new[] { L("opt.theme.system"), L("opt.theme.light"), L("opt.theme.dark") };
    [ObservableProperty] private string _themePref = "Dark"; // System | Light | Dark
    public int ThemeIndex
    {
        get { int i = Array.IndexOf(ThemeValues, ThemePref); return i >= 0 ? i : 2; }
        set => ThemePref = ThemeValues[Math.Clamp(value, 0, 2)];
    }
    public bool IsDark => ThemePref != "Light";
    public string ThemeToggleLabel => Loc.Instance.F("theme.toggle", L(ThemeIndex == 0 ? "opt.theme.system" : ThemeIndex == 1 ? "opt.theme.light" : "opt.theme.dark"));
    partial void OnThemePrefChanged(string value)
    {
        ApplyTheme(); SaveUiSettings();
        OnPropertyChanged(nameof(IsDark)); OnPropertyChanged(nameof(ThemeIndex)); OnPropertyChanged(nameof(ThemeToggleLabel));
    }

    [RelayCommand] private void ToggleTheme() => ThemePref = ThemePref == "Light" ? "Dark" : "Light";

    // ---- text size / UI zoom (Definições dropdown + Ctrl +/- / 0 + Ctrl+wheel) ----
    private const double MinZoom = 0.8, MaxZoom = 2.0, ZoomStep = 0.1;
    [ObservableProperty] private double _zoom = 1.0;
    public string ZoomLabel => $"{Zoom * 100:0}%";

    // Friendly presets shown in Definições; index 4 ("custom") is only shown (not chosen)
    // when Ctrl +/- lands on a value between presets.
    private static readonly double[] SizeZooms = { 0.9, 1.0, 1.2, 1.4 };
    public string[] TextSizeOptions => new[]
        { L("opt.size.small"), L("opt.size.normal"), L("opt.size.large"), L("opt.size.larger"), L("opt.size.custom") };
    public int TextSizeIndex
    {
        get { for (int i = 0; i < SizeZooms.Length; i++) if (Math.Abs(SizeZooms[i] - Zoom) < 0.001) return i; return 4; }
        set { if (value >= 0 && value < SizeZooms.Length) Zoom = SizeZooms[value]; }
    }

    partial void OnZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomLabel));
        OnPropertyChanged(nameof(TextSizeIndex));
        SaveUiSettings();
    }

    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(MaxZoom, Math.Round(Zoom + ZoomStep, 2));
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(MinZoom, Math.Round(Zoom - ZoomStep, 2));
    [RelayCommand] private void ZoomReset() => Zoom = 1.0;

    private void ApplyTheme()
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = ThemePref switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void LoadUiSettings()
    {
        try
        {
            if (!File.Exists(_uiSettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_uiSettingsPath));
            if (doc.RootElement.TryGetProperty("theme", out var t) && t.ValueKind == JsonValueKind.String)
                _themePref = t.GetString() ?? "Dark";
            if (doc.RootElement.TryGetProperty("zoom", out var z) && z.TryGetDouble(out var zv))
                _zoom = Math.Clamp(zv, MinZoom, MaxZoom);
            if (doc.RootElement.TryGetProperty("lang", out var lg) && lg.ValueKind == JsonValueKind.String)
            {
                int i = Array.IndexOf(LangModes, lg.GetString());
                _languageIndex = i >= 0 ? i : 0;
            }
            if (doc.RootElement.TryGetProperty("critique", out var cm) && cm.TryGetInt32(out var cmv))
                _critiqueModeIndex = Math.Clamp(cmv, 0, 2);
            if (doc.RootElement.TryGetProperty("pdfProgress", out var pp) && pp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _includeProgressInPdf = pp.GetBoolean();
        }
        catch { /* ignore */ }
    }

    private void SaveUiSettings()
    {
        try
        {
            File.WriteAllText(_uiSettingsPath, JsonSerializer.Serialize(
                new { theme = ThemePref, zoom = Zoom, lang = LangModes[Math.Clamp(LanguageIndex, 0, 2)], critique = CritiqueModeIndex, pdfProgress = IncludeProgressInPdf },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    // ---- view state ----
    [ObservableProperty] private bool _isWelcome = true;
    [ObservableProperty] private bool _isProfile;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isResults;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _exportMsg = "";
    [ObservableProperty] private bool _hasSavedProfile;
    [ObservableProperty] private string _savedProfileLabel = "";
    [ObservableProperty] private bool _isScoring;          // pipeline still running (streaming jobs in)
    [ObservableProperty] private string _scoringStatus = "";
    [ObservableProperty] private bool _paused;             // a scoring run was paused (cancelled, work persisted)
    private CancellationTokenSource? _scoreCts;
    public bool CanPause => IsScoring;
    public bool CanResume => Paused && !IsScoring;
    partial void OnIsScoringChanged(bool value) { OnPropertyChanged(nameof(CanPause)); OnPropertyChanged(nameof(CanResume)); }
    partial void OnPausedChanged(bool value) => OnPropertyChanged(nameof(CanResume));

    // A scoring failure that must OUTLIVE the scanning card: ScoringStatus lives inside the IsScoring card,
    // which hides in the finally, so the message used to vanish. Shown as a persistent, dismissible banner.
    [ObservableProperty] private string _scoringError = "";
    [RelayCommand] private void DismissScoringError() => ScoringError = "";

    /// <summary>Pause the running classification — cancels the run; jobs scored so far are already saved.</summary>
    [RelayCommand]
    private void PauseScoring() => _scoreCts?.Cancel();

    /// <summary>Resume after a pause: scores only the still-unscored jobs (no fetch, no re-score).</summary>
    [RelayCommand]
    private async Task ResumeScoring()
    {
        if (IsScoring) return;
        Log.Clear(); _all = new(); Jobs.Clear(); HasJobs = false; ExportMsg = ""; ScoringError = "";
        Paused = false; ResultsTitle = L("title.jobs"); ScoringStatus = L("scoring.resuming");
        MinScore = 0; IsScoring = true; ShowOnly(results: true); Busy = true;
        _scoreCts = new CancellationTokenSource();
        var logProg = new Progress<string>(m => Dispatcher.UIThread.Post(() => { Log.Add(m); ScoringStatus = m; }));
        var jobProg = new Progress<JobEntity>(j => Dispatcher.UIThread.Post(() => AddStreamed(j)));
        try
        {
            var result = await Pipeline.ScoreRemainingAsync(_profile, _cfg, _root, logProg, jobProg, _scoreCts.Token);
            FinalizeResults(result);
        }
        catch (OperationCanceledException) { Paused = true; ScoringStatus = L("scoring.paused"); }
        catch (Exception ex) { ScoringError = ScoringStatus = Loc.Instance.F("error.generic", ex.Message); Diag.Error("scoring failed", ex); }
        finally { IsScoring = false; Busy = false; }
    }
    [ObservableProperty] private bool _hasJobs;
    [ObservableProperty] private bool _isSettings;
    [ObservableProperty] private bool _isImprove;
    [ObservableProperty] private bool _isResearcher;
    [ObservableProperty] private bool _isCoach;
    [ObservableProperty] private bool _isCv;

    // ---- improve (career plan) ----
    [ObservableProperty] private CareerPlanResult? _plan;
    [ObservableProperty] private bool _isPlanning;
    [ObservableProperty] private bool _isCritiquing;   // plan is shown; the adversarial review is still running
    [ObservableProperty] private string _planStatus = "";
    [ObservableProperty] private string _planError = "";
    // True when the plan failed because the model's reply was cut off at the token cap — surfaces a
    // shortcut button to the token-limit setting. Matched against the same localized string we set.
    public bool PlanErrorIsTokenLimit =>
        !string.IsNullOrEmpty(PlanError) && PlanError.Contains(Loc.Instance.T("llm.truncated"), StringComparison.Ordinal);
    partial void OnPlanErrorChanged(string value) => OnPropertyChanged(nameof(PlanErrorIsTokenLimit));
    // Live transcript of the generation: phase headers + the model's streamed "thinking". Shown while the plan
    // builds (so it's not just a spinner) and kept in a collapsible section below. VM-only — never exported to PDF.
    [ObservableProperty] private string _planReasoning = "";
    public bool HasPlanReasoning => !string.IsNullOrWhiteSpace(PlanReasoning);
    public string PlanReasoningTail => Tail(PlanReasoning, 700);
    partial void OnPlanReasoningChanged(string value)
    { OnPropertyChanged(nameof(HasPlanReasoning)); OnPropertyChanged(nameof(PlanReasoningTail)); }
    /// <summary>Last <paramref name="max"/> chars, trimmed to a line start — the live "thinking" ticker.</summary>
    private static string Tail(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        string cut = s[^max..];
        int nl = cut.IndexOf('\n');
        return nl >= 0 && nl < cut.Length - 1 ? cut[(nl + 1)..] : cut;
    }
    [ObservableProperty] private int _critiqueModeIndex;   // 0 Single, 1 Debate, 2 Revise (persisted)
    // Computed (not a field) so it picks up the current language each time the window is built — the VM is
    // reused across language switches, and a field would freeze the labels at construction-time language.
    public string[] CritiqueModes => new[] { L("improve.mode.single"), L("improve.mode.debate"), L("improve.mode.revise") };
    public bool HasPlan => Plan is not null;
    // !PlanPaused: while paused the ONLY call to action is "Retomar" — a competing fresh "Generate" here would
    // silently drop the interrupted run's captured context (diff, tick carry-over, feed-forward).
    public bool ShowGenerateIntro => !HasPlan && !IsPlanning && !PlanPaused;
    // Critique view-props — let the UI/PDF update when the critique fills in without reassigning Plan.
    public IReadOnlyList<CritiquePoint>? PlanCritique => Plan?.Critique;
    public bool HasPlanCritique => Plan?.HasCritique == true;
    public bool PlanRevised => Plan?.Revised == true;
    public string PlanCaveat => Plan?.CritiqueCaveat ?? "";
    private void NotifyCritique()
    {
        OnPropertyChanged(nameof(PlanCritique)); OnPropertyChanged(nameof(HasPlanCritique));
        OnPropertyChanged(nameof(PlanRevised)); OnPropertyChanged(nameof(PlanCaveat));
    }
    partial void OnPlanChanged(CareerPlanResult? value)
    {
        OnPropertyChanged(nameof(HasPlan)); OnPropertyChanged(nameof(ShowGenerateIntro));
        NotifyCritique();
        SubscribePlanItems(value);   // keep progress live + persist as the user ticks items off
        NotifyProgress();
    }
    partial void OnIsPlanningChanged(bool value)
    { OnPropertyChanged(nameof(ShowGenerateIntro)); OnPropertyChanged(nameof(CanPausePlan)); OnPropertyChanged(nameof(CanResumePlan)); }
    // CanResumePlan too: pausing DURING the critique raises PlanPaused while IsCritiquing is still true, so the
    // "Retomar" card only appears when this final IsCritiquing=false re-notifies it.
    partial void OnIsCritiquingChanged(bool value)
    { OnPropertyChanged(nameof(CanPausePlan)); OnPropertyChanged(nameof(CanResumePlan)); }
    partial void OnCritiqueModeIndexChanged(int value) => SaveUiSettings();

    // ---- living document: checklist progress, plan-to-plan diff, growth history ----
    public bool HasPlanProgress => Plan?.HasTrackable == true;
    public string PlanProgressText => Plan is { HasTrackable: true } p ? $"{p.DoneCount}/{p.TrackableCount}" : "";
    public int PlanProgressPercent => Plan?.ProgressPercent ?? 0;
    public bool PlanComplete => Plan?.IsComplete == true;
    private void NotifyProgress()
    {
        OnPropertyChanged(nameof(HasPlanProgress)); OnPropertyChanged(nameof(PlanProgressText));
        OnPropertyChanged(nameof(PlanProgressPercent)); OnPropertyChanged(nameof(PlanComplete));
    }

    // The "what changed since last time" strip — recomputed on every regenerate, empty on a first plan.
    [ObservableProperty] private PlanDiff? _planChanges;
    public bool HasPlanChanges => PlanChanges?.HasChanges == true && HasPlan;
    partial void OnPlanChangesChanged(PlanDiff? value) => OnPropertyChanged(nameof(HasPlanChanges));

    // Export choice: whether the shared PDF carries the user's personal checklist progress (default off).
    [ObservableProperty] private bool _includeProgressInPdf;
    partial void OnIncludeProgressInPdfChanged(bool value) => SaveUiSettings();

    public ObservableCollection<PlanSnapshot> PlanHistory { get; } = new();
    public bool HasPlanHistory => PlanHistory.Count > 0;
    private readonly List<PlanSnapshot> _planHistoryStore = new();
    private const int PlanHistoryCap = 24;

    /// <summary>Wire each checklist item so ticking it persists the plan and refreshes the progress bar.</summary>
    private void SubscribePlanItems(CareerPlanResult? plan)
    {
        if (plan is null) return;
        foreach (var g in plan.SkillGaps) { g.PropertyChanged -= OnPlanItemChanged; g.PropertyChanged += OnPlanItemChanged; }
        foreach (var s in plan.Steps) { s.PropertyChanged -= OnPlanItemChanged; s.PropertyChanged += OnPlanItemChanged; }
    }
    private void OnPlanItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SkillGap.Done)) return;
        NotifyProgress();
        RefreshFocus();   // a ticked-off gap shouldn't stay the "do one thing" focus
        SavePlan();
    }

    // ---- generation + pause/resume ----
    private CancellationTokenSource? _planCts;
    [ObservableProperty] private bool _planPaused;   // a generation was paused (cancelled mid-run; parts persisted)
    public bool CanPausePlan => IsPlanning || IsCritiquing;
    public bool CanResumePlan => PlanPaused && !IsPlanning && !IsCritiquing;
    partial void OnPlanPausedChanged(bool value)
    { OnPropertyChanged(nameof(CanResumePlan)); OnPropertyChanged(nameof(ShowGenerateIntro)); }

    // Run context preserved across a pause so resume feeds the model the SAME inputs → the parts cache matches
    // and the living-document diff still works.
    private PlanSnapshot? _pendingPrevSnap;
    private HashSet<string> _pendingPrevDoneKeys = new(StringComparer.Ordinal);
    private string? _pendingCompletedContext;
    private string? _pendingMarket;
    private bool _pendingActive;

    [RelayCommand] private void PausePlan() => _planCts?.Cancel();
    [RelayCommand] private Task GeneratePlan() => RunPlanAsync(resume: false);
    [RelayCommand] private Task ResumePlan() => RunPlanAsync(resume: true);

    private async Task RunPlanAsync(bool resume)
    {
        if (IsPlanning || IsCritiquing) return;
        IsPlanning = true;   // claim the run BEFORE the first await, so a second click can't start a parallel one

        // Ground the plan in the user's own scored jobs (strong-fit only — poisoning-aware) and refresh the panel.
        var signal = await BuildMarketSignalAsync();

        if (!_pendingActive)
        {
            // Capture the plan being replaced (for carry-over of ticks, feed-forward, and the diff). Captured
            // ONCE per generation attempt-series: a paused or FAILED run keeps this context (Plan is null by
            // then), so a later attempt still diffs against the real previous plan. Cleared on success.
            var prevPlan = Plan;
            _pendingPrevSnap = prevPlan is not null ? PlanSnapshot.From(prevPlan) : null;
            _pendingPrevDoneKeys = new HashSet<string>(
                (prevPlan?.DoneLabels ?? Enumerable.Empty<string>()).Select(PlanDiff.Key), StringComparer.Ordinal);
            _pendingCompletedContext = prevPlan is not null && prevPlan.DoneLabels.Any()
                ? string.Join("\n", prevPlan.DoneLabels.Select(x => "- " + x)) : null;
            _pendingActive = true;
        }
        // Pin the market context: RESUME must feed the model the SAME inputs, or the parts-cache signature
        // changes and the completed parts are silently re-run. A fresh attempt re-pins from the current corpus.
        if (!resume || _pendingMarket is null)
            _pendingMarket = signal.HasData ? signal.ToMarketContext() : MarketContext();
        var prevSnap = _pendingPrevSnap;
        var prevDoneKeys = _pendingPrevDoneKeys;
        string? completedContext = _pendingCompletedContext;
        string market = _pendingMarket;
        void CarryOverDone(CareerPlanResult target)
        {
            if (prevDoneKeys.Count == 0) return;
            foreach (var g in target.SkillGaps) if (prevDoneKeys.Contains(PlanDiff.Key(g.Skill))) g.Done = true;
            foreach (var s in target.Steps) if (prevDoneKeys.Contains(PlanDiff.Key(s.Title))) s.Done = true;
        }

        PlanError = ""; Plan = null; PlanChanges = null; PlanPaused = false;
        PlanStatus = L(resume ? "plan.resuming" : "plan.preparing");
        PlanReasoning = "";
        _planCts = new CancellationTokenSource();
        Diag.Info($"plan: {(resume ? "resume" : "generate")} start (engine={_cfg.Claude.Provider} model={_cfg.Claude.Model} critique={(CritiqueMode)CritiqueModeIndex} done_carried={prevDoneKeys.Count} strongJobs={signal.StrongCount})");
        var progress = new Progress<string>(m => { Diag.Info("plan: " + m); Dispatcher.UIThread.Post(() => { PlanStatus = m; PlanReasoning += $"\n▸ {m}\n"; }); });
        var reasoning = new Progress<string>(d => Dispatcher.UIThread.Post(() => PlanReasoning += d));
        try
        {
            var (result, error) = await CareerPlan.GenerateAsync(_cfg.Claude, _profile, market, _careerResearchPath, progress, reasoning, completedContext, _planPartsPath, _planCts.Token);
            if (result is null)
            {
                PlanError = string.IsNullOrWhiteSpace(error) ? L("plan.error.insufficient") : error;
                Diag.Warn("plan: generate failed — " + PlanError);
                return;
            }

            CarryOverDone(result); AnnotateGaps(result, signal);
            Plan = result; SavePlan(); BuildGrounding(result, signal);
            // Show the plan immediately, then red-team it (the plan stays visible with a slim indicator).
            IsPlanning = false; IsCritiquing = true;
            var critiqued = await CareerPlan.CritiqueAsync(_cfg.Claude, _profile, result, (CritiqueMode)CritiqueModeIndex, progress, reasoning, _planCts.Token);
            CarryOverDone(critiqued); AnnotateGaps(critiqued, signal);   // Revise may return a fresh instance
            Plan = critiqued; NotifyCritique(); SavePlan(); BuildGrounding(critiqued, signal);

            // Archive the previous plan, surface what changed, and finish the run (clear resume state + parts cache).
            PlanChanges = PlanDiff.Between(prevSnap, critiqued);
            if (prevSnap is not null) ArchiveSnapshot(prevSnap);
            _pendingActive = false; _pendingPrevSnap = null; _pendingCompletedContext = null; _pendingMarket = null;
            _pendingPrevDoneKeys = new HashSet<string>(StringComparer.Ordinal);
            CareerPlan.ClearParts(_planPartsPath);
            Diag.Info("plan: done" + (critiqued.Revised ? " (revised)" : "") + (PlanChanges?.HasChanges == true ? " (changed)" : ""));
        }
        catch (OperationCanceledException)
        {
            PlanPaused = true; PlanStatus = L("plan.paused"); Diag.Info("plan: paused (resumable — completed parts kept)");
        }
        catch (Exception ex) { PlanError = Loc.Instance.F("error.generic", ex.Message); Diag.Error("plan: generate threw", ex); }
        finally { IsPlanning = false; IsCritiquing = false; SaveReasoningRecord(); }
    }

    // ---- grounding in the user's own jobs (Part B) + skills radar (Part C) ----
    [ObservableProperty] private JobMarketSignal? _planMarket;
    public bool HasDemand => PlanMarket?.HasData == true;
    public bool DemandThin => PlanMarket?.Thin == true;
    public string DemandHeader => PlanMarket is { HasData: true } m ? Loc.Instance.F("improve.demand.header", m.StrongCount, m.TotalCount) : "";
    partial void OnPlanMarketChanged(JobMarketSignal? value)
    { OnPropertyChanged(nameof(HasDemand)); OnPropertyChanged(nameof(DemandThin)); OnPropertyChanged(nameof(DemandHeader)); }

    [ObservableProperty] private IReadOnlyList<Controls.SkillAxis>? _radarAxes;
    public bool HasRadar => RadarAxes is { Count: >= 3 };
    partial void OnRadarAxesChanged(IReadOnlyList<Controls.SkillAxis>? value) => OnPropertyChanged(nameof(HasRadar));

    [ObservableProperty] private SkillGap? _focusGap;
    public bool HasFocusGap => FocusGap is not null;
    partial void OnFocusGapChanged(SkillGap? value) => OnPropertyChanged(nameof(HasFocusGap));

    /// <summary>Analyse the scored-job corpus into a demand signal and publish it to the panel. Uses the loaded
    /// jobs (`_all`) when present, else pulls the scored jobs straight from the cache DB — so Grow's grounding
    /// works even when opened without first visiting Results. No cached jobs → empty signal → panel/radar hide.</summary>
    private async Task<JobMarketSignal> BuildMarketSignalAsync(CancellationToken ct = default)
    {
        List<JobPosting> jobs;
        if (_all.Count > 0)
            jobs = _all.Select(v => new JobPosting(
                v.Entity.Title ?? "", v.Entity.Description ?? "", v.Score, v.Company ?? "", v.Entity.Url ?? "")).ToList();
        else
        {
            try
            {
                var res = await Pipeline.LoadCachedAsync(_cfg, _root, null, ct);
                jobs = res.Jobs.Select(j => new JobPosting(
                    j.Title ?? "", j.Description ?? "", j.FinalScore, j.Company ?? "", j.Url ?? "")).ToList();
            }
            catch { jobs = new(); }
        }
        var signal = JobMarket.Analyze(jobs, _profile);
        PlanMarket = signal;
        return signal;
    }

    /// <summary>Rebuild the demand panel + (if a plan is loaded) its grounding chips, radar and focus — e.g. when
    /// the Grow view is opened after a restart.</summary>
    private async Task RefreshPlanGroundingAsync()
    {
        var signal = await BuildMarketSignalAsync();
        if (Plan is not null) { AnnotateGaps(Plan, signal); BuildGrounding(Plan, signal); }
        else { RadarAxes = null; FocusGap = null; }
    }

    private static void AnnotateGaps(CareerPlanResult plan, JobMarketSignal signal)
    {
        foreach (var g in plan.SkillGaps) g.CorpusHits = signal.FitDelta(g.Skill);
    }

    /// <summary>Build the radar (top demanded skills you have + the plan's gaps you lack) and the "do one thing"
    /// focus (the highest-grounded gap).</summary>
    private void BuildGrounding(CareerPlanResult plan, JobMarketSignal signal)
    {
        // Skills you HAVE that recur in your matched jobs — these carry real demand data (both polygons non-zero).
        var have = new List<Controls.SkillAxis>();
        foreach (var d in signal.SkillDemand.Take(6))
            have.Add(new Controls.SkillAxis(Shorten(d.Skill), d.Pct, d.IsCore ? 1.0 : 0.6));
        // The plan's gaps you LACK — grounded demand from the corpus (0 for a web-only gap).
        var gaps = new List<Controls.SkillAxis>();
        foreach (var g in plan.SkillGaps.Take(3).Where(g => !string.IsNullOrWhiteSpace(g.Skill)))
        {
            double demand = signal.StrongCount > 0 ? (double)g.CorpusHits / signal.StrongCount : 0;
            gaps.Add(new Controls.SkillAxis(Shorten(g.Skill), demand, 0.0));
        }
        var axes = have.Concat(gaps).ToList();
        // Only render the radar when there's real demand to plot: at least a couple of skills you have that
        // appear in your matched jobs. A gap-only radar (no strong-fit corpus) is all-zero and meaningless.
        RadarAxes = (signal.HasData && have.Count >= 2 && axes.Count >= 3) ? axes : null;
        RefreshFocus();
    }

    /// <summary>The "do one thing" callout: the NOT-yet-done gap best grounded in the user's own jobs.
    /// Re-picked when the user ticks a gap off, so the focus always points at remaining work.</summary>
    private void RefreshFocus() => FocusGap = Plan?.SkillGaps
        .Where(g => !g.Done && !string.IsNullOrWhiteSpace(g.Skill))
        .OrderByDescending(g => g.CorpusHits)
        .FirstOrDefault();

    private static string Shorten(string s) => s.Length > 16 ? s[..15] + "…" : s;

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* opening is best-effort */ }
    }

    /// <summary>A compact summary of the jobs already scored, fed to the plan as market context.</summary>
    private string MarketContext()
    {
        if (_all.Count == 0) return "";
        var top = _all.OrderByDescending(v => v.Score).Take(12).ToList();
        var titles = top.Select(v => $"{v.Title} @ {v.Company} (score {v.Score})");
        int strong = _all.Count(v => v.Score >= 70);
        return $"{_all.Count} vagas vistas, {strong} com forte fit (≥70). Topo:\n- " + string.Join("\n- ", titles);
    }

    private void LoadPlan()
    {
        try
        {
            if (!File.Exists(_planPath)) return;
            var p = JsonSerializer.Deserialize<CareerPlanResult>(File.ReadAllText(_planPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (p is not null) Plan = p;
            // Restore the last run's reasoning so the in-app panel + Copy/Save survive a restart.
            if (File.Exists(_planReasoningPath)) PlanReasoning = File.ReadAllText(_planReasoningPath);
        }
        catch { /* ignore a bad plan file */ }
    }

    private void SavePlan()
    {
        if (Plan is null || _isDemoProfile) return; // don't persist a plan built from the sample profile
        try { File.WriteAllText(_planPath, JsonSerializer.Serialize(Plan, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* best-effort */ }
        try { if (!string.IsNullOrWhiteSpace(PlanReasoning)) File.WriteAllText(_planReasoningPath, PlanReasoning); }
        catch { /* best-effort */ }
    }

    // ---- growth history (each regenerate archives the plan it replaced) ----
    private void LoadPlanHistory()
    {
        try
        {
            if (!File.Exists(_planHistoryPath)) return;
            var list = JsonSerializer.Deserialize<List<PlanSnapshot>>(File.ReadAllText(_planHistoryPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (list is null) return;
            _planHistoryStore.Clear();
            _planHistoryStore.AddRange(list);
            RefreshPlanHistory();
        }
        catch { /* ignore a bad history file */ }
    }

    private void ArchiveSnapshot(PlanSnapshot snap)
    {
        if (_isDemoProfile) return;
        _planHistoryStore.Insert(0, snap);   // newest first
        if (_planHistoryStore.Count > PlanHistoryCap) _planHistoryStore.RemoveRange(PlanHistoryCap, _planHistoryStore.Count - PlanHistoryCap);
        RefreshPlanHistory();
        try { File.WriteAllText(_planHistoryPath, JsonSerializer.Serialize(_planHistoryStore, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* best-effort */ }
    }

    private void RefreshPlanHistory()
    {
        PlanHistory.Clear();
        foreach (var s in _planHistoryStore) PlanHistory.Add(s);
        OnPropertyChanged(nameof(HasPlanHistory));
    }

    [RelayCommand]
    private void ClearPlanHistory()
    {
        _planHistoryStore.Clear();
        RefreshPlanHistory();
        try { if (File.Exists(_planHistoryPath)) File.Delete(_planHistoryPath); }
        catch { /* best-effort */ }
    }

    // ---- search & filters (Vagas) ----
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _emptyMessage = "";
    public ObservableCollection<JobFilter> Filters { get; } = new();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void AddFilter()
    {
        Filters.Add(new JobFilter { Changed = ApplyFilter });
        ApplyFilter();
    }

    [RelayCommand]
    private void RemoveFilter(JobFilter f)
    {
        if (f is null) return;
        Filters.Remove(f);
        ApplyFilter();
    }

    // ---- settings (LLM backend) ----
    [ObservableProperty] private bool _useLocalModel;      // false = Claude CLI, true = OpenAI-compatible local
    [ObservableProperty] private string _llmBaseUrl = "";
    [ObservableProperty] private string _llmModel = "";
    [ObservableProperty] private string _llmApiKey = "";
    [ObservableProperty] private int _llmMaxTokens = 4096;   // response cap for local models (raise if cut off)
    [ObservableProperty] private int _llmTimeoutSeconds = 300; // per-call timeout; reasoning models are slow
    [ObservableProperty] private bool _highlightMaxTokens;   // briefly flashes the field when jumped to from the error
    [ObservableProperty] private string _claudeExe = "claude";

    // Machine-aware suggestion for the response token cap: bigger budgets let reasoning models finish, but cost
    // latency — so scale with RAM (a proxy for how much compute the machine can spend). Always a multiple of 1024.
    // Reasoning models (Gemma/Qwen/DeepSeek) spend most of the budget "thinking" before the answer, so the cap
    // needs real headroom — these tiers leave room to think AND emit on a machine of the given size.
    public int RecommendedMaxTokens =>
        RecommendedRamGb >= 32 ? 24576 : RecommendedRamGb >= 16 ? 16384 : RecommendedRamGb >= 8 ? 8192 : 4096;
    public string MaxTokensRecLabel => RecommendedRamGb > 0
        ? Loc.Instance.F("settings.maxTokens.rec", RecommendedRamGb, RecommendedMaxTokens)
        : Loc.Instance.F("settings.maxTokens.recNoRam", RecommendedMaxTokens);
    [RelayCommand] private void UseRecommendedMaxTokens() => LlmMaxTokens = RecommendedMaxTokens;

    /// <summary>Raised by <see cref="GoToTokenSettingCommand"/> so the View scrolls the token-limit field into view.</summary>
    public event Action? ScrollToMaxTokensRequested;

    /// <summary>Opens Settings, scrolls to the token-limit field and flashes it — the shortcut from the
    /// "reply was cut off" error in Grow.</summary>
    [RelayCommand]
    private async Task GoToTokenSetting()
    {
        await Navigate("settings");
        if (!IsSettings) return;   // user cancelled an unsaved-changes prompt
        ScrollToMaxTokensRequested?.Invoke();
        HighlightMaxTokens = true;
        await Task.Delay(2800);
        HighlightMaxTokens = false;
    }

    // LinkedIn via Apify (paid connector)
    [ObservableProperty] private bool _useApify;
    [ObservableProperty] private string _apifyToken = "";
    [ObservableProperty] private string _apifyActor = "";
    [ObservableProperty] private string _apifyMax = "50";
    [ObservableProperty] private string _apifyStatus = "";
    public ObservableCollection<string> ApifyActorOptions { get; } = new();

    // JSearch connector — keyed, quota-limited. Two providers of the same API.
    [ObservableProperty] private bool _useJSearch;
    [ObservableProperty] private int _jSearchProviderIndex;   // 0 OpenWeb Ninja (direct), 1 RapidAPI
    [ObservableProperty] private string _jSearchKey = "";
    [ObservableProperty] private string _jSearchCountry = "pt";
    [ObservableProperty] private string _jSearchMax = "20";
    public string[] JSearchProviders { get; } = { "OpenWeb Ninja", "RapidAPI" };
    public string JSearchKeyUrl => JSearchProviderIndex == 1
        ? "https://rapidapi.com/letscrape-6bRBa3QguO5/api/jsearch"
        : "https://app.openwebninja.com/";
    partial void OnJSearchProviderIndexChanged(int value) => OnPropertyChanged(nameof(JSearchKeyUrl));

    // Keyless remote-jobs sources (free, no key) — Jobicy + Himalayas.
    [ObservableProperty] private bool _useJobicy;
    [ObservableProperty] private int _jobicyRegionIndex;   // 0 Europe, 1 Portugal, 2 Any
    [ObservableProperty] private string _jobicyMax = "50";
    [ObservableProperty] private bool _useHimalayas;
    [ObservableProperty] private string _himalayasMax = "40";
    public string[] JobicyRegions => new[] { L("region.europe"), L("region.portugal"), L("region.any") };
    private static readonly string[] JobicyGeoValues = { "europe", "portugal", "" };

    // local model manager (Ollama)
    [ObservableProperty] private string _modelToPull = "";
    [ObservableProperty] private bool _isPullingModel;
    [ObservableProperty] private string _modelDownloadStatus = "";
    [ObservableProperty] private double _pullProgress;
    [ObservableProperty] private bool _ollamaReachable = true;
    [ObservableProperty] private bool _isInstallingOllama;
    [ObservableProperty] private string _ollamaInstallStatus = "";
    [ObservableProperty] private double _ollamaInstallProgress;
    public ObservableCollection<OllamaModelVm> InstalledModels { get; } = new();
    public bool HasInstalledModels => InstalledModels.Count > 0;
    public string[] SuggestedModels { get; } = { "llama3.2", "qwen2.5", "phi3.5", "gemma2", "mistral", "llama3.1", "llama3.2-vision" };

    // Machine-aware "good first model" recommendation (RAM-based; GC reports ≈ physical RAM on desktop).
    private static readonly long RecommendedRamGb =
        (long)Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024d * 1024 * 1024));
    public string RecommendedTag => RecommendedRamGb >= 16 ? "gemma4:12b" : RecommendedRamGb >= 8 ? "gemma4:e4b" : "gemma4:e2b";
    public string RecommendedLabel => RecommendedRamGb > 0
        ? Loc.Instance.F("models.recommend", RecommendedRamGb, RecommendedTag)
        : Loc.Instance.F("models.recommendNoRam", RecommendedTag);

    /// <summary>True once the recommended model is already on disk — hides the install button, keeps the message.</summary>
    public bool RecommendedInstalled => InstalledModels.Any(m =>
        string.Equals(m.Name, RecommendedTag, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(m.Name, RecommendedTag + ":latest", StringComparison.OrdinalIgnoreCase));

    /// <summary>One-click install the recommended model (reuses the Ollama pull path: progress + active).</summary>
    [RelayCommand]
    private async Task InstallRecommended()
    {
        if (IsPullingModel) return;
        ModelToPull = RecommendedTag;
        await DownloadModel();
    }

    /// <summary>One-click install of the Ollama runtime (Windows): download the official setup, run it
    /// silently, wait for the local server, then pull the machine-recommended model if none is installed.
    /// On macOS/Linux it opens the download page (manual install). Key-free; our own installer stays small.</summary>
    [RelayCommand]
    private async Task InstallOllama()
    {
        if (IsInstallingOllama || IsPullingModel) return;

        if (!OperatingSystem.IsWindows())   // macOS / Linux: official page, manual install
        {
            OpenUrl(OllamaInstaller.WindowsSetupUrl.Replace("/OllamaSetup.exe", ""));
            OllamaInstallStatus = L("ollama.manual");
            return;
        }

        IsInstallingOllama = true; OllamaInstallProgress = 0; OllamaInstallStatus = "";
        try
        {
            // Already running? Skip the download.
            if (await PingOllama())
            {
                OllamaReachable = true; OllamaInstallStatus = L("ollama.already");
                await RefreshInstalledModels();
            }
            else
            {
                OllamaInstallStatus = L("ollama.downloading");
                string dest = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");
                var progress = new Progress<double>(f => Dispatcher.UIThread.Post(() =>
                {
                    OllamaInstallProgress = f;
                    OllamaInstallStatus = Loc.Instance.F("ollama.downloadingPct", $"{f * 100:0}%");
                }));
                if (!await OllamaInstaller.DownloadWindowsSetupAsync(dest, progress))
                {
                    OllamaInstallStatus = L("ollama.failed");
                    OpenUrl(OllamaInstaller.WindowsSetupUrl.Replace("/OllamaSetup.exe", ""));
                    return;
                }

                OllamaInstallStatus = L("ollama.installing");
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo { FileName = dest, UseShellExecute = true };
                    psi.ArgumentList.Add("/VERYSILENT");
                    psi.ArgumentList.Add("/SUPPRESSMSGBOXES");
                    psi.ArgumentList.Add("/NORESTART");
                    System.Diagnostics.Process.Start(psi);
                }
                catch
                {
                    OllamaInstallStatus = L("ollama.failed");
                    OpenUrl(OllamaInstaller.WindowsSetupUrl.Replace("/OllamaSetup.exe", ""));
                    return;
                }

                // The installer starts `ollama serve`; wait (≈2 min) for it to answer on localhost:11434.
                OllamaInstallStatus = L("ollama.waiting");
                bool up = false;
                for (int i = 0; i < 60 && !up; i++) { await Task.Delay(2000); up = await PingOllama(); }
                OllamaReachable = up;
                if (!up) { OllamaInstallStatus = L("ollama.startManual"); return; }
                OllamaInstallStatus = L("ollama.ready");
                await RefreshInstalledModels();
            }

            // Zero-touch: a running engine with no model can't score — pull the machine-recommended one.
            if (OllamaReachable && InstalledModels.Count == 0)
            {
                UseLocalModel = true;
                OllamaInstallStatus = Loc.Instance.F("ollama.pullModel", RecommendedTag);
                await InstallRecommended();
            }
        }
        catch (Exception ex) { OllamaInstallStatus = Loc.Instance.F("error.generic", ex.Message); }
        finally { IsInstallingOllama = false; OllamaInstallProgress = 0; }
    }

    /// <summary>True when the active engine is a local OpenAI-compatible model — drives the "smaller models are
    /// less accurate" note on the plan/scoring screens.</summary>
    public bool UsingLocalEngine => string.Equals(_cfg.Claude.Provider, "openai", StringComparison.OrdinalIgnoreCase);

    // ---- live model browser (Ollama discovery + one-click install) ----
    [ObservableProperty] private string _modelSearchText = "";
    [ObservableProperty] private bool _isSearchingRegistry;
    [ObservableProperty] private string _registrySearchStatus = "";
    public ObservableCollection<RegistryModelVm> RegistryResults { get; } = new();
    public bool HasRegistryResults => RegistryResults.Count > 0;
    private CancellationTokenSource? _regCts;

    partial void OnModelSearchTextChanged(string value) => _ = DebouncedRegistrySearch();

    /// <summary>Debounced live search of the active tab's source; empty query → that source's popular list.</summary>
    private async Task DebouncedRegistrySearch()
    {
        _regCts?.Cancel();
        var cts = _regCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, cts.Token);
            IsSearchingRegistry = true; RegistrySearchStatus = L("models.searching");
            var results = await ModelRegistry.SearchOllamaAsync((ModelSearchText ?? "").Trim(), cts.Token);
            if (cts.Token.IsCancellationRequested) return;
            RegistryResults.Clear();
            foreach (var m in results) RegistryResults.Add(new RegistryModelVm(m));
            OnPropertyChanged(nameof(HasRegistryResults));
            RegistrySearchStatus = results.Count == 0 ? L("models.noResults") : "";
        }
        catch (OperationCanceledException) { /* superseded by a newer keystroke */ }
        catch { RegistrySearchStatus = L("models.searchError"); }
        finally { if (_regCts == cts) IsSearchingRegistry = false; }
    }

    /// <summary>Reveals a result's size choices (Ollama tags) and toggles its expander.</summary>
    [RelayCommand]
    private void ExpandQuants(RegistryModelVm? vm)
    {
        if (vm is null) return;
        if (vm.QuantsLoaded) { vm.Expanded = !vm.Expanded; return; }
        // payload: source \t repo (for row lookup) \t install-target (ollama tag)
        foreach (var s in vm.OllamaSizes) vm.Quants.Add(new QuantOption(s, $"ollama\t{vm.Repo}\t{vm.Repo}:{s}"));
        vm.QuantsLoaded = true; vm.Expanded = true;
        OnPropertyChanged(nameof(RegistryResults)); // refresh the expander binding
    }

    /// <summary>Installs a chosen quant with feedback ON the model's row: Ollama → pull; LM Studio → direct
    /// one-click install of an Ollama model with feedback ON the model's row. payload = "ollama \t repo \t tag".</summary>
    [RelayCommand]
    private async Task InstallModel(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || IsPullingModel) return;
        var parts = payload.Split('\t');
        if (parts.Length < 3) return;
        string source = parts[0], repo = parts[1], target = parts[2];

        // Find the row so progress shows exactly where the user clicked.
        var row = RegistryResults.FirstOrDefault(r => r.Source == source && r.Repo == repo);
        IsPullingModel = true; PullProgress = 0;
        if (row is not null) { row.Installing = true; row.InstallProgress = 0; row.InstallStatus = L("models.starting"); }
        ModelDownloadStatus = Loc.Instance.F("models.pulling", target, "0%");

        var progress = new Progress<(string status, double frac)>(p => Dispatcher.UIThread.Post(() =>
        {
            string line = p.frac > 0 ? $"{p.frac * 100:0}% · {p.status}" : p.status;
            if (p.frac > 0) PullProgress = p.frac;
            ModelDownloadStatus = Loc.Instance.F("models.pulling", target, p.frac > 0 ? $"{p.frac * 100:0}%" : p.status);
            if (row is not null) { if (p.frac > 0) row.InstallProgress = p.frac; row.InstallStatus = line; }
        }));

        bool ok = false;
        try
        {
            ok = await LlmClient.PullModelAsync(OllamaBaseUrl, target, progress);
            if (ok) await RefreshInstalledModels();
            string done = ok ? L("models.done") : L("models.pullFail");
            ModelDownloadStatus = done;
            if (row is not null) row.InstallStatus = done;
        }
        catch (Exception ex)
        {
            ModelDownloadStatus = Loc.Instance.F("error.generic", ex.Message);
            if (row is not null) row.InstallStatus = L("models.pullFail");
        }
        finally
        {
            IsPullingModel = false; PullProgress = 0;
            if (row is not null) row.Installing = false;
        }
    }

    /// <summary>Opens the model's page (ollama.com/library/&lt;name&gt; or huggingface.co/&lt;repo&gt;).</summary>
    [RelayCommand]
    private void OpenModelPage(RegistryModelVm? vm)
    {
        if (vm is null) return;
        OpenUrl($"https://ollama.com/library/{vm.Repo}");
    }
    /// <summary>Set by the view: confirms removing an installed model. Returns true to proceed.</summary>
    public Func<string, Task<bool>>? ConfirmRemoveAsync;

    // about / update
    public string AppVersion => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private string _updatePageUrl = "";
    public bool HasUpdatePage => !string.IsNullOrWhiteSpace(UpdatePageUrl);
    partial void OnUpdatePageUrlChanged(string value) => OnPropertyChanged(nameof(HasUpdatePage));

    // usage & limits
    [ObservableProperty] private string _apifyUsage = "";
    [ObservableProperty] private string _jSearchUsage = "";
    [ObservableProperty] private string _llmUsage = "";
    [ObservableProperty] private bool _checkingUsage;

    private string ComposeJSearchUsage()
    {
        int rem = _cfg.JSearch.LastRemaining, lim = _cfg.JSearch.LastLimit;
        if (rem < 0 && lim < 0) return L("usage.jsearch.none");
        return Loc.Instance.F("usage.jsearch", rem >= 0 ? rem.ToString() : "?", lim >= 0 ? lim.ToString() : "?");
    }

    private void PopulateUsage()
    {
        JSearchUsage = ComposeJSearchUsage();
        LlmUsage = UseLocalModel ? L("usage.local") : L("usage.claude");
        ApifyUsage = "";
    }

    /// <summary>Refreshes the usage lines: a free Apify usage call + the last-known JSearch quota.</summary>
    [RelayCommand]
    private async Task CheckUsage()
    {
        if (CheckingUsage) return;
        CheckingUsage = true;
        try
        {
            JSearchUsage = ComposeJSearchUsage();
            LlmUsage = UseLocalModel ? L("usage.local") : L("usage.claude");
            if (_cfg.Apify.Enabled && !string.IsNullOrWhiteSpace(_cfg.Apify.Token))
            {
                var u = await ApifyClient.GetUsageAsync(_cfg.Apify.Token);
                ApifyUsage = string.IsNullOrWhiteSpace(u) ? L("usage.unknown") : u;
            }
            else ApifyUsage = L("usage.unknown");
        }
        finally { CheckingUsage = false; }
    }

    /// <summary>Validates the Apify token (free) and auto-fills the actor dropdown from the store.</summary>
    [RelayCommand]
    private async Task ProbeApify()
    {
        Busy = true; ApifyStatus = L("apify.validating");
        try
        {
            var (ok, msg, actors) = await ApifyClient.ProbeAsync(ApifyToken.Trim());
            ApifyStatus = msg;
            string current = ApifyActor;
            ApifyActorOptions.Clear();
            foreach (var a in actors) ApifyActorOptions.Add(a);
            if (!string.IsNullOrWhiteSpace(current) && !ApifyActorOptions.Contains(current)) ApifyActorOptions.Add(current);
            if (ok && string.IsNullOrWhiteSpace(ApifyActor) && ApifyActorOptions.Count > 0) ApifyActor = ApifyActorOptions[0];
        }
        finally { Busy = false; }
    }

    // Ollama operations (pull, /api/tags) must hit the Ollama server — NOT whatever the scoring Base URL is
    // right now (it may have been switched to LM Studio's :1234 when an LM Studio model was activated).
    private string OllamaBaseUrl
    {
        get
        {
            string u = (LlmBaseUrl ?? "").Trim();
            return string.IsNullOrWhiteSpace(u) || u.Contains(":1234") ? "http://localhost:11434/v1" : u;
        }
    }

    /// <summary>Reloads the installed-models list from Ollama and rebuilds the cards (active = LlmModel).</summary>
    [RelayCommand]
    private async Task RefreshInstalledModels()
    {
        var models = await LlmClient.ListOllamaModelsAsync(OllamaBaseUrl);
        OllamaReachable = models.Count > 0 || await PingOllama();
        InstalledModels.Clear();
        foreach (var m in models)
            InstalledModels.Add(new OllamaModelVm(m.Name, m.Meta, string.Equals(m.Name, LlmModel, StringComparison.OrdinalIgnoreCase)));
        // Also detect LM Studio models (a folder scan — no LM Studio process needed).
        foreach (var g in LmStudioInstall.ListInstalled())
        {
            string meta = string.Join(" · ", new[] { g.SizeGb > 0 ? $"{g.SizeGb} GB" : "", g.Repo }.Where(s => !string.IsNullOrWhiteSpace(s)));
            InstalledModels.Add(new OllamaModelVm(g.Name, meta, string.Equals(g.Name, LlmModel, StringComparison.OrdinalIgnoreCase), "lmstudio", g.Path));
        }
        OnPropertyChanged(nameof(HasInstalledModels));
        OnPropertyChanged(nameof(RecommendedInstalled));
        OnPropertyChanged(nameof(ShowOllamaWarning));
    }

    /// <summary>Show the "Ollama isn't responding" hint only when Ollama is down AND nothing was detected at
    /// all (so it doesn't nag when LM Studio models are listed).</summary>
    public bool ShowOllamaWarning => !OllamaReachable && InstalledModels.Count == 0;
    partial void OnOllamaReachableChanged(bool value) => OnPropertyChanged(nameof(ShowOllamaWarning));

    private async Task<bool> PingOllama()
    {
        try { return (await LlmClient.ListOpenAiModelsAsync(OllamaBaseUrl, LlmApiKey)).Count >= 0; } catch { return false; }
    }

    /// <summary>Picks the active model (used for scoring). Applied to the UI immediately; persisted on Save
    /// (so it's part of the unsaved-changes state, consistent with the other settings).</summary>
    private const string OllamaBaseDefault = "http://localhost:11434/v1";
    private const string LmStudioBaseDefault = "http://localhost:1234/v1";

    [RelayCommand]
    private void SetActiveModel(OllamaModelVm? m)
    {
        if (m is null || string.IsNullOrWhiteSpace(m.Name)) return;
        LlmModel = m.Name.Trim();
        if (!ModelOptions.Contains(LlmModel)) ModelOptions.Add(LlmModel);
        // Point the endpoint at the model's runtime, but only when the current Base URL is clearly the OTHER
        // runtime's default (don't clobber a custom URL).
        if (m.IsLmStudio && (LlmBaseUrl ?? "").Contains(":11434")) LlmBaseUrl = LmStudioBaseDefault;
        else if (!m.IsLmStudio && (LlmBaseUrl ?? "").Contains(":1234")) LlmBaseUrl = OllamaBaseDefault;
        for (int i = 0; i < InstalledModels.Count; i++)
            InstalledModels[i] = InstalledModels[i] with { IsActive = string.Equals(InstalledModels[i].Name, LlmModel, StringComparison.OrdinalIgnoreCase) };
        // LM Studio (unlike Ollama) doesn't serve until its local server is started — warn now, not at generate time.
        if (m.IsLmStudio) _ = HintLmStudioServer();
        else ModelDownloadStatus = "";
    }

    private async Task HintLmStudioServer()
    {
        ModelDownloadStatus = L("models.lmCheck");
        bool up = false;
        try { up = (await LlmClient.ListOpenAiModelsAsync(LmStudioBaseDefault, LlmApiKey)).Count >= 0; } catch { }
        ModelDownloadStatus = up ? L("models.lmReady") : L("models.lmStart");
    }

    /// <summary>Removes an installed model (after confirmation): Ollama via its API, LM Studio by deleting the
    /// GGUF file. Then refreshes the list.</summary>
    [RelayCommand]
    private async Task RemoveModel(OllamaModelVm? m)
    {
        if (m is null || string.IsNullOrWhiteSpace(m.Name) || IsPullingModel) return;
        if (ConfirmRemoveAsync is not null && !await ConfirmRemoveAsync(m.Name)) return;
        if (m.IsLmStudio) LmStudioInstall.Remove(m.Path);
        else await LlmClient.DeleteOllamaModelAsync(OllamaBaseUrl, m.Name);
        await RefreshInstalledModels();
    }

    /// <summary>One-click install of a suggested model.</summary>
    [RelayCommand]
    private Task PullSuggested(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Task.CompletedTask;
        ModelToPull = name.Trim();
        return DownloadModel();
    }

    /// <summary>Downloads a model via Ollama (streamed progress + progress bar), then activates it.</summary>
    [RelayCommand]
    private async Task DownloadModel()
    {
        if (IsPullingModel || string.IsNullOrWhiteSpace(ModelToPull)) return;
        IsPullingModel = true; PullProgress = 0;
        string name = ModelToPull.Trim();
        ModelDownloadStatus = Loc.Instance.F("models.pulling", name, "0%");
        var progress = new Progress<(string status, double frac)>(p => Dispatcher.UIThread.Post(() =>
        {
            if (p.frac > 0) PullProgress = p.frac;
            ModelDownloadStatus = Loc.Instance.F("models.pulling", name, p.frac > 0 ? $"{p.frac * 100:0}%" : p.status);
        }));
        try
        {
            bool ok = await LlmClient.PullModelAsync(OllamaBaseUrl, name, progress);
            if (ok)
            {
                ModelDownloadStatus = Loc.Instance.F("models.pullDone", name);
                ModelToPull = "";
                LlmModel = name;   // make the freshly pulled model active (the refresh marks it)
                if (!ModelOptions.Contains(name)) ModelOptions.Add(name);
                await RefreshInstalledModels();
            }
            else ModelDownloadStatus = L("models.pullFail");
        }
        catch (Exception ex) { ModelDownloadStatus = Loc.Instance.F("error.generic", ex.Message); }
        finally { IsPullingModel = false; PullProgress = 0; }
    }

    /// <summary>Checks GitHub for a newer release; on Windows downloads + launches the installer, else opens the page.</summary>
    [RelayCommand]
    private async Task CheckForUpdate()
    {
        UpdateStatus = L("update.checking"); UpdatePageUrl = "";
        try
        {
            var info = await UpdateChecker.CheckAsync();
            if (info is null) { UpdateStatus = L("update.failed"); return; }
            if (!UpdateChecker.IsNewer(info.Latest, AppVersion))
            {
                UpdateStatus = Loc.Instance.F("update.upToDate", AppVersion);
                return;
            }
            UpdateStatus = Loc.Instance.F("update.available", info.Latest);
            UpdatePageUrl = info.HtmlUrl; // surfaces an "open download page" link as a fallback

            if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(info.WinInstallerUrl))
            {
                var progress = new Progress<double>(f => Dispatcher.UIThread.Post(() =>
                    UpdateStatus = Loc.Instance.F("update.downloading", $"{f * 100:0}%")));
                string dest = Path.Combine(Path.GetTempPath(), $"JobRadar-Setup-{info.Latest}.exe");
                if (await UpdateChecker.DownloadAsync(info.WinInstallerUrl!, dest, progress))
                {
                    UpdateStatus = L("update.installing");
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dest, UseShellExecute = true });
                        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                    }
                    catch { OpenUrl(info.HtmlUrl); }
                }
                else { UpdateStatus = L("update.failed"); }
            }
            else OpenUrl(info.HtmlUrl); // macOS / Linux: manual download
        }
        catch (Exception ex) { UpdateStatus = Loc.Instance.F("error.generic", ex.Message); }
    }

    public ObservableCollection<string> ModelOptions { get; } = new();
    private const string DefaultClaudeModel = "(predefinido)";   // maps to empty → CLI's own default
    private static readonly string[] ClaudeModels = { DefaultClaudeModel, "sonnet", "opus", "haiku" };

    partial void OnUseLocalModelChanged(bool value)
    {
        LlmModel = value ? "" : DefaultClaudeModel; // local needs an explicit model; Claude defaults
        RefreshModelOptions();
        if (value) { _ = RefreshInstalledModels(); _ = DebouncedRegistrySearch(); }
    }

    // ---- profile form ----
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _fieldText = "";
    [ObservableProperty] private string _jobTitlesText = "";
    [ObservableProperty] private string _coreSkillsText = "";
    [ObservableProperty] private string _skillsText = "";
    [ObservableProperty] private string _locText = "";
    [ObservableProperty] private string _yearsText = "4";
    [ObservableProperty] private string _seniority = "mid";
    [ObservableProperty] private string _salaryFloorText = "";
    [ObservableProperty] private string _salaryTargetText = "";
    [ObservableProperty] private bool _remote = true;
    [ObservableProperty] private bool _hybrid = true;
    [ObservableProperty] private bool _onsite;
    [ObservableProperty] private bool _useAi = true; // scoring mode toggle (AI vs keywords)

    public string[] SeniorityOptions { get; } = { "junior", "mid", "senior" };
    public int[] MinScoreOptions { get; } = { 0, 40, 60, 70 };

    // ---- results ----
    public ObservableCollection<string> Log { get; } = new();
    public ObservableCollection<JobVm> Jobs { get; } = new();
    [ObservableProperty] private int _minScore;
    [ObservableProperty] private string _resultsTitle = "";
    partial void OnMinScoreChanged(int value) => ApplyFilter();

    // ---- CV → profile (called by the view after the file picker) ----
    public async Task LoadCvAsync(string path)
    {
        Busy = true; Status = L("cv.reading");
        try
        {
            string text = "";
            try { text = await Task.Run(() => CvProfiler.ExtractText(path)); } catch { }

            if (string.IsNullOrWhiteSpace(text))
            {
                Status = "Não consegui extrair texto (PDF digitalizado?). Preenche à mão.";
                _profile = new UserProfile();
            }
            else
            {
                Status = L("cv.building");
                _profile = await CvProfiler.BuildProfileAsync(text, _cfg.Claude)
                           ?? new UserProfile { Summary = "(IA indisponível — preenche à mão)" };
            }

            if (_profile.SalaryTargetEur == 0) _profile.SalaryTargetEur = _cfg.Salary.TargetEur;
            if (_profile.SalaryFloorEur == 0) _profile.SalaryFloorEur = _cfg.Salary.FloorEur;
            _isDemoProfile = false; // a real CV the user picked
            LoadFormFromProfile();
            SaveProfile(); // persist so we don't re-parse the CV (and spend tokens) next time
            ShowOnly(profile: true);
        }
        finally { Busy = false; }
    }

    /// <summary>Loads a ready-made sample profile (John Doe) so people can try the app instantly,
    /// with no tokens and without touching their own saved profile.</summary>
    [RelayCommand]
    private void LoadDemoCv()
    {
        _profile = DemoProfile();
        _isDemoProfile = true;
        LoadFormFromProfile();
        Status = L("demo.profile.status");
        ShowOnly(profile: true);
    }

    private static UserProfile DemoProfile() => new()
    {
        Name = "John Doe",
        Summary = "Backend-leaning full-stack engineer with ~7 years building distributed systems in C#/.NET and Go.",
        Field = "Software Engineering",
        JobTitles = new() { "Backend Developer", "Full-Stack Developer", ".NET Developer", "Software Engineer", "Go Developer" },
        CoreSkills = new() { "C#", ".NET", "Go", "SQL" },
        Skills = new() { "ASP.NET Core", "PostgreSQL", "Docker", "Kubernetes", "Azure", "gRPC" },
        Locations = new() { "Porto", "Portugal" },
        Languages = new() { "English C2", "Portuguese native" },
        YearsExperience = 7,
        SeniorityTarget = "senior",
        SalaryFloorEur = 45000,
        SalaryTargetEur = 70000,
        Remote = true, Hybrid = true, Onsite = false,
    };

    [RelayCommand]
    private void EditProfile()
    {
        LoadFormFromProfile();
        ShowOnly(profile: true);
    }

    private void LoadSavedProfile()
    {
        try
        {
            if (!File.Exists(_profilePath)) return;
            var p = JsonSerializer.Deserialize<UserProfile>(File.ReadAllText(_profilePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (p is null) return;
            _profile = p;
            _isDemoProfile = false;
            LoadFormFromProfile();
            HasSavedProfile = true;
            SavedProfileLabel = string.IsNullOrWhiteSpace(p.Field) ? p.Name : $"{p.Name} · {p.Field}";
        }
        catch { /* ignore a corrupt profile */ }
    }

    private void SaveProfile()
    {
        if (_isDemoProfile) return; // never overwrite the user's real saved profile with the sample
        try
        {
            File.WriteAllText(_profilePath, JsonSerializer.Serialize(_profile, new JsonSerializerOptions { WriteIndented = true }));
            HasSavedProfile = true;
            SavedProfileLabel = string.IsNullOrWhiteSpace(_profile.Field) ? _profile.Name : $"{_profile.Name} · {_profile.Field}";
        }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private async Task StartSearch()
    {
        CommitFormToProfile();
        SaveProfile(); // persist any edits to the saved profile
        // Paid / quota-limited connectors — confirm before spending the user's credits or quota.
        bool apifyOn = _cfg.Apify.Enabled && !string.IsNullOrWhiteSpace(_cfg.Apify.Token);
        bool jsearchOn = _cfg.JSearch.Enabled && !string.IsNullOrWhiteSpace(_cfg.JSearch.ApiKey);
        if ((apifyOn || jsearchOn) && ConfirmCostAsync is not null)
            if (!await ConfirmCostAsync()) return;
        await RunPipeline(useAi: UseAi, demo: false);
    }

    [RelayCommand]
    private Task RunDemo() => RunPipeline(useAi: false, demo: true);

    /// <summary>Shows the jobs already saved in the local cache, without fetching or scoring again.</summary>
    [RelayCommand]
    private async Task ViewJobs()
    {
        Log.Clear(); _all = new(); Jobs.Clear(); HasJobs = false; ExportMsg = ""; ScoringError = "";
        ResultsTitle = L("title.saved");
        ScoringStatus = L("scoring.loadingSaved"); MinScore = 0; IsScoring = true; Paused = false;
        ShowOnly(results: true); Busy = true;
        _scoreCts = new CancellationTokenSource();
        var jobProg = new Progress<JobEntity>(j => Dispatcher.UIThread.Post(() => AddStreamed(j)));
        try
        {
            var result = await Pipeline.LoadCachedAsync(_cfg, _root, jobProg, _scoreCts.Token);
            FinalizeResults(result);
            if (!HasJobs) ScoringStatus = L("empty.noSaved");
        }
        catch (OperationCanceledException) { Paused = true; ScoringStatus = L("scoring.paused"); }
        catch (Exception ex) { ScoringError = ScoringStatus = Loc.Instance.F("error.generic", ex.Message); Diag.Error("scoring failed", ex); }
        finally { IsScoring = false; Busy = false; }
    }

    /// <summary>Set by the View: confirms before deleting all saved jobs. Returns true to proceed.</summary>
    public Func<Task<bool>>? ConfirmDeleteJobsAsync;

    /// <summary>Deletes every saved job (after confirmation) — clears the on-screen list and the SQLite cache,
    /// so "View jobs" comes up empty until the next search.</summary>
    [RelayCommand]
    private async Task DeleteJobs()
    {
        if (_all.Count == 0 || Busy) return;
        if (ConfirmDeleteJobsAsync is not null && !await ConfirmDeleteJobsAsync()) return;
        try { Pipeline.ClearCache(_cfg, _root); }
        catch (Exception ex) { Status = Loc.Instance.F("error.generic", ex.Message); }
        _all = new(); Jobs.Clear(); HasJobs = false; TotalCount = 0; ExportMsg = "";
        EmptyMessage = L("empty.noneToShow");
        Status = L("results.deleted");
    }

    /// <summary>Re-scores the cached jobs with the current model/engine (after changing it in Settings).</summary>
    [RelayCommand]
    private async Task Rescore()
    {
        Log.Clear(); _all = new(); Jobs.Clear(); HasJobs = false; ExportMsg = ""; ScoringError = "";
        ResultsTitle = L("title.jobs"); ScoringStatus = L("scoring.rescoring");
        MinScore = 0; IsScoring = true; Paused = false; ShowOnly(results: true); Busy = true;
        _scoreCts = new CancellationTokenSource();
        var logProg = new Progress<string>(m => Dispatcher.UIThread.Post(() => { Log.Add(m); ScoringStatus = m; }));
        var jobProg = new Progress<JobEntity>(j => Dispatcher.UIThread.Post(() => AddStreamed(j)));
        try
        {
            var result = await Pipeline.RescoreAsync(_profile, _cfg, _root, logProg, jobProg, _scoreCts.Token);
            FinalizeResults(result);
            if (!HasJobs) ScoringStatus = L("empty.noSavedRescore");
        }
        catch (OperationCanceledException) { Paused = true; ScoringStatus = L("scoring.paused"); }
        catch (Exception ex) { ScoringError = ScoringStatus = Loc.Instance.F("error.generic", ex.Message); Diag.Error("scoring failed", ex); }
        finally { IsScoring = false; Busy = false; }
    }

    /// <summary>Back to the home screen (keeps the loaded results in memory).</summary>
    [RelayCommand] private void GoHome() => ShowOnly(welcome: true);

    // ---- settings ----
    private void OpenSettings()
    {
        LoadSettingsFields();
        ApifyStatus = "";
        ApifyActorOptions.Clear();
        if (!string.IsNullOrWhiteSpace(_cfg.Apify.ActorId)) ApifyActorOptions.Add(_cfg.Apify.ActorId);
        ModelDownloadStatus = ""; PullProgress = 0;
        UpdateStatus = ""; UpdatePageUrl = "";
        InstalledModels.Clear(); OnPropertyChanged(nameof(HasInstalledModels));
        // Fire-and-forget: the refresh only marks the active card, it doesn't touch any Save-backed field,
        // so the snapshot below stays correct and the screen opens instantly.
        if (UseLocalModel) { _ = RefreshInstalledModels(); _ = DebouncedRegistrySearch(); }
        PopulateUsage();
        Status = "";
        _settingsSnapshot = SettingsSignature();
        ShowOnly(settings: true);
    }

    /// <summary>Loads the Save-backed settings fields from the live config (used on open and on discard).</summary>
    private void LoadSettingsFields()
    {
        UseLocalModel = string.Equals(_cfg.Claude.Provider, "openai", StringComparison.OrdinalIgnoreCase);
        LlmBaseUrl = _cfg.Claude.BaseUrl;
        LlmApiKey = _cfg.Claude.ApiKey;
        ClaudeExe = string.IsNullOrWhiteSpace(_cfg.Claude.Exe) ? "claude" : _cfg.Claude.Exe;
        // Set model AFTER UseLocalModel so the change-handler doesn't clobber it; map empty Claude → label.
        LlmModel = (!UseLocalModel && string.IsNullOrWhiteSpace(_cfg.Claude.Model)) ? DefaultClaudeModel : _cfg.Claude.Model;
        LlmMaxTokens = _cfg.Claude.MaxTokens > 0 ? _cfg.Claude.MaxTokens : 4096;
        LlmTimeoutSeconds = _cfg.Claude.TimeoutSeconds > 0 ? _cfg.Claude.TimeoutSeconds : 300;
        RefreshModelOptions();
        UseApify = _cfg.Apify.Enabled;
        ApifyToken = _cfg.Apify.Token;
        ApifyActor = _cfg.Apify.ActorId;
        ApifyMax = _cfg.Apify.MaxItems > 0 ? _cfg.Apify.MaxItems.ToString() : "50";
        UseJSearch = _cfg.JSearch.Enabled;
        JSearchProviderIndex = string.Equals(_cfg.JSearch.Provider, "rapidapi", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        JSearchKey = _cfg.JSearch.ApiKey;
        JSearchCountry = string.IsNullOrWhiteSpace(_cfg.JSearch.Country) ? "pt" : _cfg.JSearch.Country;
        JSearchMax = _cfg.JSearch.MaxItems > 0 ? _cfg.JSearch.MaxItems.ToString() : "20";
        UseJobicy = _cfg.Jobicy.Enabled;
        JobicyRegionIndex = Math.Max(0, Array.IndexOf(JobicyGeoValues, (_cfg.Jobicy.Geo ?? "europe").Trim().ToLowerInvariant()));
        JobicyMax = _cfg.Jobicy.MaxItems > 0 ? _cfg.Jobicy.MaxItems.ToString() : "50";
        UseHimalayas = _cfg.Himalayas.Enabled;
        HimalayasMax = _cfg.Himalayas.MaxItems > 0 ? _cfg.Himalayas.MaxItems.ToString() : "40";
    }

    /// <summary>A stable fingerprint of every Save-backed setting — used to detect unsaved edits.
    /// Theme/text-size/language are excluded: they apply and persist live, not via Save.</summary>
    private string SettingsSignature() => string.Join("",
        UseLocalModel, LlmBaseUrl, LlmModel, LlmApiKey, LlmMaxTokens, LlmTimeoutSeconds, ClaudeExe,
        UseApify, ApifyToken, ApifyActor, ApifyMax,
        UseJSearch, JSearchProviderIndex, JSearchKey, JSearchCountry, JSearchMax,
        UseJobicy, JobicyRegionIndex, JobicyMax, UseHimalayas, HimalayasMax);

    private string _settingsSnapshot = "";

    /// <summary>True when the user changed a Save-backed setting without saving or discarding.</summary>
    private bool SettingsDirty() => IsSettings && SettingsSignature() != _settingsSnapshot;

    /// <summary>Set by the View: prompts to save/discard unsaved settings. Returns 1=save, 2=discard, 0=cancel.</summary>
    public Func<Task<int>>? ConfirmLeaveSettingsAsync;

    [RelayCommand]
    private void SaveSettings()
    {
        _cfg.Claude.Provider = UseLocalModel ? "openai" : "claude-cli";
        _cfg.Claude.BaseUrl = string.IsNullOrWhiteSpace(LlmBaseUrl) ? "http://localhost:11434/v1" : LlmBaseUrl.Trim();
        _cfg.Claude.Model = LlmModel == DefaultClaudeModel ? "" : (LlmModel ?? "").Trim();
        _cfg.Claude.MaxTokens = LlmMaxTokens > 0 ? (int)(Math.Round(LlmMaxTokens / 1024.0) * 1024) : 4096;
        _cfg.Claude.TimeoutSeconds = LlmTimeoutSeconds >= 30 ? LlmTimeoutSeconds : 300;
        _cfg.Claude.ApiKey = LlmApiKey.Trim();
        _cfg.Claude.Exe = string.IsNullOrWhiteSpace(ClaudeExe) ? "claude" : ClaudeExe.Trim();
        SaveLlmSettings();
        _cfg.Apify.Enabled = UseApify;
        _cfg.Apify.Token = ApifyToken.Trim();
        _cfg.Apify.ActorId = ApifyActor.Trim();
        _cfg.Apify.MaxItems = int.TryParse(ApifyMax, out var am) && am > 0 ? am : 50;
        SaveApifySettings();
        _cfg.JSearch.Enabled = UseJSearch;
        _cfg.JSearch.Provider = JSearchProviderIndex == 1 ? "rapidapi" : "openwebninja";
        _cfg.JSearch.ApiKey = JSearchKey.Trim();
        _cfg.JSearch.Country = string.IsNullOrWhiteSpace(JSearchCountry) ? "pt" : JSearchCountry.Trim().ToLowerInvariant();
        _cfg.JSearch.MaxItems = int.TryParse(JSearchMax, out var jm) && jm > 0 ? jm : 20;
        SaveJSearchSettings();
        _cfg.Jobicy.Enabled = UseJobicy;
        _cfg.Jobicy.Geo = JobicyGeoValues[Math.Clamp(JobicyRegionIndex, 0, JobicyGeoValues.Length - 1)];
        _cfg.Jobicy.MaxItems = int.TryParse(JobicyMax, out var gm) && gm > 0 ? gm : 50;
        SaveJobicySettings();
        _cfg.Himalayas.Enabled = UseHimalayas;
        _cfg.Himalayas.MaxItems = int.TryParse(HimalayasMax, out var hm) && hm > 0 ? hm : 40;
        SaveHimalayasSettings();
        _settingsSnapshot = SettingsSignature();
        OnPropertyChanged(nameof(UsingLocalEngine));
        Status = L("settings.saved");
        ShowOnly(welcome: true);
    }

    /// <summary>Populates the model dropdown: curated aliases for Claude CLI, fetched list for local.</summary>
    private void RefreshModelOptions()
    {
        ModelOptions.Clear();
        if (!UseLocalModel)
            foreach (var m in ClaudeModels) ModelOptions.Add(m);
        if (!string.IsNullOrWhiteSpace(LlmModel) && !ModelOptions.Contains(LlmModel)) ModelOptions.Add(LlmModel);
        if (string.IsNullOrWhiteSpace(LlmModel) && !UseLocalModel) LlmModel = DefaultClaudeModel;
    }

    /// <summary>For local providers, fetch installed models from the runtime (LM Studio / Ollama).</summary>
    [RelayCommand]
    private async Task LoadModels()
    {
        if (!UseLocalModel) { RefreshModelOptions(); return; }
        Busy = true; Status = L("models.loading");
        try
        {
            var models = await LlmClient.ListOpenAiModelsAsync(LlmBaseUrl, LlmApiKey);
            string current = LlmModel;
            ModelOptions.Clear();
            foreach (var m in models) ModelOptions.Add(m);
            if (!string.IsNullOrWhiteSpace(current) && !ModelOptions.Contains(current)) ModelOptions.Add(current);
            if (models.Count == 0) Status = L("models.none");
            else { Status = $"{models.Count} modelo(s) encontrados."; if (string.IsNullOrWhiteSpace(LlmModel)) LlmModel = models[0]; }
        }
        finally { Busy = false; }
    }

    /// <summary>Opens LinkedIn Jobs in the default browser, pre-filled from the profile (ToS-safe: no scraping).</summary>
    [RelayCommand]
    private void OpenLinkedIn()
    {
        string kw = _profile.JobTitles.FirstOrDefault() ?? _profile.Field;
        if (string.IsNullOrWhiteSpace(kw)) kw = "software developer";
        string url = $"https://www.linkedin.com/jobs/search/?keywords={Uri.EscapeDataString(kw)}";
        string loc = _profile.Locations.FirstOrDefault() ?? "";
        if (!string.IsNullOrWhiteSpace(loc)) url += $"&location={Uri.EscapeDataString(loc)}";
        if (_profile.Remote) url += "&f_WT=2"; // remote filter
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* best-effort */ }
    }

    // ---- LinkedIn import: paste + AI extract (opt-in, ToS-safe — the USER opens/copies in their own browser;
    // the app automates nothing against their account). Extracted jobs land in linkedin-jobs.json, which the
    // pipeline already merges on the next search. ----
    [ObservableProperty] private bool _showLinkedInImport;
    [ObservableProperty] private string _linkedInPaste = "";
    [ObservableProperty] private bool _isImportingLinkedIn;
    [ObservableProperty] private string _linkedInImportStatus = "";
    [ObservableProperty] private int _linkedInImportedCount;
    public bool HasLinkedInImported => LinkedInImportedCount > 0;
    partial void OnLinkedInImportedCountChanged(int value) => OnPropertyChanged(nameof(HasLinkedInImported));

    private string LinkedInJobsFile => Path.IsPathRooted(_cfg.LinkedInJobsPath)
        ? _cfg.LinkedInJobsPath : Path.Combine(_root, _cfg.LinkedInJobsPath);

    [RelayCommand] private void ToggleLinkedInImport() => ShowLinkedInImport = !ShowLinkedInImport;

    [RelayCommand]
    private async Task ExtractLinkedInJobs()
    {
        if (IsImportingLinkedIn || string.IsNullOrWhiteSpace(LinkedInPaste)) return;
        IsImportingLinkedIn = true;
        LinkedInImportStatus = L("linkedin.import.working");
        var progress = new Progress<string>(m => Dispatcher.UIThread.Post(() => LinkedInImportStatus = m));
        try
        {
            var (jobs, error) = await LinkedInImport.ExtractAsync(_cfg.Claude, LinkedInPaste, progress);
            if (jobs.Count == 0)
            {
                LinkedInImportStatus = string.IsNullOrWhiteSpace(error)
                    ? L("linkedin.import.none") : Loc.Instance.F("error.generic", error);
                return;
            }
            var (added, total) = LinkedInImport.SaveMerged(LinkedInJobsFile, jobs);
            LinkedInImportedCount = total;
            LinkedInImportStatus = Loc.Instance.F("linkedin.import.done", added, total);
            LinkedInPaste = "";
            Diag.Info($"linkedin import: +{added} ({total} total)");
        }
        catch (Exception ex) { LinkedInImportStatus = Loc.Instance.F("error.generic", ex.Message); Diag.Error("linkedin import failed", ex); }
        finally { IsImportingLinkedIn = false; }
    }

    [RelayCommand]
    private void ClearLinkedInImports()
    {
        LinkedInImport.Clear(LinkedInJobsFile);
        LinkedInImportedCount = 0;
        LinkedInImportStatus = L("linkedin.import.cleared");
    }

    [RelayCommand] private void CloseSettings() => ShowOnly(welcome: true);
    [RelayCommand] private void UseLmStudioPreset() { UseLocalModel = true; LlmBaseUrl = "http://localhost:1234/v1"; }
    [RelayCommand] private void UseOllamaPreset() { UseLocalModel = true; LlmBaseUrl = "http://localhost:11434/v1"; }

    /// <summary>
    /// Goes straight to the results view and streams jobs in as the pipeline classifies them
    /// (cached/known jobs appear instantly; newly scored ones land one by one), so the user
    /// always has something to interact with.
    /// </summary>
    private async Task RunPipeline(bool useAi, bool demo)
    {
        Log.Clear(); _all = new(); Jobs.Clear(); HasJobs = false; ExportMsg = ""; ScoringError = "";
        ResultsTitle = demo ? L("title.demo") : L("title.jobs");
        ScoringStatus = demo ? L("scoring.loadingDemo") : L("scoring.searching");
        MinScore = 0; IsScoring = true; Paused = false; ShowOnly(results: true); Busy = true;
        _scoreCts = new CancellationTokenSource();

        var logProg = new Progress<string>(m => Dispatcher.UIThread.Post(() => { Log.Add(m); ScoringStatus = m; }));
        var jobProg = new Progress<JobEntity>(j => Dispatcher.UIThread.Post(() => AddStreamed(j)));
        try
        {
            var result = await Pipeline.RunAsync(
                demo ? new UserProfile() : _profile, _cfg, _root, useAi, logProg, demo, jobProg, _scoreCts.Token);
            FinalizeResults(result);
        }
        catch (OperationCanceledException) { Paused = true; ScoringStatus = L("scoring.paused"); }
        catch (Exception ex) { var m = Loc.Instance.F("error.generic", ex.Message); Log.Add(m); ScoringError = ScoringStatus = m; Diag.Error("search/score run failed", ex); }
        finally { IsScoring = false; Busy = false; }
    }

    [RelayCommand]
    private void Reset()
    {
        // Keep the saved profile; just clear results and go back to the start.
        _all = new(); Jobs.Clear(); ExportMsg = ""; Status = "";
        LoadFormFromProfile();
        ShowOnly(welcome: true);
    }

    [RelayCommand]
    private async Task Export()
    {
        Busy = true;
        try
        {
            string outDir = Path.Combine(_root, "output");
            Directory.CreateDirectory(outDir);
            string day = DateTime.Now.ToString("yyyy-MM-dd-HHmm");
            var jobs = _all.Where(Passes).Select(v => v.Entity).ToList();
            string csv = Path.Combine(outDir, $"jobs-{day}.csv");
            string html = Path.Combine(outDir, $"jobs-{day}.html");
            string pdf = Path.Combine(outDir, $"jobs-{day}.pdf");
            Reports.WriteCsv(csv, jobs);
            Reports.WriteHtml(html, jobs, jobs.Count, day);
            string? edge = Reports.FindEdge();
            bool ok = edge is not null && await Task.Run(() => Reports.WritePdf(html, pdf, edge));
            ExportMsg = ok ? $"Exportado: {pdf}" : $"Exportado CSV+HTML em {outDir} (PDF: Edge não encontrado).";
        }
        catch (Exception ex) { ExportMsg = "Falha a exportar: " + ex.Message; }
        finally { Busy = false; }
    }

    /// <summary>Profile-page "Create CV…" — CV Studio supersedes the old one-page CvPdf export.</summary>
    [RelayCommand]
    private Task OpenCvStudioFromProfile()
    {
        CommitFormToProfile();
        SaveProfile();
        return Navigate("cv");
    }

    /// <summary>Exports the current career plan to a styled PDF (reuses the CV/report HTML→PDF path).</summary>
    [RelayCommand]
    private async Task ExportPlan()
    {
        if (Plan is null || Busy) return;
        Busy = true;
        try
        {
            string outDir = Path.Combine(_root, "output");
            string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmm");
            bool withProgress = IncludeProgressInPdf;
            string? path = await Task.Run(() => CareerPlanPdf.Export(Plan, _profile, outDir, stamp, withProgress));
            if (path is not null)
            {
                Status = Loc.Instance.F("plan.exported", path);
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
                catch { /* opening is best-effort */ }
            }
            else Status = L("plan.exportFailed");
        }
        finally { Busy = false; }
    }

    // ---- diagnostics ----
    /// <summary>Opens the folder where the daily metadata-only log files live.</summary>
    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(Diag.LogDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Diag.LogDir, UseShellExecute = true });
        }
        catch (Exception ex) { Status = Loc.Instance.F("error.generic", ex.Message); Diag.Error("open logs folder failed", ex); }
    }

    /// <summary>Writes a redacted diagnostics bundle (system info + engine config without secrets + recent log
    /// lines) to output/ and opens it — the "download my logs" path for support.</summary>
    [RelayCommand]
    private async Task ExportDiagnostics()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            string text = BuildDiagnosticsBundle();
            string outDir = Path.Combine(_root, "output");
            string path = Path.Combine(outDir, $"jobradar-diagnostics-{DateTime.Now:yyyy-MM-dd-HHmm}.txt");
            await Task.Run(() => { Directory.CreateDirectory(outDir); File.WriteAllText(path, text, System.Text.Encoding.UTF8); });
            Status = Loc.Instance.F("diag.exported", path);
            Diag.Info("diagnostics exported");
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch { /* opening is best-effort */ }
        }
        catch (Exception ex) { Status = Loc.Instance.F("error.generic", ex.Message); Diag.Error("export diagnostics failed", ex); }
        finally { Busy = false; }
    }

    /// <summary>Builds the redacted bundle. NEVER includes API keys, tokens, prompts, replies or CV text.</summary>
    private string BuildDiagnosticsBundle()
    {
        double ramGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024d * 1024 * 1024), 1);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Job Radar — diagnostics");
        sb.AppendLine($"generated:   {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} (UTC)");
        sb.AppendLine($"version:     v{AppVersion}");
        sb.AppendLine($"os:          {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim()} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"dotnet:      {Environment.Version}");
        sb.AppendLine($"ram:         {ramGb} GB");
        sb.AppendLine();
        sb.AppendLine("AI engine (no secrets):");
        sb.AppendLine($"  provider:  {_cfg.Claude.Provider}");
        sb.AppendLine($"  baseUrl:   {_cfg.Claude.BaseUrl}");
        sb.AppendLine($"  model:     {_cfg.Claude.Model}");
        sb.AppendLine($"  maxTokens: {_cfg.Claude.MaxTokens}");
        sb.AppendLine($"  timeout:   {_cfg.Claude.TimeoutSeconds}s");
        sb.AppendLine($"  apiKey:    {(string.IsNullOrWhiteSpace(_cfg.Claude.ApiKey) ? "(none)" : "(set, redacted)")}");
        sb.AppendLine();
        sb.AppendLine("Connectors:");
        sb.AppendLine($"  Apify:     {(_cfg.Apify.Enabled ? "on" : "off")}");
        sb.AppendLine($"  JSearch:   {(_cfg.JSearch.Enabled ? "on" : "off")} provider={_cfg.JSearch.Provider} country={_cfg.JSearch.Country}");
        sb.AppendLine($"  Jobicy:    {(_cfg.Jobicy.Enabled ? "on" : "off")} geo={_cfg.Jobicy.Geo}");
        sb.AppendLine($"  Himalayas: {(_cfg.Himalayas.Enabled ? "on" : "off")}");
        sb.AppendLine();
        sb.AppendLine("Last errors:");
        sb.AppendLine($"  llm:       {(string.IsNullOrWhiteSpace(LlmClient.LastError) ? "(none)" : LlmClient.LastError)}");
        sb.AppendLine($"  plan:      {(string.IsNullOrWhiteSpace(PlanError) ? "(none)" : PlanError)}");
        sb.AppendLine();
        sb.AppendLine("Recent log (metadata only):");
        sb.AppendLine(Diag.Tail(300));
        return sb.ToString();
    }

    public string LogDir => Diag.LogDir;

    // ---- model reasoning: copy / save ----
    /// <summary>Set by the View — copies text to the system clipboard.</summary>
    public Func<string, Task>? CopyToClipboardAsync;
    [ObservableProperty] private bool _reasoningCopied;   // flips the Copy button to "✓ Copied" briefly

    [RelayCommand]
    private async Task CopyReasoning()
    {
        if (CopyToClipboardAsync is null || string.IsNullOrEmpty(PlanReasoning)) return;
        await CopyToClipboardAsync(PlanReasoning);
        ReasoningCopied = true;
        await Task.Delay(1500);
        ReasoningCopied = false;
    }

    /// <summary>Saves the full reasoning transcript to a .txt in output/ and opens it.</summary>
    [RelayCommand]
    private async Task SaveReasoning()
    {
        if (string.IsNullOrWhiteSpace(PlanReasoning) || Busy) return;
        Busy = true;
        try
        {
            string outDir = Path.Combine(_root, "output");
            string path = Path.Combine(outDir, $"jobradar-reasoning-{DateTime.Now:yyyy-MM-dd-HHmm}.txt");
            await Task.Run(() => { Directory.CreateDirectory(outDir); File.WriteAllText(path, PlanReasoning, System.Text.Encoding.UTF8); });
            Status = Loc.Instance.F("reasoning.saved", path);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch { /* opening is best-effort */ }
        }
        catch (Exception ex) { Status = Loc.Instance.F("error.generic", ex.Message); Diag.Error("save reasoning failed", ex); }
        finally { Busy = false; }
    }

    /// <summary>Archives this generation's run record (config + this run's per-call metadata + the full reasoning)
    /// to the pruned reasoning/ folder, so model performance can be reviewed across runs. Best-effort.</summary>
    private void SaveReasoningRecord()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(PlanReasoning)) return;
            string outcome = string.IsNullOrWhiteSpace(PlanError) ? "ok" : "failed: " + PlanError;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Job Radar — plan run record  {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} (UTC)");
            sb.AppendLine($"engine={_cfg.Claude.Provider} model={_cfg.Claude.Model} max_tokens={_cfg.Claude.MaxTokens} timeout={_cfg.Claude.TimeoutSeconds}s critique={(CritiqueMode)CritiqueModeIndex} outcome={outcome}");
            sb.AppendLine();
            sb.AppendLine("== per-call metadata (this run) ==");
            var lines = Diag.Tail(200).Split('\n');
            int start = Array.FindLastIndex(lines, l => l.Contains("plan: generate start"));
            for (int i = Math.Max(0, start); i < lines.Length; i++)
                if (lines[i].Contains("LLM ")) sb.AppendLine(lines[i].Trim());
            sb.AppendLine();
            sb.AppendLine("== reasoning transcript ==");
            sb.AppendLine(PlanReasoning);
            Diag.SaveRunRecord(sb.ToString());
        }
        catch { /* best-effort archive */ }
    }

    // ---- helpers ----
    /// <summary>Per-company briefing cache (machine-local, 7-day TTL) so re-opening a job re-uses the analysis.</summary>
    private Dictionary<string, CompanyBrief> _briefCache = new();

    private async Task<(CompanyBrief? brief, string? error)> ResearchCompanyAsync(JobEntity j, IProgress<string> progress, CancellationToken ct)
    {
        var (brief, error) = await CompanyResearch.ResearchAsync(_cfg.Claude, _profile, j.Company, j.Title, j.Location, progress, ct);
        if (brief is not null && !string.IsNullOrWhiteSpace(j.Company))
        {
            _briefCache[CompanyCache.Key(j.Company)] = brief;
            BriefCache.Save(_briefCachePath, _briefCache);
        }
        return (brief, error);
    }

    /// <summary>Creates the presentation VM for a job, restoring a cached (≤7-day-old) employer briefing.</summary>
    private JobVm NewJobVm(JobEntity j)
    {
        var vm = new JobVm(j, ResearchCompanyAsync);
        if (!string.IsNullOrWhiteSpace(j.Company) && _briefCache.TryGetValue(CompanyCache.Key(j.Company), out var cached))
            vm.Brief = cached;
        return vm;
    }

    /// <summary>Inserts one streamed job into the ranked lists (descending by score).</summary>
    private void AddStreamed(JobEntity j)
    {
        var vm = NewJobVm(j);
        int idx = _all.FindIndex(x => x.Score < vm.Score);
        if (idx < 0) _all.Add(vm); else _all.Insert(idx, vm);
        TotalCount = _all.Count;

        if (Passes(vm))
        {
            int j2 = 0;
            while (j2 < Jobs.Count && Jobs[j2].Score >= vm.Score) j2++;
            Jobs.Insert(j2, vm);
            HasJobs = true;
        }
    }

    /// <summary>Once the pipeline finishes, settle the final order (streaming already populated the list).</summary>
    private void FinalizeResults(PipelineResult result)
    {
        // Capture the JSearch quota seen during this search (for the Usage & limits view).
        var q = JSearchClient.LastQuota;
        if (_cfg.JSearch.Enabled && (q.Remaining >= 0 || q.Limit >= 0))
        {
            _cfg.JSearch.LastRemaining = q.Remaining; _cfg.JSearch.LastLimit = q.Limit;
            _cfg.JSearch.LastChecked = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            SaveJSearchSettings();
        }
        ResultsTitle = result.Demo ? L("title.demo") : L("title.jobs");
        _all = _all.Count == 0 && result.Jobs.Count > 0
            ? result.Jobs.Select(NewJobVm).ToList()
            : _all.OrderByDescending(v => v.Score).ToList();
        ApplyFilter();
    }

    /// <summary>Whether a job passes the current score threshold, search box and all active filters.</summary>
    private bool Passes(JobVm v)
        => v.Score >= MinScore && MatchesSearch(v) && Filters.All(f => f.Matches(v));

    private bool MatchesSearch(JobVm v)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var j = v.Entity;
        bool Has(string? s) => !string.IsNullOrEmpty(s) && s.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        return Has(j.Title) || Has(j.Company) || Has(j.Location) || Has(j.Description) || Has(j.Source);
    }

    private void ApplyFilter()
    {
        Jobs.Clear();
        foreach (var v in _all.Where(Passes)) Jobs.Add(v);
        HasJobs = Jobs.Count > 0;
        TotalCount = _all.Count;
        EmptyMessage = _all.Count > 0 ? L("empty.noMatch") : L("empty.noneToShow");
    }

    private void ShowOnly(bool welcome = false, bool profile = false, bool running = false, bool results = false, bool settings = false, bool improve = false, bool researcher = false, bool coach = false, bool cv = false)
    {
        IsWelcome = welcome; IsProfile = profile; IsRunning = running; IsResults = results; IsSettings = settings; IsImprove = improve; IsResearcher = researcher; IsCoach = coach; IsCv = cv;
        Nav = settings ? "settings" : improve ? "improve" : researcher ? "researcher" : coach ? "coach" : cv ? "cv" : results ? "results" : profile ? "profile" : "home";
    }

    // ---- Company Researcher (employer-health signals across the matched jobs) ----
    /// <summary>Set by the view: confirms a batch research (N model calls) before "Research all".</summary>
    public Func<int, Task<bool>>? ConfirmResearchAllAsync;
    private Dictionary<string, CompanyReport> _companyCache = new();
    public ObservableCollection<CompanyVm> Companies { get; } = new();   // filtered view shown in the UI
    private readonly List<CompanyVm> _companyMaster = new();             // every company (unfiltered, ordered)
    [ObservableProperty] private string _companyQuery = "";             // box that RESEARCHES a new company
    [ObservableProperty] private string _companyFilter = "";            // box that FILTERS the listed companies by name
    [ObservableProperty] private int _companySortIndex;   // 0 jobs · 1 rating · 2 layoff-risk · 3 name
    // Computed (not a field) so it re-reads the active language when the window is rebuilt.
    public string[] CompanySorts => new[]
        { L("researcher.sort.jobs"), L("researcher.sort.rating"), L("researcher.sort.layoffs"), L("researcher.sort.name") };
    public bool HasCompanies => _companyMaster.Count > 0;
    public bool ResearcherEmpty => _companyMaster.Count == 0;

    private void OpenResearcher()
    {
        BuildCompanies();
        ShowOnly(researcher: true);
        // Opened before ever visiting Jobs → quietly pull the scored cache and rebuild.
        if (_all.Count == 0)
            _ = EnsureJobsLoadedAsync().ContinueWith(t =>
            {
                if (t.Result) Dispatcher.UIThread.Post(BuildCompanies);
            }, TaskScheduler.Default);
    }

    /// <summary>Quietly fills <c>_all</c> from the scored cache when no jobs are loaded yet — the
    /// Companies view and the CV job picker need them without a Jobs visit. No view switch, no
    /// streaming UI; the Results view stays coherent via ApplyFilter.</summary>
    private async Task<bool> EnsureJobsLoadedAsync()
    {
        if (_all.Count > 0) return true;
        if (IsScoring) return false;
        try
        {
            var result = await Pipeline.LoadCachedAsync(_cfg, _root, null);
            if (_all.Count == 0 && result.Jobs.Count > 0)
            {
                _all = result.Jobs.Select(NewJobVm).OrderByDescending(v => v.Score).ToList();
                Dispatcher.UIThread.Post(ApplyFilter);
            }
        }
        catch { /* cache unavailable — pickers just stay empty */ }
        return _all.Count > 0;
    }

    /// <summary>Rebuilds the company list from the matched jobs (distinct employers + counts), reusing any
    /// fresh cached report. Manually-added companies (not in the job set) are preserved.</summary>
    private void BuildCompanies()
    {
        var grouped = _all
            .Select(v => v.Company)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .GroupBy(c => c.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .ToList();

        var manual = _companyMaster
            .Where(c => c.JobCount == 0 && !grouped.Any(g => string.Equals(g.Name, c.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Name).ToList();

        var vms = new List<CompanyVm>();
        foreach (var (name, count) in grouped) vms.Add(MakeCompanyVm(name, count));
        foreach (var name in manual) vms.Add(MakeCompanyVm(name, 0));

        _companyMaster.Clear();
        _companyMaster.AddRange(OrderCompanies(vms));
        ApplyCompanyFilter();
    }

    /// <summary>Refills the shown collection from the master list, honouring the name filter.</summary>
    private void ApplyCompanyFilter()
    {
        string f = (CompanyFilter ?? "").Trim();
        Companies.Clear();
        foreach (var vm in _companyMaster)
            if (f.Length == 0 || vm.Name.Contains(f, StringComparison.OrdinalIgnoreCase)) Companies.Add(vm);
        OnPropertyChanged(nameof(HasCompanies));
        OnPropertyChanged(nameof(ResearcherEmpty));
    }

    partial void OnCompanyFilterChanged(string value) => ApplyCompanyFilter();

    private CompanyVm MakeCompanyVm(string name, int count)
    {
        _companyCache.TryGetValue(CompanyCache.Key(name), out var cached);
        var vm = new CompanyVm(name, count, cached, ResearchCompanyReportAsync);
        vm.Researched += OnCompanyResearched;
        return vm;
    }

    private IEnumerable<CompanyVm> OrderCompanies(IEnumerable<CompanyVm> src) => CompanySortIndex switch
    {
        1 => src.OrderByDescending(c => c.Report?.Rating ?? -1).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
        2 => src.OrderByDescending(c => c.Report?.HasLayoffs == true).ThenByDescending(c => c.JobCount).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
        3 => src.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
        _ => src.OrderByDescending(c => c.JobCount).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
    };

    partial void OnCompanySortIndexChanged(int value)
    {
        if (_companyMaster.Count == 0) return;
        var ordered = OrderCompanies(_companyMaster.ToList()).ToList();
        _companyMaster.Clear();
        _companyMaster.AddRange(ordered);
        ApplyCompanyFilter();
    }

    private async Task<(CompanyReport? report, string? error)> ResearchCompanyReportAsync(string company, IProgress<string> progress, CancellationToken ct)
    {
        var (report, err) = await CompanyResearch.ResearchReportAsync(
            _cfg.Claude, _profile, company, _profile.JobTitles.FirstOrDefault() ?? _profile.Field, progress, ct);
        if (report is not null)
        {
            var stack = TechStackFor(company);
            if (stack.Count > 0) report.TechStack = stack;
        }
        return (report, err);
    }

    /// <summary>Tech keywords seen in THIS employer's own postings the radar scored — grounded + candidate-relevant.</summary>
    private List<string> TechStackFor(string company)
    {
        var skills = _profile.CoreSkills.Concat(_profile.Skills)
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (skills.Count == 0) return new();
        string hay = string.Join("  ", _all
            .Where(v => string.Equals(v.Company, company, StringComparison.OrdinalIgnoreCase))
            .Select(v => $"{v.Entity.Title} {v.Entity.Description}")).ToLowerInvariant();
        if (hay.Length == 0) return new();
        return skills.Where(s => hay.Contains(s.ToLowerInvariant())).Take(12).ToList();
    }

    /// <summary>Persist a freshly-researched report to the per-company cache (machine-local, gitignored).</summary>
    private void OnCompanyResearched(CompanyVm vm)
    {
        if (vm.Report is null) return;
        _companyCache[CompanyCache.Key(vm.Name)] = vm.Report;
        CompanyCache.Save(_companyCachePath, _companyCache);
    }

    /// <summary>Research a company typed into the box (e.g. before applying), even if it's not in the job set.</summary>
    [RelayCommand]
    private async Task ResearchManualCompany()
    {
        string name = (CompanyQuery ?? "").Trim();
        if (name.Length == 0) return;
        CompanyQuery = "";
        var vm = _companyMaster.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (vm is null)
        {
            vm = MakeCompanyVm(name, 0);
            _companyMaster.Insert(0, vm);
        }
        CompanyFilter = "";       // clear any name filter so the target is visible…
        ApplyCompanyFilter();     // …and refresh the shown list (no-op event if filter was already empty)
        if (!vm.HasReport && !vm.IsResearching) await vm.ResearchCommand.ExecuteAsync(null);
    }

    /// <summary>Research every not-yet-researched company. Each is one model call (metered on Claude CLI),
    /// so confirm the batch first. Sequential — a single local model serializes requests anyway.</summary>
    [RelayCommand]
    private async Task ResearchAllCompanies()
    {
        var todo = _companyMaster.Where(c => !c.HasReport && !c.IsResearching).ToList();
        if (todo.Count == 0) return;
        if (ConfirmResearchAllAsync is not null && !await ConfirmResearchAllAsync(todo.Count)) return;
        foreach (var c in todo)
        {
            if (c.HasReport || c.IsResearching) continue;
            await c.ResearchCommand.ExecuteAsync(null);
        }
    }

    /// <summary>Exports the researched companies to CSV + HTML + PDF (mirrors the jobs export).</summary>
    [RelayCommand]
    private async Task ExportCompanies()
    {
        var reports = _companyMaster.Where(c => c.Report is not null).Select(c => c.Report!).ToList();
        if (reports.Count == 0) { ExportMsg = L("researcher.export.none"); return; }
        Busy = true;
        try
        {
            string outDir = Path.Combine(_root, "output");
            Directory.CreateDirectory(outDir);
            string day = DateTime.Now.ToString("yyyy-MM-dd-HHmm");
            string csv = Path.Combine(outDir, $"companies-{day}.csv");
            string html = Path.Combine(outDir, $"companies-{day}.html");
            string pdf = Path.Combine(outDir, $"companies-{day}.pdf");
            CompanyExport.WriteCsv(csv, reports);
            CompanyExport.WriteHtml(html, reports, day);
            string? edge = Reports.FindEdge();
            bool ok = edge is not null && await Task.Run(() => Reports.WritePdf(html, pdf, edge));
            ExportMsg = ok ? Loc.Instance.F("export.done", pdf) : Loc.Instance.F("export.doneNoPdf", outDir);
        }
        catch (Exception ex) { ExportMsg = Loc.Instance.F("export.failed", ex.Message); }
        finally { Busy = false; }
    }

    // ---- Coach (grounded chat: applications / salary / interviews, with screenshots) ----
    public ObservableCollection<CoachMessageVm> CoachTranscript { get; } = new();
    public bool CoachEmpty => CoachTranscript.Count == 0;
    [ObservableProperty] private string _coachInput = "";
    [ObservableProperty] private bool _isCoachSending;
    [ObservableProperty] private string _coachError = "";
    public ObservableCollection<CoachAttachmentVm> CoachAttachments { get; } = new();
    public bool HasCoachAttachments => CoachAttachments.Count > 0;
    public ObservableCollection<string> CoachCompanyOptions { get; } = new();
    [ObservableProperty] private object? _coachCompanyPick;          // AutoCompleteBox selection; none-label = general thread
    [ObservableProperty] private string _coachVisionWarning = "";
    private CancellationTokenSource? _coachCts;
    private string _coachMarket = "";                                 // pinned on view open
    /// <summary>Set by the view: scrolls the transcript to the newest message (after layout).</summary>
    public Action? ScrollCoachToEnd;
    /// <summary>Persistent folder for pasted screenshots (they're referenced by the saved history).</summary>
    public string CoachImagesDir => Directory.CreateDirectory(Path.Combine(_root, "coach-images")).FullName;

    // One conversation thread per company ("" = the general "(sem empresa)" thread). The company
    // dropdown doubles as the thread selector; everything persists to coach-history.json.
    private Dictionary<string, List<CoachStoredMessage>> _coachThreads = new(StringComparer.OrdinalIgnoreCase);
    private string _coachThreadKey = "";
    private bool _coachSwitching;                                     // guards index churn while rebuilding options

    private string CoachKeyForPick(object? pick)
        => pick is string s && s.Length > 0 && !string.Equals(s, L("coach.company.none"), StringComparison.Ordinal)
            ? CompanyCache.Key(s) : "";

    private void OpenCoach()
    {
        RebuildCoachCompanies();
        if (!IsCoachSending) LoadCoachThread(CoachKeyForPick(CoachCompanyPick));
        _ = RefreshCoachMarketAsync();
        ShowOnly(coach: true);
        // Opened before ever visiting Jobs → quietly pull the scored cache so every employer is pickable.
        if (_all.Count == 0)
            _ = EnsureJobsLoadedAsync().ContinueWith(t =>
            {
                if (t.Result) Dispatcher.UIThread.Post(RebuildCoachCompanies);
            }, TaskScheduler.Default);
    }

    /// <summary>Company picker = research caches ∪ conversation threads ∪ employers from the scored
    /// jobs. Reports/jobs carry display names; briefs/threads are keyed lowercase only — cosmetic.</summary>
    private void RebuildCoachCompanies()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _companyCache.Values)
            if (!string.IsNullOrWhiteSpace(r.Company)) names.Add(r.Company.Trim());
        foreach (var v in _all)
            if (!string.IsNullOrWhiteSpace(v.Company)) names.Add(v.Company.Trim());
        foreach (var k in _briefCache.Keys)
            if (!names.Contains(k)) names.Add(k);
        foreach (var k in _coachThreads.Keys)
            if (k.Length > 0 && !names.Contains(k)) names.Add(k);

        _coachSwitching = true;
        try
        {
            string current = CoachCompanyPick as string ?? "";
            CoachCompanyOptions.Clear();
            CoachCompanyOptions.Add(L("coach.company.none"));
            foreach (var n in names) CoachCompanyOptions.Add(n);
            CoachCompanyPick = CoachCompanyOptions.Contains(current) ? current : CoachCompanyOptions[0];
        }
        finally { _coachSwitching = false; }
    }

    /// <summary>Picker change = conversation switch: stash the current thread, load the new one.</summary>
    partial void OnCoachCompanyPickChanged(object? value)
    {
        if (_coachSwitching || IsCoachSending) return;
        StashCoachThread();
        LoadCoachThread(CoachKeyForPick(value));
    }

    /// <summary>Writes the on-screen transcript into the thread map + disk (in-flight bubbles skipped).</summary>
    private void StashCoachThread()
    {
        var msgs = CoachTranscript
            .Where(m => !m.IsStreaming && (m.Text.Length > 0 || m.ImagePaths is { Count: > 0 }))
            .Select(m => new CoachStoredMessage { IsUser = m.IsUser, Text = m.Text, Images = m.ImagePaths?.ToList() })
            .ToList();
        if (msgs.Count > 0) _coachThreads[_coachThreadKey] = msgs;
        else _coachThreads.Remove(_coachThreadKey);
        CoachHistory.Save(_coachHistoryPath, _coachThreads);
    }

    private void LoadCoachThread(string key)
    {
        _coachThreadKey = key;
        CoachTranscript.Clear();
        if (_coachThreads.TryGetValue(key, out var msgs))
            foreach (var m in msgs)
                CoachTranscript.Add(new CoachMessageVm(m.IsUser, m.Text, m.Images));
        CoachError = "";
        OnPropertyChanged(nameof(CoachEmpty));
        ScrollCoachToEnd?.Invoke();
    }

    private async Task RefreshCoachMarketAsync()
    {
        try { var s = await BuildMarketSignalAsync(); _coachMarket = s.HasData ? s.ToMarketContext() : ""; }
        catch { _coachMarket = ""; }
    }

    [RelayCommand]
    private async Task SendCoach()
    {
        string text = (CoachInput ?? "").Trim();
        if (IsCoachSending || (text.Length == 0 && CoachAttachments.Count == 0)) return;
        CoachError = "";
        var images = CoachAttachments.Select(a => a.Path).ToList();
        CoachTranscript.Add(new CoachMessageVm(isUser: true, text, images.Count > 0 ? images : null));
        OnPropertyChanged(nameof(CoachEmpty));
        CoachInput = "";
        foreach (var a in CoachAttachments) a.Dispose();   // free the thumbs; keep the FILES (history references them)
        CoachAttachments.Clear();
        OnPropertyChanged(nameof(HasCoachAttachments));
        CoachVisionWarning = "";
        ScrollCoachToEnd?.Invoke();

        string? companyBlock = null;
        if (CoachKeyForPick(CoachCompanyPick) is { Length: > 0 } companyKey && CoachCompanyPick is string companyName)
        {
            _companyCache.TryGetValue(companyKey, out var rep);
            _briefCache.TryGetValue(companyKey, out var brief);
            companyBlock = Coach.FormatCompanyContext(companyName, rep, brief);
        }
        string system = Coach.BuildSystemPrompt(_profile, _coachMarket, companyBlock);
        var history = CoachTranscript
            .Select(m => new ChatMessage(m.IsUser ? "user" : "assistant", m.Text, m.ImagePaths))
            .ToList();
        var capped = Coach.Cap(history, system.Length, UsingLocalEngine ? Coach.OpenAiBudget : Coach.CliBudget);

        var answerVm = new CoachMessageVm(isUser: false, "") { IsStreaming = true };
        CoachTranscript.Add(answerVm);
        IsCoachSending = true;
        _coachCts = new CancellationTokenSource();
        var onDelta = UsingLocalEngine   // streaming only on the OpenAI path; the CLI shows a spinner
            ? new Progress<string>(d => Dispatcher.UIThread.Post(() =>
                { answerVm.Text += d; ScrollCoachToEnd?.Invoke(); }))
            : null;
        try
        {
            string? answer = await LlmClient.ChatAsync(_cfg.Claude, system, capped, onDelta, _coachCts.Token);
            if (!string.IsNullOrWhiteSpace(answer)) answerVm.Text = answer;   // final authoritative text
            else if (string.IsNullOrWhiteSpace(answerVm.Text))
            {
                CoachTranscript.Remove(answerVm);
                CoachError = LlmClient.LastError ?? L("llm.empty");
            }
        }
        catch (OperationCanceledException)
        {
            if (string.IsNullOrWhiteSpace(answerVm.Text)) CoachTranscript.Remove(answerVm);
        }
        catch (Exception ex)
        {
            CoachTranscript.Remove(answerVm);
            CoachError = Loc.Instance.F("error.generic", ex.Message);
        }
        finally
        {
            answerVm.IsStreaming = false;
            IsCoachSending = false;
            OnPropertyChanged(nameof(CoachEmpty));
            ScrollCoachToEnd?.Invoke();
            StashCoachThread();   // persist the turn (user msg + any answer) to coach-history.json
        }
    }

    [RelayCommand] private void StopCoach() => _coachCts?.Cancel();

    /// <summary>Clears the ACTIVE company's conversation only (threads are per-company). Pasted
    /// screenshots referenced only by this thread are deleted; picker-attached originals are never touched.</summary>
    [RelayCommand]
    private void ClearCoach()
    {
        _coachCts?.Cancel();
        CoachError = ""; CoachVisionWarning = "";
        foreach (var a in CoachAttachments) a.Dispose();
        CoachAttachments.Clear();
        OnPropertyChanged(nameof(HasCoachAttachments));

        var mine = CoachTranscript.SelectMany(m => m.ImagePaths ?? (IReadOnlyList<string>)Array.Empty<string>())
            .Where(p => p.StartsWith(CoachImagesDir, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        CoachTranscript.Clear();
        _coachThreads.Remove(_coachThreadKey);
        CoachHistory.Save(_coachHistoryPath, _coachThreads);

        var elsewhere = _coachThreads.Values.SelectMany(t => t)
            .SelectMany(m => m.Images ?? new List<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in mine)
            if (!elsewhere.Contains(p))
                try { File.Delete(p); } catch { }

        OnPropertyChanged(nameof(CoachEmpty));
    }

    /// <summary>Adds an image (from the picker or Ctrl+V) as a pending attachment. Silently ignores
    /// files Avalonia can't decode — for pasted clipboard data that means "not really an image".</summary>
    public void AddCoachImage(string path)
    {
        try { CoachAttachments.Add(new CoachAttachmentVm(path)); }
        catch { return; }
        OnPropertyChanged(nameof(HasCoachAttachments));
        _ = UpdateCoachVisionWarningAsync();
    }

    [RelayCommand]
    private void RemoveCoachAttachment(CoachAttachmentVm? a)
    {
        if (a is null) return;
        a.Dispose();
        CoachAttachments.Remove(a);
        OnPropertyChanged(nameof(HasCoachAttachments));
        if (CoachAttachments.Count == 0) CoachVisionWarning = "";
    }

    /// <summary>Best-effort "does the local model see images?" warning. Never blocks sending —
    /// the Claude CLI always reads images, and LM Studio can't be probed (soft warning).</summary>
    private async Task UpdateCoachVisionWarningAsync()
    {
        CoachVisionWarning = "";
        if (CoachAttachments.Count == 0 || !UsingLocalEngine) return;
        bool heur = System.Text.RegularExpressions.Regex.IsMatch(_cfg.Claude.Model ?? "",
            "llava|vision|vl|minicpm|moondream|gemma3|pixtral|internvl",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (heur) return;
        bool? cap = await LlmClient.DetectVisionAsync(_cfg.Claude.BaseUrl, _cfg.Claude.Model);
        if (CoachAttachments.Count == 0) return;   // removed meanwhile
        if (cap == false) CoachVisionWarning = L("coach.vision.no");
        else if (cap is null) CoachVisionWarning = L("coach.vision.unknown");
    }

    // ---- CV Studio (structured CV: sections editor + templates + PDF export + AI assistant) ----
    private CvDocument? _cvDoc;
    private bool _cvLoaded;                                          // lazy-open guard
    public bool HasCvDoc => _cvDoc is not null;
    private readonly List<string> _cvUndo = new();                   // JSON snapshots (chat/import applies)
    public bool CanUndoCv => _cvUndo.Count > 0;
    [ObservableProperty] private string _cvChangeNote = "";
    [ObservableProperty] private string _cvStatus = "";
    [ObservableProperty] private string _cvTailoredChip = "";
    public bool HasCvTailored => !string.IsNullOrEmpty(CvTailoredChip);
    partial void OnCvTailoredChipChanged(string value) => OnPropertyChanged(nameof(HasCvTailored));

    // Header / summary / flat-list editor fields (lists follow the app's text-field conventions).
    [ObservableProperty] private string _cvFullName = "";
    [ObservableProperty] private string _cvHeadline = "";
    [ObservableProperty] private string _cvEmail = "";
    [ObservableProperty] private string _cvPhone = "";
    [ObservableProperty] private string _cvLocation = "";
    [ObservableProperty] private string _cvLinksText = "";           // one per line: Label | URL
    [ObservableProperty] private string _cvSummary = "";
    [ObservableProperty] private string _cvSkillGroupsText = "";     // one group per line: Label: a, b, c
    [ObservableProperty] private string _cvCertsText = "";           // one per line
    [ObservableProperty] private string _cvLanguagesText = "";       // comma-separated
    public ObservableCollection<CvExperienceVm> CvExperience { get; } = new();
    public ObservableCollection<CvEducationVm> CvEducation { get; } = new();
    public ObservableCollection<CvProjectVm> CvProjects { get; } = new();

    // Presentation settings (app-owned; the assistant is never allowed to change the visual ones).
    public string[] CvTemplateOptions => CvTemplates.All.Select(t => L(t.LocKey)).ToArray();
    [ObservableProperty] private int _cvTemplateIndex;
    public string[] CvLangOptions => new[] { L("opt.cvlang.pt"), L("opt.cvlang.en") };
    [ObservableProperty] private int _cvLangIndex;
    private static readonly string[] CvAccentHex = { "#4C2DBE", "#1E5AA8", "#1A7F4B", "#8C2F39", "#333333", "#0F766E" };
    public string[] CvAccentOptions => new[]
    {
        L("cv.accent.violet"), L("cv.accent.blue"), L("cv.accent.green"),
        L("cv.accent.wine"), L("cv.accent.graphite"), L("cv.accent.teal"),
    };
    [ObservableProperty] private int _cvAccentIndex;

    private void OpenCvStudio()
    {
        if (!_cvLoaded)
        {
            _cvDoc = CvStore.Load(_cvPath);
            if (_cvDoc is not null) LoadCvEditors();
            _cvLoaded = true;
        }
        LoadCvChat();
        _ = RefreshCvJobsAsync();
        ShowOnly(cv: true);
    }

    /// <summary>Document → editor fields (after load, import, chat apply or undo).</summary>
    private void LoadCvEditors()
    {
        var d = _cvDoc!;
        CvFullName = d.Header.FullName; CvHeadline = d.Header.Title; CvEmail = d.Header.Email;
        CvPhone = d.Header.Phone; CvLocation = d.Header.Location;
        CvLinksText = string.Join("\n", d.Header.Links.Select(l =>
            string.IsNullOrWhiteSpace(l.Label) ? l.Url : $"{l.Label} | {l.Url}"));
        CvSummary = d.Summary;
        CvExperience.Clear(); foreach (var e in d.Experience) CvExperience.Add(CvExperienceVm.From(e));
        CvEducation.Clear(); foreach (var e in d.Education) CvEducation.Add(CvEducationVm.From(e));
        CvProjects.Clear(); foreach (var p in d.Projects) CvProjects.Add(CvProjectVm.From(p));
        CvSkillGroupsText = string.Join("\n", d.SkillGroups.Select(g =>
            string.IsNullOrWhiteSpace(g.Label) ? string.Join(", ", g.Skills) : $"{g.Label}: {string.Join(", ", g.Skills)}"));
        CvCertsText = string.Join("\n", d.Certifications);
        CvLanguagesText = string.Join(", ", d.Languages);
        CvTemplateIndex = Math.Max(0, Array.FindIndex(CvTemplates.All, t => t.Id == d.TemplateId));
        CvLangIndex = d.Lang == "en" ? 1 : 0;
        CvAccentIndex = Math.Max(0, Array.IndexOf(CvAccentHex, d.AccentColor));
        CvTailoredChip = d.TailoredFor.Length > 0 ? Loc.Instance.F("cv.tailor.chip", d.TailoredFor) : "";
        OnPropertyChanged(nameof(HasCvDoc));
        OnPropertyChanged(nameof(HasCvPhoto));
    }

    /// <summary>Editor fields → document (before save/preview/export/chat and on leaving the view).</summary>
    private void CommitCvEditors()
    {
        if (_cvDoc is null) return;
        var d = _cvDoc;
        d.Header.FullName = CvFullName.Trim(); d.Header.Title = CvHeadline.Trim();
        d.Header.Email = CvEmail.Trim(); d.Header.Phone = CvPhone.Trim(); d.Header.Location = CvLocation.Trim();
        d.Header.Links = CvLinksText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                int bar = line.IndexOf('|');
                return bar < 0
                    ? new CvLink { Url = line }
                    : new CvLink { Label = line[..bar].Trim(), Url = line[(bar + 1)..].Trim() };
            })
            .Where(l => l.Url.Length > 0 || l.Label.Length > 0).ToList();
        d.Summary = CvSummary.Trim();
        d.Experience = CvExperience.Select(v => v.ToModel()).ToList();
        d.Education = CvEducation.Select(v => v.ToModel()).ToList();
        d.Projects = CvProjects.Select(v => v.ToModel()).ToList();
        d.SkillGroups = CvSkillGroupsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                int colon = line.IndexOf(':');
                string label = colon < 0 ? "" : line[..colon].Trim();
                string rest = colon < 0 ? line : line[(colon + 1)..];
                return new CvSkillGroup { Label = label, Skills = Split(rest) };
            })
            .Where(g => g.Skills.Count > 0).ToList();
        d.Certifications = CvCertsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        d.Languages = Split(CvLanguagesText);
        d.TemplateId = CvTemplates.All[Math.Clamp(CvTemplateIndex, 0, CvTemplates.All.Length - 1)].Id;
        d.Lang = CvLangIndex == 1 ? "en" : "pt";
        d.AccentColor = CvAccentHex[Math.Clamp(CvAccentIndex, 0, CvAccentHex.Length - 1)];
    }

    private void SaveCvDoc(bool announce)
    {
        if (_cvDoc is null) return;
        CvStore.Save(_cvPath, _cvDoc);
        if (announce) CvStatus = L("cv.saved");
    }

    [RelayCommand]
    private void SaveCv()
    {
        CommitCvEditors();
        SaveCvDoc(announce: true);
    }

    [RelayCommand]
    private void SeedCvFromProfile()
    {
        if (_cvDoc is not null) PushCvUndo();
        _cvDoc = CvStudio.FromProfile(_profile);
        LoadCvEditors();
        SaveCvDoc(false);
        CvStatus = "";
    }

    /// <summary>Called by the view after the PDF picker: extract text + LLM-import the full document.</summary>
    public async Task ImportCvForStudioAsync(string path)
    {
        Busy = true; CvStatus = L("cv.importing");
        try
        {
            string text = "";
            try { text = await Task.Run(() => CvProfiler.ExtractText(path)); } catch { }
            var doc = string.IsNullOrWhiteSpace(text) ? null
                : await CvStudio.ImportAsync(text, _profile, _cfg.Claude);
            if (doc is null)
            {
                CvStatus = L("cv.import.failed") +
                    (string.IsNullOrWhiteSpace(LlmClient.LastError) ? "" : $" ({LlmClient.LastError})");
                return;
            }
            if (_cvDoc is not null) PushCvUndo();
            _cvDoc = doc;
            LoadCvEditors();
            SaveCvDoc(false);
            CvStatus = "";
        }
        finally { Busy = false; }
    }

    private string CvSafeName()
        => string.Concat((string.IsNullOrWhiteSpace(CvFullName) ? "CV" : CvFullName)
            .Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');

    /// <summary>Opens the SELECTED template rendered with John Doe sample data — template browsing
    /// without touching the user's document.</summary>
    [RelayCommand]
    private void PreviewTemplateSample()
    {
        try
        {
            var sample = CvTemplates.SampleDoc(CvLangIndex == 1 ? "en" : "pt");
            sample.TemplateId = CvTemplates.All[Math.Clamp(CvTemplateIndex, 0, CvTemplates.All.Length - 1)].Id;
            sample.AccentColor = CvAccentHex[Math.Clamp(CvAccentIndex, 0, CvAccentHex.Length - 1)];
            string outDir = Path.Combine(_root, "output");
            Directory.CreateDirectory(outDir);
            string html = Path.Combine(outDir, "cv-template-sample.html");
            File.WriteAllText(html, CvTemplates.Render(sample), new System.Text.UTF8Encoding(false));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = html, UseShellExecute = true });
        }
        catch (Exception ex) { CvStatus = Loc.Instance.F("cv.export.failed", ex.Message); }
    }

    public bool HasCvPhoto => _cvDoc is { PhotoPath.Length: > 0 };

    /// <summary>Copies the picked photo next to the CV data (cv-photo.*, machine-local) and attaches it.</summary>
    public void SetCvPhoto(string path)
    {
        if (_cvDoc is null) return;
        try
        {
            foreach (var old in Directory.GetFiles(_root, "cv-photo.*")) File.Delete(old);
            string dest = Path.Combine(_root, "cv-photo" + Path.GetExtension(path).ToLowerInvariant());
            File.Copy(path, dest, overwrite: true);
            _cvDoc.PhotoPath = dest;
            SaveCvDoc(false);
            CvStatus = L("cv.photo.set");
            OnPropertyChanged(nameof(HasCvPhoto));
        }
        catch (Exception ex) { CvStatus = Loc.Instance.F("error.generic", ex.Message); }
    }

    [RelayCommand]
    private void RemoveCvPhoto()
    {
        if (_cvDoc is null) return;
        try { if (_cvDoc.PhotoPath.Length > 0 && File.Exists(_cvDoc.PhotoPath)) File.Delete(_cvDoc.PhotoPath); } catch { }
        _cvDoc.PhotoPath = "";
        SaveCvDoc(false);
        CvStatus = "";
        OnPropertyChanged(nameof(HasCvPhoto));
    }

    [RelayCommand]
    private void PreviewCv()
    {
        if (_cvDoc is null) return;
        CommitCvEditors(); SaveCvDoc(false);
        try
        {
            string outDir = Path.Combine(_root, "output");
            Directory.CreateDirectory(outDir);
            string html = Path.Combine(outDir, $"{CvSafeName()}-CV.html");
            File.WriteAllText(html, CvTemplates.Render(_cvDoc), new System.Text.UTF8Encoding(false));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = html, UseShellExecute = true });
        }
        catch (Exception ex) { CvStatus = Loc.Instance.F("cv.export.failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportCvPdf()
    {
        if (_cvDoc is null) return;
        CommitCvEditors(); SaveCvDoc(false);
        Busy = true;
        try
        {
            string outDir = Path.Combine(_root, "output");
            Directory.CreateDirectory(outDir);
            string suffix = _cvDoc.TailoredFor.Length > 0
                ? "-" + string.Concat(_cvDoc.TailoredFor.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_')
                : "";
            string baseName = $"{CvSafeName()}-CV{suffix}";
            string content = CvTemplates.Render(_cvDoc);
            string? open = await Task.Run(() =>
            {
                string html = Path.Combine(outDir, baseName + ".html");
                File.WriteAllText(html, content, new System.Text.UTF8Encoding(false));
                string? edge = Reports.FindEdge();
                if (edge is null) return html;                        // no Edge — hand back the HTML
                string pdf = Path.Combine(outDir, baseName + ".pdf");
                if (Reports.WritePdf(html, pdf, edge) && File.Exists(pdf)) return pdf;
                // Likely locked by a PDF viewer — retry once with a stamped name.
                pdf = Path.Combine(outDir, $"{baseName}-{DateTime.Now:HHmmss}.pdf");
                return Reports.WritePdf(html, pdf, edge) && File.Exists(pdf) ? pdf : html;
            });
            CvStatus = Loc.Instance.F("cv.exported", open);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = open, UseShellExecute = true });
        }
        catch (Exception ex) { CvStatus = Loc.Instance.F("cv.export.failed", ex.Message); }
        finally { Busy = false; }
    }

    // Row commands (Filters pattern): add / remove / move ±1 per section.
    private static void MoveRow<T>(ObservableCollection<T> c, T? item, int delta)
    {
        if (item is null) return;
        int i = c.IndexOf(item), j = i + delta;
        if (i < 0 || j < 0 || j >= c.Count) return;
        c.Move(i, j);
    }

    [RelayCommand] private void AddCvExperience() => CvExperience.Add(new CvExperienceVm());
    [RelayCommand] private void RemoveCvExperience(CvExperienceVm? v) { if (v is not null) CvExperience.Remove(v); }
    [RelayCommand] private void MoveCvExperienceUp(CvExperienceVm? v) => MoveRow(CvExperience, v, -1);
    [RelayCommand] private void MoveCvExperienceDown(CvExperienceVm? v) => MoveRow(CvExperience, v, +1);
    [RelayCommand] private void AddCvEducation() => CvEducation.Add(new CvEducationVm());
    [RelayCommand] private void RemoveCvEducation(CvEducationVm? v) { if (v is not null) CvEducation.Remove(v); }
    [RelayCommand] private void MoveCvEducationUp(CvEducationVm? v) => MoveRow(CvEducation, v, -1);
    [RelayCommand] private void MoveCvEducationDown(CvEducationVm? v) => MoveRow(CvEducation, v, +1);
    [RelayCommand] private void AddCvProject() => CvProjects.Add(new CvProjectVm());
    [RelayCommand] private void RemoveCvProject(CvProjectVm? v) { if (v is not null) CvProjects.Remove(v); }
    [RelayCommand] private void MoveCvProjectUp(CvProjectVm? v) => MoveRow(CvProjects, v, -1);
    [RelayCommand] private void MoveCvProjectDown(CvProjectVm? v) => MoveRow(CvProjects, v, +1);

    private void PushCvUndo()
    {
        if (_cvDoc is null) return;
        _cvUndo.Add(JsonSerializer.Serialize(_cvDoc));
        if (_cvUndo.Count > 10) _cvUndo.RemoveAt(0);
        OnPropertyChanged(nameof(CanUndoCv));
    }

    [RelayCommand]
    private void UndoCvChange()
    {
        if (_cvUndo.Count == 0) return;
        string snap = _cvUndo[^1];
        _cvUndo.RemoveAt(_cvUndo.Count - 1);
        try
        {
            var doc = JsonSerializer.Deserialize<CvDocument>(snap,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (doc is null) return;
            CvStore.Normalize(doc);
            _cvDoc = doc;
        }
        catch { return; }
        LoadCvEditors();
        SaveCvDoc(false);
        CvChangeNote = L("cv.chat.undone");
        OnPropertyChanged(nameof(CanUndoCv));
    }

    // ---- tailor: tune the CV to one of the scored jobs (the app has the full posting text) ----
    public ObservableCollection<string> CvJobOptions { get; } = new();
    private List<JobEntity> _cvJobs = new();
    [ObservableProperty] private object? _cvJobPick;   // AutoCompleteBox selection (type-to-search)
    public bool HasCvJobs => _cvJobs.Count > 0;

    /// <summary>Job picker = loaded jobs (quietly pulled from the scored cache when needed),
    /// deduped by Title+Company keeping the best score, best-first.</summary>
    private async Task RefreshCvJobsAsync()
    {
        await EnsureJobsLoadedAsync();
        _cvJobs = _all.Select(v => v.Entity)
            .Where(j => !string.IsNullOrWhiteSpace(j.Title))
            .GroupBy(j => $"{j.Title.Trim()}|{(j.Company ?? "").Trim()}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(j => j.FinalScore).First())
            .OrderByDescending(j => j.FinalScore)
            .Take(100).ToList();
        CvJobOptions.Clear();
        foreach (var j in _cvJobs) CvJobOptions.Add($"{j.Title} @ {j.Company}");
        CvJobPick = null;
        OnPropertyChanged(nameof(HasCvJobs));
    }

    [RelayCommand]
    private Task TailorCvToJob()
    {
        int i = CvJobPick is string s ? CvJobOptions.IndexOf(s) : -1;
        if (i < 0 || i >= _cvJobs.Count) return Task.CompletedTask;
        var job = _cvJobs[i];
        return SendCvChatCore(L("cv.tailor.msg"), CvStudio.BuildTailorJobBlock(job), job.Company);
    }

    // ---- CV assistant chat: critiques AND applies edits ({"reply","cv"} contract) ----
    public ObservableCollection<CoachMessageVm> CvChatTranscript { get; } = new();
    public bool CvChatEmpty => CvChatTranscript.Count == 0;
    [ObservableProperty] private string _cvChatInput = "";
    [ObservableProperty] private bool _isCvChatSending;
    [ObservableProperty] private string _cvChatError = "";
    private CancellationTokenSource? _cvChatCts;
    private bool _cvChatLoaded;
    /// <summary>Set by the view: scrolls the assistant transcript to the newest message.</summary>
    public Action? ScrollCvChatToEnd;

    private void LoadCvChat()
    {
        if (_cvChatLoaded) return;
        _cvChatLoaded = true;
        try
        {
            if (!File.Exists(_cvChatPath)) return;
            var msgs = JsonSerializer.Deserialize<List<CoachStoredMessage>>(File.ReadAllText(_cvChatPath));
            if (msgs is null) return;
            foreach (var m in msgs) CvChatTranscript.Add(new CoachMessageVm(m.IsUser, m.Text));
            OnPropertyChanged(nameof(CvChatEmpty));
        }
        catch { /* ignore a bad chat file */ }
    }

    private void SaveCvChat()
    {
        try
        {
            var msgs = CvChatTranscript.Where(m => !m.IsStreaming && m.Text.Length > 0)
                .Select(m => new CoachStoredMessage { IsUser = m.IsUser, Text = m.Text }).ToList();
            File.WriteAllText(_cvChatPath, JsonSerializer.Serialize(msgs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    [RelayCommand]
    private Task SendCvChat() => SendCvChatCore(CvChatInput, null, null);

    [RelayCommand] private void StopCvChat() => _cvChatCts?.Cancel();

    [RelayCommand]
    private void ClearCvChat()
    {
        _cvChatCts?.Cancel();
        CvChatTranscript.Clear();
        CvChatError = "";
        try { File.Delete(_cvChatPath); } catch { }
        OnPropertyChanged(nameof(CvChatEmpty));
    }

    private async Task SendCvChatCore(string userText, string? jobBlock, string? tailoredFor)
    {
        userText = (userText ?? "").Trim();
        if (IsCvChatSending || _cvDoc is null || userText.Length == 0) return;
        CommitCvEditors(); SaveCvDoc(false);              // the assistant sees exactly what's on screen
        CvChatError = ""; CvChangeNote = "";
        CvChatTranscript.Add(new CoachMessageVm(isUser: true, userText));
        CvChatInput = "";
        OnPropertyChanged(nameof(CvChatEmpty));
        ScrollCvChatToEnd?.Invoke();

        var history = CvChatTranscript.Where(m => !m.IsStreaming && m.Text.Length > 0)
            .Select(m => (m.IsUser, m.Text)).ToList();
        if (history.Count > 0) history.RemoveAt(history.Count - 1);   // the latest message travels separately
        if (history.Count > 6) history.RemoveRange(0, history.Count - 6);
        string prompt = CvStudio.BuildChatPrompt(_cvDoc, history, userText, jobBlock);

        var answerVm = new CoachMessageVm(isUser: false, "") { IsStreaming = true };
        CvChatTranscript.Add(answerVm);
        IsCvChatSending = true;
        _cvChatCts = new CancellationTokenSource();
        try
        {
            string? raw = await LlmClient.CompleteAsync(CvStudio.WithCvBudget(_cfg.Claude), prompt, _cvChatCts.Token, json: true);
            if (string.IsNullOrWhiteSpace(raw))
            {
                CvChatTranscript.Remove(answerVm);
                CvChatError = LlmClient.LastError ?? L("llm.empty");
                return;
            }
            var (reply, newCv, triedButInvalid) = CvStudio.ParseChatReply(raw, _cvDoc);
            answerVm.Text = reply.Length > 0 ? reply : L("cv.chat.notApplied");
            if (newCv is not null)
            {
                var sections = CvStudio.ChangedSections(_cvDoc, newCv);
                PushCvUndo();                              // snapshot BEFORE apply
                if (tailoredFor is not null) newCv.TailoredFor = tailoredFor;
                _cvDoc = newCv;
                LoadCvEditors();
                SaveCvDoc(false);
                if (sections.Count > 0)
                    CvChangeNote = Loc.Instance.F("cv.chat.applied",
                        string.Join(", ", sections.Select(s => L("cv.sec." + s))));
            }
            else if (triedButInvalid && reply.Length > 0)
                answerVm.Text += "\n\n" + L("cv.chat.notApplied");
        }
        catch (OperationCanceledException)
        {
            if (answerVm.Text.Length == 0) CvChatTranscript.Remove(answerVm);
        }
        catch (Exception ex)
        {
            CvChatTranscript.Remove(answerVm);
            CvChatError = Loc.Instance.F("error.generic", ex.Message);
        }
        finally
        {
            answerVm.IsStreaming = false;
            IsCvChatSending = false;
            OnPropertyChanged(nameof(CvChatEmpty));
            ScrollCvChatToEnd?.Invoke();
            SaveCvChat();
        }
    }

    /// <summary>Loads the LLM backend override saved from the Settings screen (machine-local, gitignored).</summary>
    private void ApplyLlmOverride()
    {
        try
        {
            if (!File.Exists(_llmSettingsPath)) return;
            var c = JsonSerializer.Deserialize<ClaudeConfig>(File.ReadAllText(_llmSettingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (c is not null) _cfg.Claude = c;
        }
        catch { /* ignore a bad settings file */ }
    }

    private void SaveLlmSettings()
    {
        try { File.WriteAllText(_llmSettingsPath, JsonSerializer.Serialize(_cfg.Claude, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* best-effort */ }
    }

    /// <summary>Loads the Apify connector settings (machine-local, gitignored — contains a secret token).</summary>
    private void ApplyApifyOverride()
    {
        try
        {
            if (!File.Exists(_apifySettingsPath)) return;
            var c = JsonSerializer.Deserialize<ApifyConfig>(File.ReadAllText(_apifySettingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (c is not null) _cfg.Apify = c;
        }
        catch { /* ignore */ }
    }

    private void SaveApifySettings()
    {
        try { File.WriteAllText(_apifySettingsPath, JsonSerializer.Serialize(_cfg.Apify, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* best-effort */ }
    }

    private void ApplyJSearchOverride()
    {
        try
        {
            if (!File.Exists(_jsearchSettingsPath)) return;
            var c = JsonSerializer.Deserialize<JSearchConfig>(File.ReadAllText(_jsearchSettingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (c is not null) _cfg.JSearch = c;
        }
        catch { /* ignore */ }
    }

    private void SaveJSearchSettings()
    {
        try { File.WriteAllText(_jsearchSettingsPath, JsonSerializer.Serialize(_cfg.JSearch, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* best-effort */ }
    }

    private void ApplyJobicyOverride()
    {
        try
        {
            if (!File.Exists(_jobicySettingsPath)) return;
            var c = JsonSerializer.Deserialize<JobicyConfig>(File.ReadAllText(_jobicySettingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (c is not null) _cfg.Jobicy = c;
        }
        catch { /* ignore */ }
    }

    private void SaveJobicySettings()
    {
        try { File.WriteAllText(_jobicySettingsPath, JsonSerializer.Serialize(_cfg.Jobicy, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* best-effort */ }
    }

    private void ApplyHimalayasOverride()
    {
        try
        {
            if (!File.Exists(_himalayasSettingsPath)) return;
            var c = JsonSerializer.Deserialize<HimalayasConfig>(File.ReadAllText(_himalayasSettingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (c is not null) _cfg.Himalayas = c;
        }
        catch { /* ignore */ }
    }

    private void SaveHimalayasSettings()
    {
        try { File.WriteAllText(_himalayasSettingsPath, JsonSerializer.Serialize(_cfg.Himalayas, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* best-effort */ }
    }

    private void LoadFormFromProfile()
    {
        Name = _profile.Name;
        Summary = _profile.Summary;
        FieldText = _profile.Field;
        JobTitlesText = string.Join(", ", _profile.JobTitles);
        CoreSkillsText = string.Join(", ", _profile.CoreSkills);
        SkillsText = string.Join(", ", _profile.Skills);
        LocText = string.Join(", ", _profile.Locations);
        YearsText = _profile.YearsExperience.ToString();
        Seniority = string.IsNullOrWhiteSpace(_profile.SeniorityTarget) ? "mid" : _profile.SeniorityTarget;
        SalaryFloorText = _profile.SalaryFloorEur > 0 ? _profile.SalaryFloorEur.ToString() : "";
        SalaryTargetText = _profile.SalaryTargetEur > 0 ? _profile.SalaryTargetEur.ToString() : "";
    }

    private void CommitFormToProfile()
    {
        _profile.Name = Name;
        _profile.Summary = Summary;
        _profile.Field = FieldText;
        _profile.JobTitles = Split(JobTitlesText);
        _profile.CoreSkills = Split(CoreSkillsText);
        _profile.Skills = Split(SkillsText);
        _profile.Locations = Split(LocText);
        _profile.YearsExperience = int.TryParse(YearsText, out var y) ? y : 0;
        _profile.SeniorityTarget = Seniority;
        _profile.SalaryFloorEur = int.TryParse(SalaryFloorText, out var f) ? f : 0;
        _profile.SalaryTargetEur = int.TryParse(SalaryTargetText, out var t) ? t : 0;
        _profile.Remote = Remote; _profile.Hybrid = Hybrid; _profile.Onsite = Onsite;
    }

    private static List<string> Split(string s) =>
        (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private AppConfig LoadConfig()
    {
        try
        {
            string p = Path.Combine(_root, "appsettings.json");
            if (File.Exists(p))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(p),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception ex) { Diag.Error("appsettings.json load failed — using defaults", ex); }
        return new AppConfig();
    }

    private static string FindRoot()
    {
        // Installed build: the install dir is read-only, so seed + use a per-user data dir.
        if (AppPaths.IsPackaged(AppContext.BaseDirectory))
            return AppPaths.EnsureSeeded(AppContext.BaseDirectory);

        // Dev build: walk up to the repo root (unchanged behaviour).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "fetcher-config.json")) ||
                Directory.Exists(Path.Combine(dir.FullName, "samples")) ||
                Directory.Exists(Path.Combine(dir.FullName, "fetcher")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
