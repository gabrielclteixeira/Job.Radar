using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
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
    private readonly string _planPath;
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
        _planPath = Path.Combine(_root, "career-plan.json");
        _cfg = LoadConfig();
        ApplyLlmOverride();
        ApplyApifyOverride();
        LoadUiSettings();
        ApplyTheme();
        LoadSavedProfile();
        LoadPlan();
    }

    // ---- navigation (sidebar) ----
    [ObservableProperty] private string _nav = "home";
    public bool IsNavHome => Nav == "home";
    public bool IsNavProfile => Nav == "profile";
    public bool IsNavResults => Nav == "results";
    public bool IsNavImprove => Nav == "improve";
    public bool IsNavSettings => Nav == "settings";
    partial void OnNavChanged(string value)
    {
        OnPropertyChanged(nameof(IsNavHome)); OnPropertyChanged(nameof(IsNavProfile));
        OnPropertyChanged(nameof(IsNavResults)); OnPropertyChanged(nameof(IsNavImprove));
        OnPropertyChanged(nameof(IsNavSettings));
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        switch (page)
        {
            case "profile": EditProfile(); break;          // loads the form + shows profile
            case "results":                                  // show loaded jobs, or load from cache if none yet
                if (_all.Count == 0 && !IsScoring) _ = ViewJobs();
                else ShowOnly(results: true);
                break;
            case "improve": ShowOnly(improve: true); break;  // career-growth area
            case "settings": OpenSettings(); break;          // loads settings fields + shows
            default: ShowOnly(welcome: true); break;          // home
        }
    }

    // ---- theme ----
    public string[] ThemeOptions { get; } = { "System", "Light", "Dark" };
    [ObservableProperty] private string _themePref = "Dark"; // System | Light | Dark
    public bool IsDark => ThemePref != "Light";
    partial void OnThemePrefChanged(string value) { ApplyTheme(); SaveUiSettings(); OnPropertyChanged(nameof(IsDark)); }

    [RelayCommand] private void ToggleTheme() => ThemePref = ThemePref == "Light" ? "Dark" : "Light";

    // ---- UI zoom (Ctrl +/- / 0, Ctrl+wheel, or the Definições stepper) ----
    private const double MinZoom = 0.8, MaxZoom = 2.0, ZoomStep = 0.1;
    [ObservableProperty] private double _zoom = 1.0;
    public string ZoomLabel => $"{Zoom * 100:0}%";
    partial void OnZoomChanged(double value) { OnPropertyChanged(nameof(ZoomLabel)); SaveUiSettings(); }

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
        }
        catch { /* ignore */ }
    }

    private void SaveUiSettings()
    {
        try { File.WriteAllText(_uiSettingsPath, JsonSerializer.Serialize(new { theme = ThemePref, zoom = Zoom }, new JsonSerializerOptions { WriteIndented = true })); }
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
    [ObservableProperty] private bool _hasJobs;
    [ObservableProperty] private bool _isSettings;
    [ObservableProperty] private bool _isImprove;

    // ---- improve (career plan) ----
    [ObservableProperty] private CareerPlanResult? _plan;
    [ObservableProperty] private bool _isPlanning;
    [ObservableProperty] private string _planStatus = "";
    [ObservableProperty] private string _planError = "";
    public bool HasPlan => Plan is not null;
    public bool ShowGenerateIntro => !HasPlan && !IsPlanning;
    partial void OnPlanChanged(CareerPlanResult? value)
    {
        OnPropertyChanged(nameof(HasPlan)); OnPropertyChanged(nameof(ShowGenerateIntro));
    }
    partial void OnIsPlanningChanged(bool value) => OnPropertyChanged(nameof(ShowGenerateIntro));

    [RelayCommand]
    private async Task GeneratePlan()
    {
        if (IsPlanning) return;
        IsPlanning = true; PlanError = ""; Plan = null;
        PlanStatus = "A preparar a pesquisa…";
        var progress = new Progress<string>(m => Dispatcher.UIThread.Post(() => PlanStatus = m));
        try
        {
            var result = await CareerPlan.GenerateAsync(_cfg.Claude, _profile, MarketContext(), progress);
            if (result is null) PlanError = "Não consegui reunir informação suficiente. Tenta outra vez.";
            else { Plan = result; SavePlan(); }
        }
        catch (Exception ex) { PlanError = "Erro: " + ex.Message; }
        finally { IsPlanning = false; }
    }

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
        }
        catch { /* ignore a bad plan file */ }
    }

    private void SavePlan()
    {
        if (Plan is null || _isDemoProfile) return; // don't persist a plan built from the sample profile
        try { File.WriteAllText(_planPath, JsonSerializer.Serialize(Plan, new JsonSerializerOptions { WriteIndented = true })); }
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
    [ObservableProperty] private string _claudeExe = "claude";

    // LinkedIn via Apify (paid connector)
    [ObservableProperty] private bool _useApify;
    [ObservableProperty] private string _apifyToken = "";
    [ObservableProperty] private string _apifyActor = "";
    [ObservableProperty] private string _apifyMax = "50";
    [ObservableProperty] private string _apifyStatus = "";
    public ObservableCollection<string> ApifyActorOptions { get; } = new();

    /// <summary>Validates the Apify token (free) and auto-fills the actor dropdown from the store.</summary>
    [RelayCommand]
    private async Task ProbeApify()
    {
        Busy = true; ApifyStatus = "A validar o token e a procurar actors…";
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

    public ObservableCollection<string> ModelOptions { get; } = new();
    private const string DefaultClaudeModel = "(predefinido)";   // maps to empty → CLI's own default
    private static readonly string[] ClaudeModels = { DefaultClaudeModel, "sonnet", "opus", "haiku" };

    partial void OnUseLocalModelChanged(bool value)
    {
        LlmModel = value ? "" : DefaultClaudeModel; // local needs an explicit model; Claude defaults
        RefreshModelOptions();
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
        Busy = true; Status = "A ler o CV…";
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
                Status = "A IA está a montar o teu perfil…";
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
        Status = "Perfil de exemplo (John Doe) — experimenta a pesquisa. Não substitui o teu perfil guardado.";
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
        // Apify is paid — confirm before spending the user's credits.
        if (_cfg.Apify.Enabled && !string.IsNullOrWhiteSpace(_cfg.Apify.Token) && ConfirmCostAsync is not null)
            if (!await ConfirmCostAsync()) return;
        await RunPipeline(useAi: UseAi, demo: false);
    }

    [RelayCommand]
    private Task RunDemo() => RunPipeline(useAi: false, demo: true);

    /// <summary>Shows the jobs already saved in the local cache, without fetching or scoring again.</summary>
    [RelayCommand]
    private async Task ViewJobs()
    {
        Log.Clear(); _all = new(); Jobs.Clear(); HasJobs = false; ExportMsg = "";
        ResultsTitle = "Vagas guardadas";
        ScoringStatus = "A carregar vagas guardadas…"; MinScore = 0; IsScoring = true;
        ShowOnly(results: true); Busy = true;
        var jobProg = new Progress<JobEntity>(j => Dispatcher.UIThread.Post(() => AddStreamed(j)));
        try
        {
            var result = await Pipeline.LoadCachedAsync(_cfg, _root, jobProg);
            FinalizeResults(result);
            if (!HasJobs) ScoringStatus = "Ainda não há vagas guardadas — usa \"Procurar vagas\".";
        }
        catch (Exception ex) { ScoringStatus = "Erro: " + ex.Message; }
        finally { IsScoring = false; Busy = false; }
    }

    /// <summary>Re-scores the cached jobs with the current model/engine (after changing it in Settings).</summary>
    [RelayCommand]
    private async Task Rescore()
    {
        Log.Clear(); _all = new(); Jobs.Clear(); HasJobs = false; ExportMsg = "";
        ResultsTitle = "Vagas para ti"; ScoringStatus = "A reclassificar com o modelo atual…";
        MinScore = 0; IsScoring = true; ShowOnly(results: true); Busy = true;
        var logProg = new Progress<string>(m => Dispatcher.UIThread.Post(() => { Log.Add(m); ScoringStatus = m; }));
        var jobProg = new Progress<JobEntity>(j => Dispatcher.UIThread.Post(() => AddStreamed(j)));
        try
        {
            var result = await Pipeline.RescoreAsync(_profile, _cfg, _root, logProg, jobProg);
            FinalizeResults(result);
            if (!HasJobs) ScoringStatus = "Sem vagas guardadas — usa \"Procurar vagas\" primeiro.";
        }
        catch (Exception ex) { ScoringStatus = "Erro: " + ex.Message; }
        finally { IsScoring = false; Busy = false; }
    }

    /// <summary>Back to the home screen (keeps the loaded results in memory).</summary>
    [RelayCommand] private void GoHome() => ShowOnly(welcome: true);

    // ---- settings ----
    [RelayCommand]
    private void OpenSettings()
    {
        UseLocalModel = string.Equals(_cfg.Claude.Provider, "openai", StringComparison.OrdinalIgnoreCase);
        LlmBaseUrl = _cfg.Claude.BaseUrl;
        LlmApiKey = _cfg.Claude.ApiKey;
        ClaudeExe = string.IsNullOrWhiteSpace(_cfg.Claude.Exe) ? "claude" : _cfg.Claude.Exe;
        // Set model AFTER UseLocalModel so the change-handler doesn't clobber it; map empty Claude → label.
        LlmModel = (!UseLocalModel && string.IsNullOrWhiteSpace(_cfg.Claude.Model)) ? DefaultClaudeModel : _cfg.Claude.Model;
        RefreshModelOptions();
        UseApify = _cfg.Apify.Enabled;
        ApifyToken = _cfg.Apify.Token;
        ApifyActor = _cfg.Apify.ActorId;
        ApifyMax = _cfg.Apify.MaxItems > 0 ? _cfg.Apify.MaxItems.ToString() : "50";
        ApifyStatus = "";
        ApifyActorOptions.Clear();
        if (!string.IsNullOrWhiteSpace(_cfg.Apify.ActorId)) ApifyActorOptions.Add(_cfg.Apify.ActorId);
        Status = "";
        ShowOnly(settings: true);
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _cfg.Claude.Provider = UseLocalModel ? "openai" : "claude-cli";
        _cfg.Claude.BaseUrl = string.IsNullOrWhiteSpace(LlmBaseUrl) ? "http://localhost:11434/v1" : LlmBaseUrl.Trim();
        _cfg.Claude.Model = LlmModel == DefaultClaudeModel ? "" : (LlmModel ?? "").Trim();
        _cfg.Claude.ApiKey = LlmApiKey.Trim();
        _cfg.Claude.Exe = string.IsNullOrWhiteSpace(ClaudeExe) ? "claude" : ClaudeExe.Trim();
        SaveLlmSettings();
        _cfg.Apify.Enabled = UseApify;
        _cfg.Apify.Token = ApifyToken.Trim();
        _cfg.Apify.ActorId = ApifyActor.Trim();
        _cfg.Apify.MaxItems = int.TryParse(ApifyMax, out var am) && am > 0 ? am : 50;
        SaveApifySettings();
        Status = "Definições guardadas.";
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
        Busy = true; Status = "A obter modelos do runtime local…";
        try
        {
            var models = await LlmClient.ListOpenAiModelsAsync(LlmBaseUrl, LlmApiKey);
            string current = LlmModel;
            ModelOptions.Clear();
            foreach (var m in models) ModelOptions.Add(m);
            if (!string.IsNullOrWhiteSpace(current) && !ModelOptions.Contains(current)) ModelOptions.Add(current);
            if (models.Count == 0) Status = "Sem modelos — o runtime local está a correr no Base URL indicado?";
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
        Log.Clear(); _all = new(); Jobs.Clear(); HasJobs = false; ExportMsg = "";
        ResultsTitle = demo ? "Vagas para ti (demo)" : "Vagas para ti";
        ScoringStatus = demo ? "A carregar demonstração…" : "A procurar vagas…";
        MinScore = 0; IsScoring = true; ShowOnly(results: true); Busy = true;

        var logProg = new Progress<string>(m => Dispatcher.UIThread.Post(() => { Log.Add(m); ScoringStatus = m; }));
        var jobProg = new Progress<JobEntity>(j => Dispatcher.UIThread.Post(() => AddStreamed(j)));
        try
        {
            var result = await Pipeline.RunAsync(
                demo ? new UserProfile() : _profile, _cfg, _root, useAi, logProg, demo, jobProg);
            FinalizeResults(result);
        }
        catch (Exception ex) { Log.Add("Erro: " + ex.Message); ScoringStatus = "Erro: " + ex.Message; }
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

    [RelayCommand]
    private async Task ExportCv()
    {
        CommitFormToProfile();
        Busy = true;
        try
        {
            string outDir = Path.Combine(_root, "output");
            string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmm");
            string? path = await Task.Run(() => CvPdf.Export(_profile, outDir, stamp));
            if (path is not null)
            {
                Status = "CV gerado: " + path;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
                catch { /* opening is best-effort */ }
            }
            else Status = "Não consegui gerar o CV.";
        }
        finally { Busy = false; }
    }

    // ---- helpers ----
    /// <summary>Inserts one streamed job into the ranked lists (descending by score).</summary>
    private Task<CompanyBrief?> ResearchCompanyAsync(JobEntity j)
        => CompanyResearch.ResearchAsync(_cfg.Claude, _profile, j.Company, j.Title, j.Location);

    private void AddStreamed(JobEntity j)
    {
        var vm = new JobVm(j, ResearchCompanyAsync);
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
        ResultsTitle = result.Demo ? "Vagas para ti (demo)" : "Vagas para ti";
        _all = _all.Count == 0 && result.Jobs.Count > 0
            ? result.Jobs.Select(j => new JobVm(j, ResearchCompanyAsync)).ToList()
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
        EmptyMessage = _all.Count > 0
            ? "Nenhuma vaga corresponde à pesquisa/filtros."
            : "Sem vagas para mostrar. Usa “Procurar vagas” para fazer um varrimento, ou “Ver vagas” para as guardadas.";
    }

    private void ShowOnly(bool welcome = false, bool profile = false, bool running = false, bool results = false, bool settings = false, bool improve = false)
    {
        IsWelcome = welcome; IsProfile = profile; IsRunning = running; IsResults = results; IsSettings = settings; IsImprove = improve;
        Nav = settings ? "settings" : improve ? "improve" : results ? "results" : profile ? "profile" : "home";
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
        catch { }
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
