using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JobRadar.Desktop.ViewModels;

namespace JobRadar.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

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
