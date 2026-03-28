using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ReClaw.Core.IO;

namespace ReClaw.Core;

public enum ResetMode
{
    PreserveCliOnly,
    PreserveConfig,
    PreserveBackups,
    FullLocalReset
}

public sealed record ResetContext(
    string OpenClawHome,
    string ConfigDirectory,
    string DataDirectory,
    string BackupDirectory);

public sealed record ResetPlan(
    ResetMode Mode,
    IReadOnlyList<string> DeletePaths,
    IReadOnlyList<string> PreservePaths);

public sealed class ResetService
{
    private readonly IFileFaultInjector faultInjector;

    public ResetService()
        : this(null)
    {
    }

    internal ResetService(IFileFaultInjector? faultInjector)
    {
        this.faultInjector = faultInjector ?? new NullFileFaultInjector();
    }

    public ResetPlan BuildPlan(ResetContext context, ResetMode mode)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        var delete = new List<string>();
        var preserve = new List<string>();

        var openClawHome = NormalizePath(context.OpenClawHome);
        var configDir = NormalizePath(context.ConfigDirectory);
        var dataDir = NormalizePath(context.DataDirectory);
        var backupDir = NormalizePath(context.BackupDirectory);

        switch (mode)
        {
            case ResetMode.PreserveCliOnly:
                delete.Add(openClawHome);
                delete.Add(configDir);
                delete.Add(dataDir);
                break;
            case ResetMode.PreserveConfig:
                preserve.Add(configDir);
                delete.Add(openClawHome);
                delete.Add(dataDir);
                break;
            case ResetMode.PreserveBackups:
                preserve.Add(backupDir);
                delete.Add(openClawHome);
                delete.Add(configDir);
                delete.AddRange(EnumerateChildrenExcept(dataDir, backupDir));
                break;
            case ResetMode.FullLocalReset:
                delete.Add(openClawHome);
                delete.Add(configDir);
                delete.Add(dataDir);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported reset mode.");
        }

        delete = delete
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !IsRootPath(path))
            .ToList();

        preserve = preserve
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        delete.RemoveAll(path => preserve.Any(keep => IsSameOrChild(path, keep)));

        return new ResetPlan(mode, delete, preserve);
    }

    public Task ExecuteAsync(ResetPlan plan)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        foreach (var path in plan.DeletePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            faultInjector.BeforeDeletePath(path);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    private static IEnumerable<string> EnumerateChildrenExcept(string parentPath, string excludePath)
    {
        if (string.IsNullOrWhiteSpace(parentPath)) yield break;

        if (!Directory.Exists(parentPath))
        {
            yield return parentPath;
            yield break;
        }

        var normalizedExclude = NormalizePath(excludePath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(parentPath))
        {
            var normalizedEntry = NormalizePath(entry);
            if (IsSameOrChild(normalizedEntry, normalizedExclude))
            {
                continue;
            }
            yield return normalizedEntry;
        }
    }

    private static bool IsRootPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var full = NormalizePath(path);
        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrChild(string path, string candidateRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(candidateRoot)) return false;
        var full = NormalizePath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = NormalizePath(candidateRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return Path.GetFullPath(path);
    }
}
