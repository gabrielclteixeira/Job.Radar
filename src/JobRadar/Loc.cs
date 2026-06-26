using System.ComponentModel;
using System.Globalization;

namespace JobRadar;

/// <summary>
/// Tiny runtime localizer shared by the core and the desktop UI. Holds the PT/EN string
/// tables and the active language; raises an indexer change so XAML bindings refresh live
/// when the language switches. Core code (pipeline logs, AI prompts) reads it directly.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _lang = "pt"; // resolved two-letter code: "pt" | "en"

    /// <summary>Active language code ("pt" | "en").</summary>
    public string Lang => _lang;
    public bool IsEnglish => _lang == "en";

    /// <summary>Bound by the UI: localized string for a key (falls back to PT, then the key).</summary>
    public string this[string key]
    {
        get
        {
            var table = _lang == "en" ? Strings.En : Strings.Pt;
            if (table.TryGetValue(key, out var v)) return v;
            return Strings.Pt.TryGetValue(key, out var pt) ? pt : key;
        }
    }

    /// <summary>Direct lookup for code (same as the indexer).</summary>
    public string T(string key) => this[key];

    /// <summary>Format helper: T(key) with string.Format args.</summary>
    public string F(string key, params object[] args) => string.Format(this[key], args);

    /// <summary>Set the language from a preference ("Automático"/"Auto", "Português", "English").</summary>
    public void SetPreference(string? pref)
    {
        string code = pref switch
        {
            "English" => "en",
            "Português" => "pt",
            _ => DetectOsLanguage(),
        };
        if (code == _lang) return;
        _lang = code;
        // Empty/null property name == "everything changed" — makes Avalonia re-read every
        // binding sourced from this object, including the [key] indexer bindings (live switch).
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lang)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnglish)));
    }

    /// <summary>"en" if the OS UI language is English, otherwise "pt".</summary>
    public static string DetectOsLanguage()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)
            ? "en" : "pt";
}
