using System;
using System.IO;
using System.Runtime.InteropServices;
using ReClaw.App.Actions;

namespace ReClaw.App.Platform;

public static class PathDefaults
{
    public static ActionContext CreateDefaultContext()
    {
        var configDir = GetConfigDirectory();
        var dataDir = GetDataDirectory();
        var backupDir = Path.Combine(dataDir, "backups");
        var logsDir = Path.Combine(dataDir, "logs");
        var openClawHome = GetOpenClawHome();
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(openClawHome);

        var openClawExecutable = GetOpenClawExecutable();
        var openClawEntry = GetOpenClawEntry();

        return new ActionContext(
            configDir,
            dataDir,
            backupDir,
            logsDir,
            Path.GetTempPath(),
            openClawHome,
            openClawExecutable,
            openClawEntry);
    }

    public static string GetConfigDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReClaw");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReClaw");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return Path.Combine(xdg, "reclaw");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "reclaw");
    }

    public static string GetDataDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReClaw");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReClaw");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return Path.Combine(xdg, "reclaw");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "reclaw");
    }

    public static string GetOpenClawHome()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw");
    }

    public static string? GetOpenClawExecutable()
    {
        var env = Environment.GetEnvironmentVariable("RECLAW_OPENCLAW_PATH")
            ?? Environment.GetEnvironmentVariable("OPENCLAW_EXE");
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    public static string? GetOpenClawEntry()
    {
        var entry = Environment.GetEnvironmentVariable("OPENCLAW_ENTRY");
        if (!string.IsNullOrWhiteSpace(entry)) return entry;

        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO");
        if (string.IsNullOrWhiteSpace(repo)) return null;

        var candidate = Path.Combine(repo, "openclaw.mjs");
        return File.Exists(candidate) ? candidate : null;
    }
}
