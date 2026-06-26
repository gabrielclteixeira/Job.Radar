using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JobRadar.Desktop.ViewModels;

/// <summary>One row of the jobs filter bar: a field + contains/not-contains + value. AND-combined.</summary>
public partial class JobFilter : ObservableObject
{
    public string[] FieldOptions { get; } = { "Descrição", "Título", "Empresa", "Localização", "Fonte" };
    public string[] ModeOptions { get; } = { "contém", "não contém" };

    [ObservableProperty] private string _field = "Descrição";
    [ObservableProperty] private string _mode = "contém";
    [ObservableProperty] private string _value = "";

    /// <summary>Invoked when any part of the filter changes (the VM re-applies the filter).</summary>
    public Action? Changed;

    partial void OnFieldChanged(string value) => Changed?.Invoke();
    partial void OnModeChanged(string value) => Changed?.Invoke();
    partial void OnValueChanged(string value) => Changed?.Invoke();

    public bool Matches(JobVm v)
    {
        if (string.IsNullOrWhiteSpace(Value)) return true;
        var j = v.Entity;
        string hay = Field switch
        {
            "Título" => j.Title,
            "Empresa" => j.Company,
            "Localização" => j.Location,
            "Fonte" => j.Source,
            _ => j.Description,
        } ?? "";
        bool contains = hay.Contains(Value, StringComparison.OrdinalIgnoreCase);
        return Mode == "não contém" ? !contains : contains;
    }
}
