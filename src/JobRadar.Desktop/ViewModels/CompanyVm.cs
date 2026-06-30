using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JobRadar;

namespace JobRadar.Desktop.ViewModels;

/// <summary>
/// One employer in the Company Researcher view. Wraps a (cached or freshly researched)
/// <see cref="CompanyReport"/> and exposes compact chips + an on-demand research command.
/// Mirrors <see cref="JobVm"/>'s research toggle (click again to cancel).
/// </summary>
public partial class CompanyVm : ObservableObject
{
    private readonly Func<string, IProgress<string>, CancellationToken, Task<(CompanyReport? report, string? error)>>? _research;
    private CancellationTokenSource? _cts;

    public string Name { get; }
    public int JobCount { get; }

    /// <summary>Raised after a successful research so the owner can persist the cache + re-sort.</summary>
    public event Action<CompanyVm>? Researched;

    public CompanyVm(string name, int jobCount, CompanyReport? cached,
        Func<string, IProgress<string>, CancellationToken, Task<(CompanyReport? report, string? error)>>? research)
    {
        Name = name;
        JobCount = jobCount;
        _research = research;
        _report = cached;
    }

    [ObservableProperty] private CompanyReport? _report;
    [ObservableProperty] private bool _isResearching;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _expanded;

    public bool HasReport => Report is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public string JobCountLabel => Loc.Instance.F("researcher.jobsHere", JobCount);

    // ---- compact chips (shown collapsed) ----
    public bool HasRatingChip => Report?.HasRating == true;
    public string RatingChip => Report is { Rating: > 0 } r ? $"★ {r.Rating:0.0}" : "";
    public bool HasLayoffChip => Report?.HasLayoffs == true;
    public string LayoffChip => Report?.HasLayoffs == true
        ? "⚠ " + Loc.Instance.T("researcher.chip.layoffs")
              + (string.IsNullOrWhiteSpace(Report.Layoffs[0].Period) ? "" : " " + Report.Layoffs[0].Period)
        : "";
    public bool HasPayChip => Report?.HasPayBand == true;
    public string PayChip => Report?.HasPayBand == true ? "~" + Report!.PayBand : "";
    public bool HasConfidenceChip => Report?.HasConfidence == true;
    public string ConfidenceChip => Report?.HasConfidence == true
        ? Loc.Instance.T("researcher.conf." + Report!.Confidence) : "";
    public bool HasAsOf => Report?.AsOfDate is not null;
    public string AsOf => Report?.AsOfDate is DateTime d
        ? Loc.Instance.F("researcher.asOf", d.ToLocalTime().ToString("yyyy-MM-dd")) : "";

    partial void OnReportChanged(CompanyReport? value)
    {
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(HasRatingChip)); OnPropertyChanged(nameof(RatingChip));
        OnPropertyChanged(nameof(HasLayoffChip)); OnPropertyChanged(nameof(LayoffChip));
        OnPropertyChanged(nameof(HasPayChip)); OnPropertyChanged(nameof(PayChip));
        OnPropertyChanged(nameof(HasConfidenceChip)); OnPropertyChanged(nameof(ConfidenceChip));
        OnPropertyChanged(nameof(HasAsOf)); OnPropertyChanged(nameof(AsOf));
    }
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Researches the employer; clicking again while running cancels.</summary>
    [RelayCommand]
    private async Task Research()
    {
        if (_research is null) return;
        if (IsResearching) { _cts?.Cancel(); return; }
        IsResearching = true; Error = ""; Expanded = true;
        Status = Loc.Instance.T("research.starting");
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(m => Status = m);
        try
        {
            var (r, err) = await _research(Name, progress, _cts.Token);
            if (r is null) Error = string.IsNullOrWhiteSpace(err) ? Loc.Instance.T("research.failed") : err;
            else { Report = r; Researched?.Invoke(this); }
        }
        catch (OperationCanceledException) { /* cancelled by the user — no error */ }
        catch { Error = Loc.Instance.T("research.failed"); }
        finally { IsResearching = false; }
    }

    [RelayCommand] private void Toggle() => Expanded = !Expanded;

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
