using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JobRadar.Desktop.ViewModels;

/// <summary>One bubble of the Coach transcript. Assistant bubbles stream plain text and switch to
/// markdown once complete (re-parsing markdown on every delta would stutter); user bubbles show the
/// attached screenshots as width-capped thumbnails decoded once.</summary>
public partial class CoachMessageVm : ObservableObject
{
    public bool IsUser { get; }
    public IReadOnlyList<string>? ImagePaths { get; }
    public IReadOnlyList<Bitmap> Thumbs { get; }
    public bool HasThumbs => Thumbs.Count > 0;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isStreaming;

    public bool ShowFinalMarkdown => !IsUser && !IsStreaming;
    public bool ShowSpinner => IsStreaming && Text.Length == 0;

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(ShowSpinner));
    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFinalMarkdown));
        OnPropertyChanged(nameof(ShowSpinner));
    }

    public CoachMessageVm(bool isUser, string text, IReadOnlyList<string>? imagePaths = null)
    {
        IsUser = isUser;
        _text = text;
        ImagePaths = imagePaths;
        var thumbs = new List<Bitmap>();
        foreach (var p in imagePaths ?? Array.Empty<string>())
        {
            try
            {
                using var fs = File.OpenRead(p);
                thumbs.Add(Bitmap.DecodeToWidth(fs, 220));
            }
            catch { /* undecodable image — skip the thumbnail, the path still reaches the model */ }
        }
        Thumbs = thumbs;
    }
}
