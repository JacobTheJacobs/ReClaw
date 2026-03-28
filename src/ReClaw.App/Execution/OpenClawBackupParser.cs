using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ReClaw.App.Actions;

namespace ReClaw.App.Execution;

public static class OpenClawBackupParser
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static OpenClawBackupCreateSummary ParseCreate(OpenClawCommandSummary summary)
    {
        var json = ExtractJson(summary);
        var result = JsonSerializer.Deserialize<OpenClawBackupCreateSummary>(json, Options);
        if (result is null)
        {
            throw new InvalidOperationException("Unable to parse OpenClaw backup create JSON output.");
        }
        return NormalizeCreate(result);
    }

    public static OpenClawBackupVerifySummary ParseVerify(OpenClawCommandSummary summary)
    {
        var json = ExtractJson(summary);
        var result = JsonSerializer.Deserialize<OpenClawBackupVerifySummary>(json, Options);
        if (result is null)
        {
            throw new InvalidOperationException("Unable to parse OpenClaw backup verify JSON output.");
        }
        return NormalizeVerify(result);
    }

    private static OpenClawBackupCreateSummary NormalizeCreate(OpenClawBackupCreateSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.ArchiveRoot)
            && !string.IsNullOrWhiteSpace(summary.ArchivePath)
            && !Path.IsPathRooted(summary.ArchivePath))
        {
            return summary with { ArchivePath = Path.Combine(summary.ArchiveRoot, summary.ArchivePath) };
        }

        return summary;
    }

    private static OpenClawBackupVerifySummary NormalizeVerify(OpenClawBackupVerifySummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.ArchiveRoot)
            && !string.IsNullOrWhiteSpace(summary.ArchivePath)
            && !Path.IsPathRooted(summary.ArchivePath))
        {
            return summary with { ArchivePath = Path.Combine(summary.ArchiveRoot, summary.ArchivePath) };
        }

        return summary;
    }

    private static string ExtractJson(OpenClawCommandSummary summary)
    {
        var joined = string.Join("\n", summary.StdOut).Trim();
        if (string.IsNullOrWhiteSpace(joined))
        {
            joined = string.Join("\n", summary.StdErr).Trim();
        }

        var start = joined.IndexOf('{');
        if (start < 0)
        {
            throw new InvalidOperationException("OpenClaw JSON output not found.");
        }

        var json = joined.Substring(start).Trim();
        return json;
    }
}
