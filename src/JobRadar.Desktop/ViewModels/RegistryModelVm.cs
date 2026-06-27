using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using JobRadar;

namespace JobRadar.Desktop.ViewModels;

/// <summary>An installable quant/size choice. <see cref="Payload"/> encodes everything the install command
/// needs (tab-separated: "ollama\t&lt;name:size&gt;" or "lmstudio\t&lt;repo&gt;\t&lt;file.gguf&gt;").</summary>
public sealed record QuantOption(string Label, string Payload);

/// <summary>Presentation wrapper for one model in the live browser (Ollama or LM Studio/HF).</summary>
public partial class RegistryModelVm : ObservableObject
{
    public RegistryModelVm(RegistryModel m)
    {
        Source = m.Source;
        Name = m.Name;
        Repo = m.Repo;
        Description = m.Description;
        OllamaSizes = m.Sizes;
        CapBadges = Badges(m.Capabilities);
        Meta = string.Join("  ·  ", new[]
        {
            string.IsNullOrWhiteSpace(m.Pulls) ? "" : m.Pulls + " ↓",
            m.Updated,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public string Source { get; }
    public string Name { get; }
    public string Repo { get; }
    public string Description { get; }
    public string CapBadges { get; }
    public string Meta { get; }
    public IReadOnlyList<string> OllamaSizes { get; }

    public bool IsOllama => Source == "ollama";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasCaps => !string.IsNullOrWhiteSpace(CapBadges);
    public ObservableCollection<QuantOption> Quants { get; } = new();
    public bool HasQuants => Quants.Count > 0;

    [ObservableProperty] private bool _quantsLoaded;
    [ObservableProperty] private bool _expanded;

    // Per-row install feedback (so the user sees progress right where they clicked).
    [ObservableProperty] private bool _installing;
    [ObservableProperty] private double _installProgress;
    [ObservableProperty] private string _installStatus = "";

    private static string Badges(List<string> caps)
    {
        var sb = new StringBuilder();
        foreach (var c in caps)
            sb.Append(c switch
            {
                "vision" => "👁 ",
                "tools" => "🛠 ",
                "thinking" or "reasoning" => "🧠 ",
                "embedding" => "🔢 ",
                "audio" => "🔊 ",
                _ => "",
            });
        return sb.ToString().Trim();
    }
}
