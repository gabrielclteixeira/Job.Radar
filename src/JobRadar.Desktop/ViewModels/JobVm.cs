using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JobRadar;

namespace JobRadar.Desktop.ViewModels;

/// <summary>Presentation wrapper around a scored <see cref="JobEntity"/>.</summary>
public partial class JobVm : ObservableObject
{
    private readonly JobEntity _j;
    private readonly Func<JobEntity, IProgress<string>, CancellationToken, Task<(CompanyBrief? brief, string? error)>>? _research;
    private CancellationTokenSource? _researchCts;

    public JobVm(JobEntity j, Func<JobEntity, IProgress<string>, CancellationToken, Task<(CompanyBrief? brief, string? error)>>? research = null)
    {
        _j = j;
        _research = research;
        OpenCommand = new RelayCommand(Open);
    }

    [ObservableProperty] private CompanyBrief? _brief;
    [ObservableProperty] private bool _isResearching;
    [ObservableProperty] private string _researchStatus = "";
    [ObservableProperty] private string _researchError = "";
    public bool HasBrief => Brief is not null;
    partial void OnBriefChanged(CompanyBrief? value) => OnPropertyChanged(nameof(HasBrief));

    /// <summary>Researches the employer (reviews + comparable salaries) via the web-search step.</summary>
    [RelayCommand]
    private async Task Research()
    {
        if (_research is null) return;
        if (IsResearching) { _researchCts?.Cancel(); return; }   // clicking again cancels
        IsResearching = true; ResearchError = ""; Brief = null;
        ResearchStatus = Loc.Instance.T("research.starting");
        _researchCts = new CancellationTokenSource();
        var progress = new Progress<string>(m => ResearchStatus = m);
        try
        {
            var (b, err) = await _research(_j, progress, _researchCts.Token);
            if (b is null) ResearchError = string.IsNullOrWhiteSpace(err) ? Loc.Instance.T("research.failed") : err;
            else Brief = b;
        }
        catch (OperationCanceledException) { /* cancelled by the user — no error */ }
        catch { ResearchError = Loc.Instance.T("research.failed"); }
        finally { IsResearching = false; }
    }

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    public JobEntity Entity => _j;
    public int Score => _j.AiScore ?? _j.PreScore;
    public string ScoreLabel => _j.AiScore.HasValue ? "AI" : "KW";
    public string Title => _j.Title;
    public string Company => _j.Company;
    public string Location => _j.Location;
    public string Remote => _j.Remote;
    public string Url => _j.Url;
    public string Source => _j.Source;
    public string Salary => _j.SalaryText;
    public bool HasSalary => !string.IsNullOrEmpty(_j.SalaryText);
    public bool HasRemote => !string.IsNullOrEmpty(_j.Remote);

    // AI verdict when scored by the LLM; otherwise the deterministic keyword base verdict.
    public string Verdict => !string.IsNullOrWhiteSpace(_j.AiVerdict) ? _j.AiVerdict! : (_j.BaseVerdict ?? "");
    public bool HasVerdict => !string.IsNullOrWhiteSpace(Verdict);

    public string Reasons => string.Join("\n", ParseArr(_j.AiReasons).Select(r => "•  " + r));
    public bool HasReasons => ParseArr(_j.AiReasons).Length > 0;

    public string RedFlags => string.Join("\n", ParseArr(_j.AiRedFlags).Select(r => "⚠  " + r));
    public bool HasRedFlags => ParseArr(_j.AiRedFlags).Length > 0;

    public string Explanation => _j.PreScoreExplanation ?? "";
    public bool HasExplanation => !string.IsNullOrWhiteSpace(_j.PreScoreExplanation);

    public ICommand OpenCommand { get; }

    public IBrush ScoreBrush => new SolidColorBrush(Color.Parse(
        Score >= 70 ? "#1A7F4B" : Score >= 50 ? "#4C2DBE" : "#8A8699"));

    private void Open()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        try { Process.Start(new ProcessStartInfo { FileName = Url, UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static string[] ParseArr(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(s) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }
}
