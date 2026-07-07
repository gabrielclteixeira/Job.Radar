using CommunityToolkit.Mvvm.ComponentModel;

namespace JobRadar.Desktop.ViewModels;

/// <summary>Editable row for one work-experience entry (JobFilter pattern). Bullets are edited as
/// one multiline TextBox, one bullet per line; ToModel() strips list prefixes and empties —
/// Trim() also eats the '\r' of CRLF lines.</summary>
public partial class CvExperienceVm : ObservableObject
{
    [ObservableProperty] private string _company = "";
    [ObservableProperty] private string _role = "";
    [ObservableProperty] private string _start = "";
    [ObservableProperty] private string _end = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private string _bulletsText = "";

    public static CvExperienceVm From(CvExperience m) => new()
    {
        Company = m.Company, Role = m.Role, Start = m.Start, End = m.End,
        Location = m.Location, BulletsText = string.Join("\n", m.Bullets),
    };

    public CvExperience ToModel() => new()
    {
        Company = Company.Trim(), Role = Role.Trim(), Start = Start.Trim(), End = End.Trim(),
        Location = Location.Trim(), Bullets = SplitBullets(BulletsText),
    };

    internal static List<string> SplitBullets(string text) => (text ?? "")
        .Split('\n')
        .Select(b => b.Trim().TrimStart('-', '•', '*').Trim())
        .Where(b => b.Length > 0)
        .ToList();
}

/// <summary>Editable row for one education entry.</summary>
public partial class CvEducationVm : ObservableObject
{
    [ObservableProperty] private string _school = "";
    [ObservableProperty] private string _degree = "";
    [ObservableProperty] private string _start = "";
    [ObservableProperty] private string _end = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private string _detailsText = "";

    public static CvEducationVm From(CvEducation m) => new()
    {
        School = m.School, Degree = m.Degree, Start = m.Start, End = m.End,
        Location = m.Location, DetailsText = string.Join("\n", m.Details),
    };

    public CvEducation ToModel() => new()
    {
        School = School.Trim(), Degree = Degree.Trim(), Start = Start.Trim(), End = End.Trim(),
        Location = Location.Trim(), Details = CvExperienceVm.SplitBullets(DetailsText),
    };
}

/// <summary>Editable row for one project entry.</summary>
public partial class CvProjectVm : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _link = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _bulletsText = "";

    public static CvProjectVm From(CvProject m) => new()
    {
        Name = m.Name, Link = m.Link, Description = m.Description,
        BulletsText = string.Join("\n", m.Bullets),
    };

    public CvProject ToModel() => new()
    {
        Name = Name.Trim(), Link = Link.Trim(), Description = Description.Trim(),
        Bullets = CvExperienceVm.SplitBullets(BulletsText),
    };
}
