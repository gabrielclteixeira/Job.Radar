using System.Threading.Tasks;
using Avalonia.Controls;
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

    /// <summary>Cost confirmation shown before a paid (Apify) search.</summary>
    private async Task<bool> ConfirmApifyCostAsync()
    {
        var dlg = new ContentDialog
        {
            Title = "Pesquisar com Apify (LinkedIn)?",
            Content = "O Apify é pago — esta pesquisa vai consumir créditos da tua conta Apify. Continuar?",
            PrimaryButtonText = "Continuar",
            CloseButtonText = "Cancelar",
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
