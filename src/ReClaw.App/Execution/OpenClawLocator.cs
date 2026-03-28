using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using ReClaw.App.Actions;

namespace ReClaw.App.Execution;

public sealed record OpenClawCommand(string FileName, string[] BaseArgs, string? WorkingDirectory);

public sealed record OpenClawCandidate(OpenClawCommand Command, string Source);

public static class OpenClawLocator
{
    private sealed record WslProbe(bool IsDefaultWsl2, bool OpenClawAvailable);
    private static readonly Lazy<WslProbe> WslInfo = new(ProbeWsl, isThreadSafe: true);

    public static bool IsWsl2Default() => (!IsWslDisabled() || IsWslForced()) && WslInfo.Value.IsDefaultWsl2;

    public static bool IsWslForced()
    {
        var raw = Environment.GetEnvironmentVariable("RECLAW_FORCE_WSL");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
    }

    public static OpenClawCommand? Resolve(ActionContext? context = null)
    {
        var wsl = ResolveWsl();
        var configured = ResolveConfigured(context);
        if (IsWslForced())
        {
            if (wsl != null) return wsl;
            return null;
        }
        else
        {
            if (configured != null) return configured;
            if (wsl != null) return wsl;
        }

        var fromPath = ResolveFromPath();
        if (fromPath != null)
        {
            return new OpenClawCommand(fromPath, Array.Empty<string>(), WorkingDirectory: null);
        }

        var fallback = ResolveLocalRepoFallback();
        if (fallback != null) return fallback;

        var serviceEntrypoint = ResolveServiceEntrypoint();
        if (serviceEntrypoint != null) return serviceEntrypoint;

        return null;
    }

    public static OpenClawCandidate? ResolveWithSource(ActionContext? context = null)
    {
        var wsl = ResolveWsl();
        var configured = ResolveConfigured(context);
        if (IsWslForced())
        {
            if (wsl != null)
            {
                return new OpenClawCandidate(wsl, "wsl");
            }
            return null;
        }
        else
        {
            if (configured != null)
            {
                return new OpenClawCandidate(configured, "configured");
            }

            if (wsl != null)
            {
                return new OpenClawCandidate(wsl, "wsl");
            }
        }

        var fromPath = ResolveFromPath();
        if (fromPath != null)
        {
            return new OpenClawCandidate(new OpenClawCommand(fromPath, Array.Empty<string>(), WorkingDirectory: null), "path");
        }

        var fallback = ResolveLocalRepoFallback();
        if (fallback != null)
        {
            return new OpenClawCandidate(fallback, "repo-fallback");
        }

        var serviceEntrypoint = ResolveServiceEntrypoint();
        if (serviceEntrypoint != null)
        {
            return new OpenClawCandidate(serviceEntrypoint, "service-entrypoint");
        }

        return null;
    }

    public static IReadOnlyList<OpenClawCandidate> EnumerateCandidates(ActionContext? context = null)
    {
        var candidates = new List<OpenClawCandidate>();

        var executable = context?.OpenClawExecutable
            ?? Environment.GetEnvironmentVariable("RECLAW_OPENCLAW_PATH")
            ?? Environment.GetEnvironmentVariable("OPENCLAW_EXE");
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            candidates.Add(new OpenClawCandidate(new OpenClawCommand(executable, Array.Empty<string>(), WorkingDirectory: null), "configured-executable"));
        }

        var entry = context?.OpenClawEntry ?? Environment.GetEnvironmentVariable("OPENCLAW_ENTRY");
        if (!string.IsNullOrWhiteSpace(entry) && File.Exists(entry))
        {
            var repoRoot = Path.GetDirectoryName(entry);
            candidates.Add(new OpenClawCandidate(new OpenClawCommand(ResolveNodeExecutable(), new[] { entry }, repoRoot), "configured-entry"));
        }

        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO");
        if (!string.IsNullOrWhiteSpace(repo))
        {
            var entryPath = ResolveEntryFromRepo(repo);
            if (entryPath != null)
            {
                candidates.Add(new OpenClawCandidate(new OpenClawCommand(ResolveNodeExecutable(), new[] { entryPath }, repo), "configured-repo"));
            }
        }

