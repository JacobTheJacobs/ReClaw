using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ReClaw.App.Actions;

namespace ReClaw.App.Execution;

internal static class GatewayLaunchHelper
{
    private static readonly Regex GatewayCmdLineRegex = new(@"^(""[^""]+""|\S+)\s+""([^""]+)""(?:\s+(.*))?$", RegexOptions.Compiled);
    private static readonly Regex GatewayCmdSkipRegex = new(@"^(@?echo\b|rem\b|set\s+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GatewayCmdArgRegex = new(@"""[^""]*""|\S+", RegexOptions.Compiled);

    public static bool TryLaunchDetachedGateway(ActionContext context, int port)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        var simulate = Environment.GetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START_SIMULATE");
        if (string.Equals(simulate, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(simulate, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var env = BuildLaunchEnvironment();

        if (OperatingSystem.IsWindows())
        {
            if (TryLaunchWindowsFallback(context.OpenClawHome, env, port))
            {
                return true;
            }
        }

        var command = OpenClawLocator.Resolve(context);
        if (command == null)
        {
            return false;
        }

        return TryLaunchWithCommand(command, env, port);
    }

    private static bool TryLaunchWindowsFallback(string? openClawHome, IDictionary<string, string> env, int port)
    {
        if (string.IsNullOrWhiteSpace(openClawHome))
        {
            return false;
        }

        var gatewayCmdPath = Path.Combine(openClawHome, "gateway.cmd");
        if (TryLaunchGatewayCmdParsed(gatewayCmdPath, env))
        {
            return true;
        }

        if (TryLaunchOpenClawCmdRun(env, port))
        {
            return true;
        }

        if (TryLaunchGatewayCmdDirect(gatewayCmdPath, env))
        {
            return true;
        }

        return false;
    }

    private static IDictionary<string, string> BuildLaunchEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                env[key] = value;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var pathSegments = new List<string>();
            if (env.TryGetValue("APPDATA", out var appData) && !string.IsNullOrWhiteSpace(appData))
            {
                pathSegments.Add(Path.Combine(appData, "npm"));
            }
            if (env.TryGetValue("ProgramFiles", out var programFiles) && !string.IsNullOrWhiteSpace(programFiles))
            {
                pathSegments.Add(Path.Combine(programFiles, "nodejs"));
            }
            if (env.TryGetValue("ProgramFiles(x86)", out var programFilesX86) && !string.IsNullOrWhiteSpace(programFilesX86))
            {
                pathSegments.Add(Path.Combine(programFilesX86, "nodejs"));
            }

            var currentPath = env.GetValueOrDefault("PATH") ?? string.Empty;
            var merged = pathSegments
                .Concat(currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
                .Select(segment => segment.Trim())
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            env["PATH"] = string.Join(';', merged);
        }

        return env;
    }

    private static bool TryLaunchGatewayCmdParsed(string gatewayCmdPath, IDictionary<string, string> env)
    {
        if (!File.Exists(gatewayCmdPath))
        {
            return false;
        }

        try
        {
            var lines = File.ReadAllLines(gatewayCmdPath)
                .Select(line => (line ?? string.Empty).Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            var launchLine = lines
                .AsEnumerable()
                .Reverse()
                .FirstOrDefault(line => !GatewayCmdSkipRegex.IsMatch(line));

            if (string.IsNullOrWhiteSpace(launchLine))
            {
                return false;
            }

            var match = GatewayCmdLineRegex.Match(launchLine);
            if (!match.Success)
            {
                return false;
            }

            var exe = TrimQuotes(match.Groups[1].Value);
            var scriptPath = match.Groups[2].Value;
            var tail = match.Groups[3].Value ?? string.Empty;
            var tailArgs = GatewayCmdArgRegex.Matches(tail)
                .Select(m => TrimQuotes(m.Value))
                .Where(arg => !string.IsNullOrWhiteSpace(arg))
                .ToList();

            var args = new List<string> { scriptPath };
            args.AddRange(tailArgs);

            var workingDir = Path.GetDirectoryName(gatewayCmdPath) ?? Environment.CurrentDirectory;
            return TrySpawnDetached(exe, args, env, workingDir);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLaunchOpenClawCmdRun(IDictionary<string, string> env, int port)
    {
        try
        {
            var tmpRoot = Path.Combine(Path.GetTempPath(), "openclaw");
            Directory.CreateDirectory(tmpRoot);
            var logPath = Path.Combine(tmpRoot, "gateway-detached.log");
            var commandLine = $"\"openclaw.cmd\" gateway run --port {port} >> \"{logPath}\" 2>&1";
            return TrySpawnDetached("cmd.exe", new[] { "/c", commandLine }, env, Environment.CurrentDirectory);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLaunchGatewayCmdDirect(string gatewayCmdPath, IDictionary<string, string> env)
    {
        if (!File.Exists(gatewayCmdPath))
        {
            return false;
        }

        try
        {
            var workingDir = Path.GetDirectoryName(gatewayCmdPath) ?? Environment.CurrentDirectory;
            var cmd = $"\"{gatewayCmdPath}\"";
            return TrySpawnDetached("cmd.exe", new[] { "/c", cmd }, env, workingDir);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLaunchWithCommand(OpenClawCommand command, IDictionary<string, string> env, int port)
    {
        var args = new List<string>();
        args.AddRange(command.BaseArgs);
        args.AddRange(new[] { "gateway", "run", "--port", port.ToString() });

        if (OperatingSystem.IsWindows() && command.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var cmdArgs = new List<string> { "/c", command.FileName };
            cmdArgs.AddRange(args);
            return TrySpawnDetached("cmd.exe", cmdArgs, env, command.WorkingDirectory);
        }

        return TrySpawnDetached(command.FileName, args, env, command.WorkingDirectory);
    }

    private static bool TrySpawnDetached(string fileName, IReadOnlyList<string> args, IDictionary<string, string> env, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var kvp in env)
        {
            startInfo.Environment[kvp.Key] = kvp.Value;
        }

        if (OperatingSystem.IsWindows())
        {
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        }

        try
        {
            var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }
            process.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string TrimQuotes(string value)
    {
        return value.Trim().Trim('"');
    }
}
