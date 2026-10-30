using System.Diagnostics;
using Avalonia;

namespace NtfsStreamsViewer;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Last-resort diagnostics: anything that escapes the UI loop must still reach
        // the rotating log file (docs/GUI.md — Error Handling).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Services.AppLogger.Error("fatal",
                $"Unhandled exception (terminating={e.IsTerminating}).",
                ex ?? new Exception("unknown"));
            Debug.WriteLine(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Services.AppLogger.Error("fatal", "Unobserved task exception.", e.Exception);
            e.SetObserved(); // do not crash the process on a swallowed background fault
        };

        try
        {
            return BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Services.AppLogger.Error("fatal", "Fatal error during startup/shutdown.", ex);
            return 1;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
