using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JobRadar.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly string _root;
    private readonly string _profilePath;
    private AppConfig _cfg;
    private UserProfile _profile = new();
    private List<JobVm> _all = new();
    private bool _isDemoProfile; // true while the John Doe sample is loaded → never persisted

    public MainViewModel()
    {
        _root = FindRoot();
        _profilePath = Path.Combine(_root, "profile.json");
        _cfg = LoadConfig();
        LoadSavedProfile();
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
        await RunPipeline(useAi: UseAi, demo: false);
    }

    [RelayCommand]
    private Task RunDemo() => RunPipeline(useAi: false, demo: true);

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
            var jobs = _all.Where(v => v.Score >= MinScore).Select(v => v.Entity).ToList();
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
    private void AddStreamed(JobEntity j)
    {
        var vm = new JobVm(j);
        int idx = _all.FindIndex(x => x.Score < vm.Score);
        if (idx < 0) _all.Add(vm); else _all.Insert(idx, vm);

        if (vm.Score >= MinScore)
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
            ? result.Jobs.Select(j => new JobVm(j)).ToList()
            : _all.OrderByDescending(v => v.Score).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Jobs.Clear();
        foreach (var v in _all.Where(v => v.Score >= MinScore)) Jobs.Add(v);
        HasJobs = Jobs.Count > 0;
    }

    private void ShowOnly(bool welcome = false, bool profile = false, bool running = false, bool results = false)
    {
        IsWelcome = welcome; IsProfile = profile; IsRunning = running; IsResults = results;
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
