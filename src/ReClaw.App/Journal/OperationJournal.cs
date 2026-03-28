using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReClaw.App.Actions;
using ReClaw.Core;

namespace ReClaw.App.Journal;

public sealed record OperationJournalEntry(
    string Action,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Success,
    int ExitCode,
    string? Summary,
    string? Error,
    string? Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Artifacts,
    string? RollbackPoint,
    string? DiagnosticsBundlePath,
    bool? OutputTruncated,
    int? StdOutLineCount,
    int? StdErrLineCount);

public static class OperationJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static void TryAppend(
        ActionContext context,
        ActionDescriptor descriptor,
        object? input,
        ActionResult result,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        try
        {
            var journalPath = Path.Combine(context.DataDirectory, "journal.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath) ?? ".");
            var entry = BuildEntry(descriptor, input, result, startedAt, completedAt);
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            File.AppendAllText(journalPath, json + Environment.NewLine);
        }
        catch
        {
            // best effort journal
        }
    }

    private static OperationJournalEntry BuildEntry(
        ActionDescriptor descriptor,
        object? input,
        ActionResult result,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        var exitCode = result.ExitCode ?? (result.Success ? 0 : 1);
        var summary = RedactText(BuildSummary(result));
        var (changes, artifacts, rollback) = ExtractArtifacts(result);
        var diagnosticsPath = ExtractDiagnostics(result);
        var (command, args, outputMeta) = BuildCommandArgs(descriptor, input, result);

        return new OperationJournalEntry(
            descriptor.Id,
            startedAt,
            completedAt,
            result.Success,
            exitCode,
            summary,
            RedactText(result.Error),
            RedactText(command),
            args,
            changes,
            artifacts,
            rollback,
            diagnosticsPath,
            outputMeta.OutputTruncated,
            outputMeta.StdOutLineCount,
            outputMeta.StdErrLineCount);
    }

    private static string? BuildSummary(ActionResult result)
    {
        if (!result.Success)
        {
            return result.Error ?? "Action failed.";
        }

        return result.Output switch
        {
            BackupVerificationSummary summary =>
                $"Backup OK: {summary.ArchivePath} (entries: {summary.EntryCount}, assets: {summary.AssetCount}, payload: {summary.PayloadEntryCount})",
            BackupExportSummary export =>
                export.Verified
                    ? $"Backup exported and verified: {export.ArchivePath}"
                    : $"Backup exported: {export.ArchivePath}",
            RestoreSummary restore =>
                $"Restore {(restore.Applied ? "completed" : "preview")}: {restore.Preview.RestorePayloadEntries} entries ({restore.Preview.OverwritePayloadEntries} overwrite)",
            DoctorSummary =>
                "Doctor completed.",
            FixSummary =>
                "Fix completed.",
            RecoverSummary recover =>
                recover.Applied ? "Recovery completed." : "Recovery preview.",
            RollbackSummary rollback =>
                rollback.Applied ? "Rollback completed." : "Rollback preview.",
            ResetSummary reset =>
                reset.Applied ? "Reset completed." : "Reset preview.",
            DiagnosticsBundleSummary bundle =>
                $"Diagnostics bundle exported: {bundle.BundlePath}",
            StatusSummary status =>
                $"Status collected (backups: {status.BackupCount})",
            OpenClawCommandSummary =>
                "OpenClaw command completed.",
            string value => value,
            _ => "Completed successfully."
        };
    }

    private static (IReadOnlyList<string> Changes, IReadOnlyList<string> Artifacts, string? RollbackPoint) ExtractArtifacts(ActionResult result)
    {
        if (result.Output is RestoreSummary restore)
        {
            var changes = restore.Preview.Assets
                .Where(asset => asset.Exists)
                .Select(asset => asset.ArchivePath)
                .ToArray();
            var artifacts = string.IsNullOrWhiteSpace(restore.SnapshotPath)
                ? Array.Empty<string>()
                : new[] { restore.SnapshotPath };
            return (changes, artifacts, restore.SnapshotPath);
        }

        if (result.Output is FixSummary fix)
        {
            var artifacts = string.IsNullOrWhiteSpace(fix.SnapshotPath)
                ? Array.Empty<string>()
                : new[] { fix.SnapshotPath };
            return (Array.Empty<string>(), artifacts, fix.SnapshotPath);
        }

        if (result.Output is DiagnosticsBundleSummary bundle)
        {
            return (Array.Empty<string>(), new[] { bundle.BundlePath }, bundle.BundlePath);
        }

        if (result.Output is RollbackSummary rollback)
        {
            var changes = rollback.Preview.Assets
                .Where(asset => asset.Exists)
                .Select(asset => asset.ArchivePath)
                .ToArray();
            return (changes, Array.Empty<string>(), null);
        }

        if (result.Output is ResetSummary reset)
        {
            return (reset.Plan.DeletePaths, Array.Empty<string>(), null);
        }

        return (Array.Empty<string>(), Array.Empty<string>(), null);
    }

    private static string? ExtractDiagnostics(ActionResult result)
    {
        return result.Output is IDiagnosticsBundleCarrier carrier
            ? carrier.DiagnosticsBundlePath
            : null;
    }

    private static (string? Command, IReadOnlyList<string> Args, OutputMeta Meta) BuildCommandArgs(ActionDescriptor descriptor, object? input, ActionResult result)
    {
        var commandSummary = ExtractOpenClawSummary(result);
        if (commandSummary != null)
        {
            return (commandSummary.Command, RedactArgs(descriptor.CommandArgs ?? Array.Empty<string>()), new OutputMeta(commandSummary));
        }

        if (descriptor.CommandArgs != null)
        {
            return (string.Join(' ', descriptor.CommandArgs), RedactArgs(descriptor.CommandArgs), new OutputMeta(null));
        }

        var args = input == null ? Array.Empty<string>() : RedactInput(input);
        return (descriptor.Id, args, new OutputMeta(null));
    }

    private static OpenClawCommandSummary? ExtractOpenClawSummary(ActionResult result)
    {
        return result.Output switch
        {
            OpenClawCommandSummary command => command,
            DoctorSummary doctor => doctor.Command,
            FixSummary fix => fix.Command,
            _ => null
        };
    }

    private static IReadOnlyList<string> RedactArgs(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (IsSensitiveArg(arg))
            {
                if (arg.Contains('='))
                {
                    var key = arg.Split('=')[0];
                    list[i] = $"{key}=***redacted***";
                }
                else if (i + 1 < list.Count)
                {
                    list[i + 1] = "***redacted***";
                }
            }
        }

        return list;
    }

    private static IReadOnlyList<string> RedactInput(object input)
    {
        var props = input.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(prop => prop.CanRead)
            .ToList();

        var args = new List<string>();
        foreach (var prop in props)
        {
            var value = prop.GetValue(input);
            if (value == null) continue;
            var key = prop.Name;
            var valueText = FormatValue(value);
            if (IsSensitiveKey(key))
            {
                valueText = "***redacted***";
            }
            args.Add($"{key}={valueText}");
        }

        return args;
    }

    private static string FormatValue(object value)
    {
        if (value is string s) return s;
        if (value is Array array)
        {
            var items = array.Cast<object>().Select(FormatValue);
            return "[" + string.Join(", ", items) + "]";
        }
        return value.ToString() ?? string.Empty;
    }

    private static bool IsSensitiveArg(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return false;
        var normalized = arg.ToLowerInvariant();
        return normalized is "-p" or "--password" or "--token" or "--secret" or "--key" or "--credential"
               || normalized.StartsWith("--password=")
               || normalized.StartsWith("--token=")
               || normalized.StartsWith("--secret=")
               || normalized.StartsWith("--key=")
               || normalized.StartsWith("--credential=");
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

    private static readonly Regex[] RedactionPatterns =
    {
        new Regex(@"(?i)\b(bearer|token|secret|password|apikey|api_key|credential|key)\b\s*[:=]\s*([^\s\""]+)", RegexOptions.Compiled),
        new Regex(@"(?i)\b(bearer)\s+([A-Za-z0-9\-._~+/]+=*)", RegexOptions.Compiled),
        new Regex(@"(?i)(token|secret|password|apikey|api_key)=([^&\s]+)", RegexOptions.Compiled),
        new Regex(@"(?i)secret://[^\s]+", RegexOptions.Compiled)
    };

    private static string? RedactText(string? input)
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

                var value = match.Groups[2].Value;
                return match.Value.Replace(value, "***redacted***");
            });
        }
        return output;
    }

    private readonly record struct OutputMeta(bool? OutputTruncated, int? StdOutLineCount, int? StdErrLineCount)
    {
        public OutputMeta(OpenClawCommandSummary? command)
            : this(command?.OutputTruncated, command?.StdOutLineCount, command?.StdErrLineCount)
        {
        }
    }
}
