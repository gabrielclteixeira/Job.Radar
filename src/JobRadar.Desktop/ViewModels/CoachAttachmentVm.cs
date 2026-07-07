using Avalonia.Media.Imaging;

namespace JobRadar.Desktop.ViewModels;

/// <summary>A pending image attachment shown as a removable chip under the Coach input.
/// Throws if the file can't be decoded — the caller treats that as "not an image".</summary>
public sealed class CoachAttachmentVm : IDisposable
{
    public string Path { get; }
    public Bitmap Thumb { get; }

    public CoachAttachmentVm(string path)
    {
        Path = path;
        using var fs = File.OpenRead(path);
        Thumb = Bitmap.DecodeToWidth(fs, 96);
    }

    public void Dispose() => Thumb.Dispose();
}
