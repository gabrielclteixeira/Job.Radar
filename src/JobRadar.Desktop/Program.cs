using System;
using System.Threading.Tasks;
using Avalonia;
using JobRadar;

namespace JobRadar.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Diagnostics first: capture anything that goes wrong from here on (incl. startup crashes).
        Diag.Init();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Diag.Fatal("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Diag.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Diag.Fatal("Fatal crash in app loop", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
