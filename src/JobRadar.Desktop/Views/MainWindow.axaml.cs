using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using JobRadar.Desktop.ViewModels;

namespace JobRadar.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Tunneled: with AcceptsReturn the TextBox consumes Return in its own KeyDown, so a
        // bubble handler never sees it — tunneling fires first and e.Handled stops the newline.
        CoachInputBox.AddHandler(KeyDownEvent, OnCoachInputKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ConfirmCostAsync = ConfirmApifyCostAsync;
                vm.ConfirmResearchAllAsync = ConfirmResearchAllAsync;
                vm.ConfirmRemoveAsync = ConfirmRemoveModelAsync;
                vm.ConfirmLeaveSettingsAsync = ConfirmLeaveSettingsAsync;
                vm.ConfirmDeleteJobsAsync = ConfirmDeleteJobsAsync;
                vm.ScrollToMaxTokensRequested += ScrollToMaxTokens;
                vm.CopyToClipboardAsync = async text =>
                {
                    if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(text);
                };
                // Background priority: layout runs first so ScrollToEnd sees the new extent.
                vm.ScrollCoachToEnd = () => Dispatcher.UIThread.Post(
                    () => CoachScroll?.ScrollToEnd(), DispatcherPriority.Background);
            }
        };
    }

    /// <summary>Coach input: Enter sends (Shift+Enter inserts a newline); Ctrl+V may carry an image.</summary>
    private void OnCoachInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;   // let the TextBox insert \n
            e.Handled = true;                                          // eat Return BEFORE the TextBox does
            if (DataContext is MainViewModel vm && vm.SendCoachCommand.CanExecute(null))
                vm.SendCoachCommand.Execute(null);
            return;
        }
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Clipboard reads are async but e.Handled must be set NOW — so we always take over the
            // paste and re-issue a plain text paste ourselves when no image is found.
            e.Handled = true;
            _ = HandleCoachPasteAsync();
        }
    }

    /// <summary>Ctrl+V on the Coach input: image bytes (PNG → DIB) or copied image files become
    /// attachments; anything else falls back to the TextBox's own text paste.</summary>
    private async Task HandleCoachPasteAsync()
    {
        if (DataContext is not MainViewModel vm) return;
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb is null) return;
        string[] formats;
        try { formats = await cb.GetFormatsAsync(); } catch { formats = System.Array.Empty<string>(); }

        // 1) Raw PNG bytes ("PNG" registered format: browsers, Snipping Tool, most modern apps).
        foreach (var f in new[] { "PNG", "image/png" })
            if (System.Array.IndexOf(formats, f) >= 0)
                try
                {
                    if (await cb.GetDataAsync(f) is byte[] png && png.Length > 8)
                    {
                        string path = System.IO.Path.Combine(vm.CoachImagesDir, $"paste-{System.DateTime.Now:HHmmss-fff}.png");
                        await System.IO.File.WriteAllBytesAsync(path, png);
                        vm.AddCoachImage(path);
                        return;
                    }
                }
                catch { }

        // 2) DIB (PrintScreen / classic apps). CF_DIB has no registered name → "Unknown_Format_8" (V5 = 17).
        foreach (var f in new[] { "DeviceIndependentBitmap", "Unknown_Format_8", "Unknown_Format_17" })
            if (System.Array.IndexOf(formats, f) >= 0)
                try
                {
                    if (await cb.GetDataAsync(f) is byte[] dib && dib.Length > 40 && WrapDibAsBmp(dib) is { } bmp)
                    {
                        // Re-encode to PNG via Avalonia so downstream mime/base64 handling stays PNG-only.
                        using var b = new Avalonia.Media.Imaging.Bitmap(new System.IO.MemoryStream(bmp));
                        string path = System.IO.Path.Combine(vm.CoachImagesDir, $"paste-{System.DateTime.Now:HHmmss-fff}.png");
                        b.Save(path);
                        vm.AddCoachImage(path);
                        return;
                    }
                }
                catch { }

        // 3) Copied image FILES (Explorer → Ctrl+C on a .png).
        if (System.Array.IndexOf(formats, DataFormats.Files) >= 0)
            try
            {
                if (await cb.GetDataAsync(DataFormats.Files) is System.Collections.Generic.IEnumerable<IStorageItem> items)
                {
                    bool any = false;
                    foreach (var it in items)
                        if ((it as IStorageFile)?.TryGetLocalPath() is { } p &&
                            p.ToLowerInvariant() is var lp &&
                            (lp.EndsWith(".png") || lp.EndsWith(".jpg") || lp.EndsWith(".jpeg") || lp.EndsWith(".webp")))
                        { vm.AddCoachImage(p); any = true; }
                    if (any) return;
                }
            }
            catch { }

        // 4) No image on the clipboard — re-issue the plain text paste we intercepted.
        CoachInputBox.Paste();
    }

    /// <summary>Prepends the 14-byte BITMAPFILEHEADER a clipboard DIB lacks, so Avalonia can decode it.</summary>
    private static byte[]? WrapDibAsBmp(byte[] dib)
    {
        try
        {
            int headerSize = System.BitConverter.ToInt32(dib, 0);            // biSize (40 = INFO, 124 = V5)
            int bitCount = System.BitConverter.ToInt16(dib, 14);
            int compression = System.BitConverter.ToInt32(dib, 16);
            int clrUsed = System.BitConverter.ToInt32(dib, 32);
            int palette = clrUsed > 0 ? clrUsed * 4 : (bitCount <= 8 ? (1 << bitCount) * 4 : 0);
            if (compression == 3) palette += 12;                              // BI_BITFIELDS masks
            int offset = 14 + headerSize + palette;
            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            System.BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            System.BitConverter.GetBytes(offset).CopyTo(bmp, 10);
            dib.CopyTo(bmp, 14);
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Attach button on the Coach input — image file picker (multi-select).</summary>
    private async void OnCoachAttach(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = JobRadar.Loc.Instance.T("coach.attach.title"),
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType(JobRadar.Loc.Instance.T("coach.attach.filter"))
                { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } } },
        });
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } p) vm.AddCoachImage(p);
    }

    /// <summary>Brings the token-limit setting into view after navigating to Settings (deferred so the
    /// view has switched and laid out first).</summary>
    private void ScrollToMaxTokens()
        => Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(140);
            MaxTokensField?.BringIntoView();
        }, DispatcherPriority.Background);

    /// <summary>Confirmation before deleting all saved jobs (destructive, clears the cache).</summary>
    private async Task<bool> ConfirmDeleteJobsAsync()
    {
        var dlg = new ContentDialog
        {
            Title = JobRadar.Loc.Instance.T("dlg.deleteJobs.title"),
            Content = JobRadar.Loc.Instance.T("dlg.deleteJobs.body"),
            PrimaryButtonText = JobRadar.Loc.Instance.T("dlg.delete"),
            CloseButtonText = JobRadar.Loc.Instance.T("dlg.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Prompt shown when navigating away from Settings with unsaved changes.
    /// Returns 1 = save, 2 = discard, 0 = cancel (stay).</summary>
    private async Task<int> ConfirmLeaveSettingsAsync()
    {
        var dlg = new ContentDialog
        {
            Title = JobRadar.Loc.Instance.T("dlg.unsaved.title"),
            Content = JobRadar.Loc.Instance.T("dlg.unsaved.body"),
            PrimaryButtonText = JobRadar.Loc.Instance.T("dlg.save"),
            SecondaryButtonText = JobRadar.Loc.Instance.T("dlg.discard"),
            CloseButtonText = JobRadar.Loc.Instance.T("dlg.cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dlg.ShowAsync() switch
        {
            ContentDialogResult.Primary => 1,
            ContentDialogResult.Secondary => 2,
            _ => 0,
        };
    }

    /// <summary>Confirmation before deleting a locally installed model.</summary>
    private async Task<bool> ConfirmRemoveModelAsync(string model)
    {
        var dlg = new ContentDialog
        {
            Title = JobRadar.Loc.Instance.F("models.removeConfirm", model),
            Content = JobRadar.Loc.Instance.T("models.removeBody"),
            PrimaryButtonText = JobRadar.Loc.Instance.T("models.remove"),
            CloseButtonText = JobRadar.Loc.Instance.T("dlg.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>UI zoom shortcuts: Ctrl + / Ctrl - / Ctrl 0 (numpad or main row).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is MainViewModel vm)
        {
            switch (e.Key)
            {
                case Key.Add: case Key.OemPlus: vm.ZoomInCommand.Execute(null); e.Handled = true; return;
                case Key.Subtract: case Key.OemMinus: vm.ZoomOutCommand.Execute(null); e.Handled = true; return;
                case Key.D0: case Key.NumPad0: vm.ZoomResetCommand.Execute(null); e.Handled = true; return;
            }
        }
        base.OnKeyDown(e);
    }

    /// <summary>Ctrl + mouse wheel zooms, like a browser.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is MainViewModel vm)
        {
            if (e.Delta.Y > 0) vm.ZoomInCommand.Execute(null);
            else if (e.Delta.Y < 0) vm.ZoomOutCommand.Execute(null);
            e.Handled = true;
            return;
        }
        base.OnPointerWheelChanged(e);
    }

    /// <summary>Cost confirmation shown before a paid (Apify) search.</summary>
    private async Task<bool> ConfirmApifyCostAsync()
    {
        var dlg = new ContentDialog
        {
            Title = JobRadar.Loc.Instance.T("dlg.cost.title"),
            Content = JobRadar.Loc.Instance.T("dlg.cost.body"),
            PrimaryButtonText = JobRadar.Loc.Instance.T("dlg.continue"),
            CloseButtonText = JobRadar.Loc.Instance.T("dlg.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Confirmation before researching a batch of companies (N model calls).</summary>
    private async Task<bool> ConfirmResearchAllAsync(int count)
    {
        var dlg = new ContentDialog
        {
            Title = JobRadar.Loc.Instance.T("dlg.researchAll.title"),
            Content = JobRadar.Loc.Instance.F("dlg.researchAll.body", count),
            PrimaryButtonText = JobRadar.Loc.Instance.T("dlg.continue"),
            CloseButtonText = JobRadar.Loc.Instance.T("dlg.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnPickCv(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Escolhe o teu CV (PDF)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } },
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) await vm.LoadCvAsync(path);
        }
    }

    /// <summary>CV Studio import: same PDF picker, but the AI extracts the FULL structured document.</summary>
    private async void OnPickCvForStudio(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Escolhe o teu CV (PDF)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } },
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) await vm.ImportCvForStudioAsync(path);
        }
    }
}
