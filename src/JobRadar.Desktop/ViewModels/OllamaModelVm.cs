namespace JobRadar.Desktop.ViewModels;

/// <summary>One row in the installed-models list (rebuilt on each refresh). <see cref="Source"/> is "ollama"
/// or "lmstudio"; <see cref="Path"/> is the on-disk GGUF path for LM Studio models (used to remove them).</summary>
public sealed record OllamaModelVm(string Name, string Meta, bool IsActive, string Source = "ollama", string Path = "")
{
    public bool IsLmStudio => Source == "lmstudio";
    public string SourceLabel => IsLmStudio ? "LM Studio" : "Ollama";
}
