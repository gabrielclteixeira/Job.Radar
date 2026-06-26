using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
            if (DataContext is MainViewModel vm) vm.ConfirmCostAsync = ConfirmApifyCostAsync;
        };
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
