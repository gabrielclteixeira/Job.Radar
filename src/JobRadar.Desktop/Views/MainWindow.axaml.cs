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
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ConfirmCostAsync = ConfirmApifyCostAsync;
                vm.ConfirmRemoveAsync = ConfirmRemoveModelAsync;
                vm.ConfirmLeaveSettingsAsync = ConfirmLeaveSettingsAsync;
                vm.ConfirmDeleteJobsAsync = ConfirmDeleteJobsAsync;
                vm.ScrollToMaxTokensRequested += ScrollToMaxTokens;
                vm.CopyToClipboardAsync = async text =>
                {
                    if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(text);
                };
            }
        };
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
}
