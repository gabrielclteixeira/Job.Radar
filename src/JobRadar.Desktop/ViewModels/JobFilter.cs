using System;
using CommunityToolkit.Mvvm.ComponentModel;
using JobRadar;

namespace JobRadar.Desktop.ViewModels;

/// <summary>One row of the jobs filter bar: a field + contains/not-contains + value. AND-combined.</summary>
public partial class JobFilter : ObservableObject
{
    public string[] FieldOptions => new[]
    {
        Loc.Instance.T("opt.field.desc"), Loc.Instance.T("opt.field.title"),
        Loc.Instance.T("opt.field.company"), Loc.Instance.T("opt.field.location"),
        Loc.Instance.T("opt.field.source"),
    };
    public string[] ModeOptions => new[] { Loc.Instance.T("opt.mode.contains"), Loc.Instance.T("opt.mode.notcontains") };

    [ObservableProperty] private int _fieldIndex; // 0 desc, 1 title, 2 company, 3 location, 4 source
    [ObservableProperty] private int _modeIndex;   // 0 contains, 1 doesn't contain
    [ObservableProperty] private string _value = "";

    /// <summary>Invoked when any part of the filter changes (the VM re-applies the filter).</summary>
    public Action? Changed;

    partial void OnFieldIndexChanged(int value) => Changed?.Invoke();
    partial void OnModeIndexChanged(int value) => Changed?.Invoke();
    partial void OnValueChanged(string value) => Changed?.Invoke();

    public bool Matches(JobVm v)
    {
        if (string.IsNullOrWhiteSpace(Value)) return true;
        var j = v.Entity;
        string hay = (FieldIndex switch
        {
            1 => j.Title,
            2 => j.Company,
            3 => j.Location,
            4 => j.Source,
            _ => j.Description,
        }) ?? "";
        bool contains = hay.Contains(Value, StringComparison.OrdinalIgnoreCase);
        return ModeIndex == 1 ? !contains : contains;
    }
}