        var fromPath = ResolveFromPath();
        if (!string.IsNullOrWhiteSpace(fromPath)
            && (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || File.Exists(fromPath)))
        {
            candidates.Add(new OpenClawCandidate(new OpenClawCommand(fromPath, Array.Empty<string>(), WorkingDirectory: null), "path"));
        }

        var wsl = ResolveWsl();
        if (wsl != null)
        {
            candidates.Add(new OpenClawCandidate(wsl, "wsl"));
        }

        var repoFallback = ResolveLocalRepoFallback();
        if (repoFallback != null)
        {
            candidates.Add(new OpenClawCandidate(repoFallback, "repo-fallback"));
        }

        var serviceEntrypoint = ResolveServiceEntrypoint();
        if (serviceEntrypoint != null)
        {
            candidates.Add(new OpenClawCandidate(serviceEntrypoint, "service-entrypoint"));
        }

        return candidates
            .GroupBy(candidate =>
                $"{candidate.Command.FileName}::{string.Join('|', candidate.Command.BaseArgs)}::{candidate.Command.WorkingDirectory}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    public static string? ResolveRepoRoot(ActionContext? context = null)
    {
        var entry = context?.OpenClawEntry ?? Environment.GetEnvironmentVariable("OPENCLAW_ENTRY");
        if (!string.IsNullOrWhiteSpace(entry))
        {
            var parent = Path.GetDirectoryName(entry);
            if (!string.IsNullOrWhiteSpace(parent)) return parent;
        }

        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO");
        if (!string.IsNullOrWhiteSpace(repo) && Directory.Exists(repo)) return repo;

        return FindRepoRoot(Environment.CurrentDirectory)
            ?? FindRepoRoot(AppContext.BaseDirectory);
    }

    public static OpenClawInventory BuildInventory(
        ActionContext context,
        OpenClawGatewayInfo? gatewayInfo = null,
        IReadOnlyList<OpenClawServiceInfo>? services = null,
        IReadOnlyList<OpenClawArtifactInfo>? artifacts = null,
        IReadOnlyList<OpenClawWarning>? warnings = null,
        string? runtimeVersion = null,
        string? configVersion = null,
        OpenClawCommand? resolved = null,
        IReadOnlyList<OpenClawCandidate>? candidates = null)
    {
        var resolvedCommand = resolved ?? Resolve(context);
        var resolvedKey = resolvedCommand != null ? BuildCommandKey(resolvedCommand) : null;
        var configInfo = BuildConfigInfo(context, configVersion);

        var candidateList = candidates ?? EnumerateCandidates(context);
        var runtimeInfos = candidateList
            .Select(candidate => BuildRuntimeInfo(candidate, resolvedKey, runtimeVersion, configInfo?.ConfigPath))
            .OrderByDescending(info => info.Score)
            .ToList();

        var active = runtimeInfos.FirstOrDefault(info => info.IsSelected);
        var gateway = gatewayInfo ?? new OpenClawGatewayInfo(false, false, false, null, null);
        var serviceList = services ?? Array.Empty<OpenClawServiceInfo>();
        var artifactList = artifacts ?? Array.Empty<OpenClawArtifactInfo>();
        var warningList = warnings ?? Array.Empty<OpenClawWarning>();

        return new OpenClawInventory(
            active,
            runtimeInfos,
            configInfo,
            gateway,
            serviceList,
            artifactList,
            warningList);
    }

    private static OpenClawCommand? ResolveConfigured(ActionContext? context)
    {
        var executable = context?.OpenClawExecutable
            ?? Environment.GetEnvironmentVariable("RECLAW_OPENCLAW_PATH")
            ?? Environment.GetEnvironmentVariable("OPENCLAW_EXE");
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            return new OpenClawCommand(executable, Array.Empty<string>(), WorkingDirectory: null);
        }

        var entry = context?.OpenClawEntry ?? Environment.GetEnvironmentVariable("OPENCLAW_ENTRY");
        if (!string.IsNullOrWhiteSpace(entry) && File.Exists(entry))
        {
            var repoRoot = Path.GetDirectoryName(entry);
            return new OpenClawCommand(ResolveNodeExecutable(), new[] { entry }, repoRoot);
        }

        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO");
        if (!string.IsNullOrWhiteSpace(repo))
        {
            var entryPath = ResolveEntryFromRepo(repo);
            if (entryPath != null)
            {
                return new OpenClawCommand(ResolveNodeExecutable(), new[] { entryPath }, repo);
            }
        }

        return null;
    }

