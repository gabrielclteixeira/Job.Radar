using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

namespace JobRadar.Desktop.ViewModels;

/// <summary>Presentation wrapper around a scored <see cref="JobEntity"/>.</summary>
public class JobVm
{
    private readonly JobEntity _j;

    public JobVm(JobEntity j)
    {
        _j = j;
        OpenCommand = new RelayCommand(Open);
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

    public string Verdict => _j.AiVerdict ?? "";
    public bool HasVerdict => !string.IsNullOrWhiteSpace(_j.AiVerdict);

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
