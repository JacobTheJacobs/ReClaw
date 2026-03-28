using System.Diagnostics;
using System.IO;
using Avalonia;

namespace ReClaw.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var logPath = StartupLog.PathOnDisk;
        var logDirectory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }
        var tracePath = Path.Combine(logDirectory ?? AppContext.BaseDirectory, "reclaw-desktop-trace.log");
        try
        {
            Trace.Listeners.Add(new TextWriterTraceListener(tracePath));
            Trace.AutoFlush = true;
            Trace.WriteLine($"ReClaw.Desktop trace starting at {DateTimeOffset.Now:O}");
        }
        catch
        {
            // If logging can't be initialized, still try to launch the app.
        }

        StartupLog.Write($"ReClaw.Desktop starting at {DateTimeOffset.Now:O}");
        StartupLog.Write("Startup: Program.Main");

        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                var ex = eventArgs.ExceptionObject as Exception;
                StartupLog.Write($"Unhandled: {ex}");
            };

            StartupLog.Write("Startup: before StartWithClassicDesktopLifetime");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            StartupLog.Write("Startup: StartWithClassicDesktopLifetime returned");
        }
        catch (Exception ex)
        {
            try
            {
                StartupLog.Write($"Fatal: {ex}");
            }
            catch
            {
                // ignore
            }
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
