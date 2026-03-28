using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Actions;
using ReClaw.Core;

namespace ReClaw.App.Diagnostics;

public sealed class DiagnosticsBundleService
{
    private const int MaxLogFiles = 200;
    private const long MaxLogFileBytes = 10 * 1024 * 1024;
    private static readonly Regex[] RedactionPatterns =
    {
        new Regex(@"(?i)\b(bearer|token|secret|password|apikey|api_key|credential|key)\b\s*[:=]\s*([^\s\""]+)", RegexOptions.Compiled),
        new Regex(@"(?i)\b(bearer)\s+([A-Za-z0-9\-._~+/]+=*)", RegexOptions.Compiled),
        new Regex(@"(?i)(token|secret|password|apikey|api_key)=([^&\s]+)", RegexOptions.Compiled),
        new Regex(@"(?i)\b(secret|token|password|apikey|api_key)\b\s*:\s*\""[^\\""]+\""", RegexOptions.Compiled),
        new Regex(@"(?i)secret://[^\s]+", RegexOptions.Compiled),
        new Regex(@"(?i)bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.Compiled)
    };

    public async Task<DiagnosticsBundleSummary> CreateBundleAsync(ActionContext context, string? outputPath = null, CancellationToken ct = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        var bundlePath = outputPath ?? BuildBundlePath(context.DataDirectory);
        var stagingRoot = Path.Combine(context.TempDirectory, $"reclaw-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);

        var state = new BundleState();

        try
        {
            var metaPath = Path.Combine(stagingRoot, "reclaw-context.json");
            var meta = new
            {
                timestamp = DateTimeOffset.UtcNow,
                os = RuntimeInformation.OSDescription,
                runtime = RuntimeInformation.FrameworkDescription,
                openClawHome = context.OpenClawHome,
                configDirectory = context.ConfigDirectory,
                dataDirectory = context.DataDirectory,
                logsDirectory = context.LogsDirectory
            };
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
            AddIncluded(state, metaPath, "reclaw-context.json");

            WriteRedactedEnvironment(stagingRoot, state);
            CopyJournal(context, stagingRoot, state);
            CopyLogs(context, stagingRoot, state);
            await CopyRedactedConfigAsync(context, stagingRoot, state, ct).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(bundlePath) ?? ".");
            TarUtils.CreateTarGzDirectory(stagingRoot, bundlePath);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }

        return new DiagnosticsBundleSummary(bundlePath, state.Included.Count, state.TotalBytes, state.Included, state.Skipped);
    }

    private static string BuildBundlePath(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(dataDirectory, $"reclaw_diagnostics_{timestamp}.tar.gz");
    }

    private static void CopyJournal(ActionContext context, string stagingRoot, BundleState state)
    {
        var journalPath = Path.Combine(context.DataDirectory, "journal.jsonl");
        if (!File.Exists(journalPath))
        {
            state.Skipped.Add("journal.jsonl (missing)");
            return;
        }

        var destPath = Path.Combine(stagingRoot, "reclaw-journal.jsonl");
        CopyFile(state, journalPath, destPath, "reclaw-journal.jsonl");
    }

    private static void CopyLogs(ActionContext context, string stagingRoot, BundleState state)
    {
        var logDirs = new[]
        {
            context.LogsDirectory,
            Path.Combine(context.OpenClawHome, "logs"),
            Path.Combine(context.OpenClawHome, "gateway", "logs")
        };

        foreach (var logDir in logDirs.Where(Directory.Exists))
        {
            var targetRoot = Path.Combine(stagingRoot, "logs", Path.GetFileName(logDir));
            CopyLogDirectory(logDir, targetRoot, state);
        }
    }

    private static void WriteRedactedEnvironment(string stagingRoot, BundleState state)
    {
        var envPath = Path.Combine(stagingRoot, "env.redacted.txt");
        try
        {
            using var writer = new StreamWriter(envPath);
            foreach (var key in Environment.GetEnvironmentVariables().Keys)
            {
                var name = key?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var value = Environment.GetEnvironmentVariable(name) ?? string.Empty;
                var safeValue = IsSensitiveKey(name) ? "***redacted***" : RedactText(value);
                writer.WriteLine($"{name}={safeValue}");
            }

            AddIncluded(state, envPath, "env.redacted.txt");
        }
        catch
        {
            state.Skipped.Add("env.redacted.txt (write failed)");
        }
    }

    private static async Task CopyRedactedConfigAsync(ActionContext context, string stagingRoot, BundleState state, CancellationToken ct)
    {
        var candidates = new[]
        {
            Path.Combine(context.OpenClawHome, "openclaw.json"),
            Path.Combine(context.ConfigDirectory, "openclaw.json")
        };

        var configPath = candidates.FirstOrDefault(File.Exists);
        if (configPath == null)
        {
            state.Skipped.Add("openclaw.json (missing)");
            return;
        }

        var destPath = Path.Combine(stagingRoot, "openclaw.redacted.json");
        try
        {
            var text = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
            var node = JsonNode.Parse(text);
            if (node == null)
            {
                state.Skipped.Add("openclaw.json (empty)");
                return;
            }

            RedactJsonNode(node);
            var payload = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(destPath, payload, ct).ConfigureAwait(false);
            AddIncluded(state, destPath, "openclaw.redacted.json");
        }
        catch
        {
            state.Skipped.Add("openclaw.json (redaction failed)");
        }
    }

    private static void CopyLogDirectory(string sourceDir, string targetDir, BundleState state)
    {
        Directory.CreateDirectory(targetDir);
        var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Where(IsLogFile)
            .Take(MaxLogFiles)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxLogFileBytes)
                {
                    state.Skipped.Add($"{file} (too large)");
                    continue;
                }

                var relative = Path.GetRelativePath(sourceDir, file);
                var destPath = Path.Combine(targetDir, relative);
                CopyRedactedTextFile(state, file, destPath, Path.Combine("logs", Path.GetFileName(sourceDir), relative));
            }
            catch
            {
                state.Skipped.Add($"{file} (copy failed)");
            }
        }
    }

    private static bool IsLogFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".log" or ".txt" or ".json" or ".ndjson";
    }

    private static void CopyFile(BundleState state, string source, string dest, string entryName)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
            File.Copy(source, dest, overwrite: true);
            AddIncluded(state, dest, entryName);
        }
        catch
        {
            state.Skipped.Add($"{entryName} (copy failed)");
        }
    }

    private static void CopyRedactedTextFile(BundleState state, string source, string dest, string entryName)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
            using var reader = new StreamReader(source);
            using var writer = new StreamWriter(dest);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                writer.WriteLine(RedactText(line));
            }
            AddIncluded(state, dest, entryName);
        }
        catch
        {
            state.Skipped.Add($"{entryName} (copy failed)");
        }
    }

    private static void AddIncluded(BundleState state, string path, string entryName)
    {
        state.Included.Add(entryName.Replace('\\', '/'));
        if (File.Exists(path))
        {
            state.TotalBytes += new FileInfo(path).Length;
        }
    }

    private static void RedactJsonNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kvp => kvp.Key).ToList())
            {
                if (IsSensitiveKey(key))
                {
                    obj[key] = "***redacted***";
                }
                else
                {
                    RedactJsonNode(obj[key]);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                RedactJsonNode(item);
            }
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var normalized = key.ToLowerInvariant();
        return normalized.Contains("password")
            || normalized.Contains("secret")
            || normalized.Contains("token")
            || normalized.Contains("credential")
            || normalized.Contains("apikey")
            || normalized.Contains("api_key")
            || normalized.Contains("key");
    }

    private static string RedactText(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var output = input;
        foreach (var regex in RedactionPatterns)
        {
            output = regex.Replace(output, match =>
            {
                if (match.Groups.Count < 3)
                {
                    return "***redacted***";
                }

                var prefix = match.Groups[1].Value;
                var value = match.Groups[2].Value;
                return match.Value.Replace(value, "***redacted***");
            });
        }
        return output;
    }

    private sealed class BundleState
    {
        public List<string> Included { get; } = new();
        public List<string> Skipped { get; } = new();
        public long TotalBytes { get; set; }
    }
}