    private static OpenClawCommand? ResolveWsl()
    {
        if (IsWslDisabled() && !IsWslForced())
        {
            return null;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        var probe = WslInfo.Value;
        if (!probe.IsDefaultWsl2 || !probe.OpenClawAvailable)
        {
            return null;
        }

        return new OpenClawCommand("wsl.exe", new[] { "--", "openclaw" }, WorkingDirectory: null);
    }

    private static WslProbe ProbeWsl()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || (IsWslDisabled() && !IsWslForced()))
        {
            return new WslProbe(false, false);
        }

        var list = TryRunProcess("wsl.exe", new[] { "-l", "-v" }, 3000);
        var defaultIsWsl2 = list != null && list.ExitCode == 0 && TryParseDefaultWslVersion(list.StdOut) == 2;
        if (!defaultIsWsl2)
        {
            return new WslProbe(false, false);
        }

        var openclaw = TryRunProcess("wsl.exe", new[] { "--", "openclaw", "--version" }, 4000);
        var openclawAvailable = openclaw != null && openclaw.ExitCode == 0;
        return new WslProbe(defaultIsWsl2, openclawAvailable);
    }

    private static bool IsWslDisabled()
    {
        var raw = Environment.GetEnvironmentVariable("RECLAW_DISABLE_WSL");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProcessProbeResult(int ExitCode, string StdOut, string StdErr);

    private static ProcessProbeResult? TryRunProcess(string fileName, IReadOnlyList<string> args, int timeoutMs)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }
            if (!process.Start())
            {
                return null;
            }
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            return new ProcessProbeResult(process.ExitCode, stdout, stderr);
        }
        catch
        {
            return null;
        }
    }

    private static int? TryParseDefaultWslVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var entries = lines
            .Where(line => !line.Contains("NAME", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var lineToUse = entries.FirstOrDefault(line => line.StartsWith("*"))
            ?? entries.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(lineToUse))
        {
            return null;
        }

        var tokens = lineToUse.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var versionToken = tokens.LastOrDefault();
        return int.TryParse(versionToken, out var version) ? version : null;
    }

    private static string ResolveNodeExecutable()
    {
        var envNode = Environment.GetEnvironmentVariable("RECLAW_NODE_PATH")
            ?? Environment.GetEnvironmentVariable("NODE");
        if (!string.IsNullOrWhiteSpace(envNode) && File.Exists(envNode)) return envNode;
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";
    }

    private static string? ResolveFromPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            var roaming = appData != null ? Path.Combine(appData, "npm", "openclaw.cmd") : null;
            var pf = Environment.GetEnvironmentVariable("ProgramFiles");
            var pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            var candidates = new[]
            {
                roaming,
                pf != null ? Path.Combine(pf, "nodejs", "openclaw.cmd") : null,
                pf86 != null ? Path.Combine(pf86, "nodejs", "openclaw.cmd") : null,
                "openclaw.cmd"
            };

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        var unixCandidates = new[]
        {
            "/usr/local/bin/openclaw",
            "/opt/homebrew/bin/openclaw",
            "/usr/bin/openclaw",
            "openclaw"
        };

        foreach (var candidate in unixCandidates)
        {
            try
            {
                if (candidate == "openclaw" || File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore
            }
        }

        return "openclaw";
    }

    private static OpenClawCommand? ResolveLocalRepoFallback()
    {
        var repoRoot = ResolveRepoRoot();
        if (string.IsNullOrWhiteSpace(repoRoot)) return null;

        var entryPath = ResolveEntryFromRepo(repoRoot);
        if (entryPath == null) return null;

        return new OpenClawCommand(ResolveNodeExecutable(), new[] { entryPath }, repoRoot);
    }

    private static OpenClawCommand? ResolveServiceEntrypoint()
    {
        var entry = Environment.GetEnvironmentVariable("RECLAW_OPENCLAW_SERVICE_ENTRYPOINT")
            ?? Environment.GetEnvironmentVariable("OPENCLAW_SERVICE_ENTRYPOINT");
        if (string.IsNullOrWhiteSpace(entry)) return null;
        if (!File.Exists(entry)) return null;

        var extension = Path.GetExtension(entry);
        if (string.Equals(extension, ".js", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".mjs", StringComparison.OrdinalIgnoreCase))
        {
            var repoRoot = Path.GetDirectoryName(entry);
            return new OpenClawCommand(ResolveNodeExecutable(), new[] { entry }, repoRoot);
        }

        return new OpenClawCommand(entry, Array.Empty<string>(), Path.GetDirectoryName(entry));
    }

    private static string? ResolveEntryFromRepo(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot)) return null;

        var direct = Path.Combine(repoRoot, "openclaw.mjs");
        if (File.Exists(direct)) return direct;

        var nested = Path.Combine(repoRoot, "openclaw", "openclaw.mjs");
        if (File.Exists(nested)) return nested;

        return null;
    }

    private static string? FindRepoRoot(string? start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        var current = Path.GetFullPath(start);
        for (var i = 0; i < 6; i++)
        {
            if (File.Exists(Path.Combine(current, "openclaw.mjs")))
            {
                return current;
            }

            var nested = Path.Combine(current, "openclaw");
            if (File.Exists(Path.Combine(nested, "openclaw.mjs")))
            {
                return nested;
            }

            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        return null;
    }

    public static IReadOnlyList<OpenClawArtifactInfo> ScanArtifacts(
        ActionContext context,
        OpenClawInventory? inventory = null)
    {
        var artifacts = new List<OpenClawArtifactInfo>();

        var tempRoot = Path.Combine(Path.GetTempPath(), "openclaw");
        if (Directory.Exists(tempRoot))
        {
            foreach (var file in Directory.EnumerateFiles(tempRoot, "gateway*.lock"))
            {
                artifacts.Add(new OpenClawArtifactInfo(
                    "lock",
                    file,
                    true,
                    "Stale gateway lock file"));
            }
        }

        if (inventory?.CandidateRuntimes is { Count: > 1 } runtimes)
        {
            foreach (var runtime in runtimes.Where(r => !r.IsSelected))
            {
                artifacts.Add(new OpenClawArtifactInfo(
                    "old-runtime",
                    runtime.ExecutablePath,
                    false,
                    $"Alternate runtime candidate ({runtime.Kind})."));
            }
        }

        var logRoot = context.LogsDirectory;
        if (!string.IsNullOrWhiteSpace(logRoot) && Directory.Exists(logRoot))
        {
            artifacts.Add(new OpenClawArtifactInfo(
                "log",
                logRoot,
                false,
                "OpenClaw log directory. Use Nuke Local Data (confirmation required) to remove."));
        }

        if (inventory?.Services is { Count: > 0 } services)
        {
            foreach (var service in services.Where(s => s.IsMismatched || s.IsLegacy))
            {
                var summary = service.IsMismatched
                    ? "Mismatched gateway service entrypoint."
                    : "Legacy gateway service entry.";
                artifacts.Add(new OpenClawArtifactInfo(
                    "orphan-service",
                    service.Entrypoint ?? service.Name,
                    false,
                    summary));
            }
        }

        return artifacts;
    }

    private static OpenClawRuntimeInfo BuildRuntimeInfo(
        OpenClawCandidate candidate,
        string? resolvedKey,
        string? runtimeVersion,
        string? configPath)
    {
        var key = BuildCommandKey(candidate.Command);
        var isSelected = resolvedKey != null && string.Equals(resolvedKey, key, StringComparison.OrdinalIgnoreCase);
        var kind = MapKind(candidate.Source);
        var score = ScoreForKind(kind);
        var version = isSelected ? runtimeVersion : null;

        return new OpenClawRuntimeInfo(
            kind,
            candidate.Command.FileName,
            candidate.Command.WorkingDirectory,
            version,
            isSelected,
            score);
    }

    private static string MapKind(string source)
    {
        if (source.StartsWith("configured", StringComparison.OrdinalIgnoreCase)) return "configured";
        if (source.Equals("wsl", StringComparison.OrdinalIgnoreCase)) return "wsl2";
        if (source.Equals("path", StringComparison.OrdinalIgnoreCase)) return "path";
        if (source.Equals("repo-fallback", StringComparison.OrdinalIgnoreCase)) return "repo-fallback";
        if (source.Equals("service-entrypoint", StringComparison.OrdinalIgnoreCase)) return "detected-service";
        return source;
    }

    private static int ScoreForKind(string kind)
    {
        return kind switch
        {
            "configured" => 100,
            "wsl2" => 95,
            "path" => 90,
            "repo-fallback" => 80,
            "detected-service" => 70,
            _ => 50
        };
    }

    private static string BuildCommandKey(OpenClawCommand command)
    {
        return $"{command.FileName}::{string.Join('|', command.BaseArgs)}::{command.WorkingDirectory}";
    }

    private static OpenClawConfigInfo BuildConfigInfo(ActionContext context, string? configVersion)
    {
        var configPath = ResolveConfigPath(context);
        var exists = !string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath);
        string? stateDir = null;
        string? workspaceDir = null;
        string? logPath = null;
        var version = configVersion;

        if (exists && configPath != null)
        {
            try
            {
                using var stream = File.OpenRead(configPath);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                version ??= TryFindVersion(root);
                stateDir = TryFindString(root, "stateDir", "stateDirectory", "state_path");
                workspaceDir = TryFindString(root, "workspaceDir", "workspaceDirectory", "workspace_path");
                logPath = TryFindString(root, "logPath", "logsPath", "logDir", "logsDir");
                if (logPath == null && root.TryGetProperty("logs", out var logsObj) && logsObj.ValueKind == JsonValueKind.Object)
                {
                    logPath = TryFindString(logsObj, "path", "dir", "root");
                }
            }
            catch
            {
                // ignore config parsing failures
            }
        }

        return new OpenClawConfigInfo(
            configPath ?? Path.Combine(context.ConfigDirectory, "openclaw.json"),
            stateDir,
            workspaceDir,
            logPath,
            version,
            exists);
    }

    private static string? ResolveConfigPath(ActionContext context)
    {
        var explicitPath = Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;

        var configDirPath = Path.Combine(context.ConfigDirectory, "openclaw.json");
        if (File.Exists(configDirPath)) return configDirPath;

        var homePath = Path.Combine(context.OpenClawHome, "openclaw.json");
        if (File.Exists(homePath)) return homePath;

        var nested = Path.Combine(context.OpenClawHome, "config", "openclaw.json");
        return nested;
    }

    private static string? TryFindString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (property.NameEquals(name))
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }
        return null;
    }

    private static string? TryFindVersion(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("version")
                || property.NameEquals("configVersion")
                || property.NameEquals("schemaVersion")
                || property.NameEquals("openclawVersion"))
            {
                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return null;
    }
}
