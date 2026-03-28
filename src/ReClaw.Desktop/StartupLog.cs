using System;
using System.IO;

namespace ReClaw.Desktop;

internal static class StartupLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = ResolveLogPath();
    private static readonly string FallbackLogPath = ResolveFallbackPath();

    public static void Write(string message)
    {
        if (TryAppend(LogPath, message))
        {
            return;
        }

        TryAppend(FallbackLogPath, message);
    }

    public static string PathOnDisk => LogPath;
    public static string FallbackPathOnDisk => FallbackLogPath;

    private static string ResolveLogPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }
        return Path.Combine(root, "ReClaw", "logs", "reclaw-desktop.log");
    }

    private static string ResolveFallbackPath()
        => Path.Combine(AppContext.BaseDirectory, "reclaw-desktop-fallback.log");

    private static bool TryAppend(string path, string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
            lock (Sync)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.AppendAllText(path, line);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
