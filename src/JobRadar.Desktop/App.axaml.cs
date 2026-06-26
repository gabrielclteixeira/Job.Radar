using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using JobRadar.Desktop.ViewModels;
using JobRadar.Desktop.Views;

namespace JobRadar.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Build the VM first: its constructor resolves the saved language into Loc BEFORE the
            // window's XAML (and its {l:T} bindings) is loaded, so the UI renders in the right language.
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // The {l:T} static labels are bound once, so to switch language live we rebuild the window
            // with the SAME view-model (state preserved). Set the new MainWindow before closing the old
            // one so closing it doesn't trip OnMainWindowClose shutdown.
            vm.LanguageChanged += () =>
            {
                var previous = desktop.MainWindow;
                var fresh = new MainWindow { DataContext = vm };
                desktop.MainWindow = fresh;
                fresh.Show();
                previous?.Close();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
