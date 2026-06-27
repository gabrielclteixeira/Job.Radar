namespace JobRadar.Desktop.ViewModels;

/// <summary>One row in the installed-models list (rebuilt on each refresh).</summary>
public sealed record OllamaModelVm(string Name, string Meta, bool IsActive);
