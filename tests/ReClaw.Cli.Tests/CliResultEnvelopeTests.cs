using System;
using System.Linq;
using System.Text.Json;
using ReClaw.App.Actions;
using ReClaw.Cli;
using ReClaw.App.Execution;
using ReClaw.Core;
using Xunit;

namespace ReClaw.Cli.Tests;

public sealed class CliResultEnvelopeTests
{
    [Fact]
    public void BuildEnvelope_ForVerify_IncludesRequiredFields()
    {
        var output = new BackupVerificationSummary(
            "C:\\backups\\sample.tar.gz",
            "tar.gz",
            10,
            2,
            3,
            "2026-03-22T12:00:00Z",
            1);
        var result = new ActionResult(true, Output: output, ExitCode: 0);

        var envelope = CliResultFormatter.Build("backup-verify", result);
        var json = JsonSerializer.Serialize(envelope, CliResultFormatter.JsonOptions);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("backup-verify", root.GetProperty("action").GetString());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("details", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("artifacts", out _));
        Assert.True(root.TryGetProperty("changes", out _));
        Assert.True(root.TryGetProperty("rollbackPoint", out _));
    }

    [Fact]
    public void BuildEnvelope_ForFailure_ReturnsStructuredError()
    {
        var result = new ActionResult(false, Error: "Backup verification failed");
        var envelope = CliResultFormatter.Build("backup-verify", result);
        var json = JsonSerializer.Serialize(envelope, CliResultFormatter.JsonOptions);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("backup-verify", root.GetProperty("action").GetString());
        Assert.Equal("Backup verification failed", root.GetProperty("summary").GetString());
        Assert.Equal(CliExitCodes.ActionFailed, root.GetProperty("exitCode").GetInt32());
        Assert.True(root.GetProperty("details").TryGetProperty("error", out _));
    }

    [Fact]
    public void BuildEnvelope_ForFix_IncludesSnapshotRollback()
    {
        var command = new OpenClawCommandSummary(
            "openclaw doctor --fix",
            0,
            false,
            Array.Empty<string>(),
            Array.Empty<string>(),
            0,
            0,
            false);
        var output = new FixSummary(command, "C:\\backups\\pre_fix.tar.gz", "C:\\diag\\bundle.tar.gz");
        var warnings = new[] { new WarningItem("diagnostics-failed", "Diagnostics failed") };
        var result = new ActionResult(true, Output: output, Warnings: warnings);

        var envelope = CliResultFormatter.Build("fix", result);
        var json = JsonSerializer.Serialize(envelope, CliResultFormatter.JsonOptions);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("C:\\backups\\pre_fix.tar.gz", root.GetProperty("rollbackPoint").GetString());
        var artifacts = root.GetProperty("artifacts").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("C:\\backups\\pre_fix.tar.gz", artifacts);
        Assert.Contains("C:\\diag\\bundle.tar.gz", artifacts);
        var warning = root.GetProperty("warnings").EnumerateArray().First();
        Assert.Equal("diagnostics-failed", warning.GetProperty("code").GetString());
    }

    [Fact]
    public void BuildEnvelope_ForRestorePreview_IncludesWarnings()
    {
        var preview = new RestorePreview(
            "C:\\backups\\sample.tar.gz",
            "tar.gz",
            BackupArchiveKind.ReClaw,
            "C:\\dest",
            "config",
            1,
            1,
            0,
            null,
            1,
            Array.Empty<RestoreAssetImpact>());
        var restore = new RestoreSummary(
            preview.ArchivePath,
            preview.DestinationPath,
            preview.Scope,
            false,
            preview,
            null,
            null,
            null);
        var warnings = new[] { new WarningItem("preview-only", "Restore preview only") };
        var result = new ActionResult(true, Output: restore, Warnings: warnings);

        var envelope = CliResultFormatter.Build("backup-restore", result);
        var json = JsonSerializer.Serialize(envelope, CliResultFormatter.JsonOptions);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        var warning = root.GetProperty("warnings").EnumerateArray().First();
        Assert.Equal("preview-only", warning.GetProperty("code").GetString());
    }

    [Fact]
    public void BuildEnvelope_ForFailure_WithWarnings()
    {
        var warnings = new[]
        {
            new WarningItem("partial-failure", "Partial failure"),
            new WarningItem("rollback-available", "Rollback available")
        };
        var result = new ActionResult(false, Error: "Restore failed", Warnings: warnings);

        var envelope = CliResultFormatter.Build("backup-restore", result);
        var json = JsonSerializer.Serialize(envelope, CliResultFormatter.JsonOptions);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        var codes = root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToArray();
        Assert.Contains("partial-failure", codes);
        Assert.Contains("rollback-available", codes);
    }

    [Fact]
    public void BuildEnvelope_ForGatewayRepair_IncludesInventoryWarnings()
    {
        var inventory = new OpenClawInventory(
            ActiveRuntime: new OpenClawRuntimeInfo("configured", "openclaw", null, "2026.3.9", true, 100),
            CandidateRuntimes: Array.Empty<OpenClawRuntimeInfo>(),
            Config: null,
            Gateway: new OpenClawGatewayInfo(false, false, false, null, null),
            Services: Array.Empty<OpenClawServiceInfo>(),
            Artifacts: Array.Empty<OpenClawArtifactInfo>(),
            Warnings: new[] { new OpenClawWarning("config-newer-than-runtime", "Config newer than runtime.", "Update runtime") }
        );
        var summary = new GatewayRepairSummary(
            "gateway-status",
            "status",
            new OpenClawDetectionSummary(null, null, null, "2026.3.9", "2026.3.13", false, false, Array.Empty<OpenClawCandidateSummary>()),
            Array.Empty<GatewayRepairStep>(),
            Array.Empty<GatewayRepairAttempt>(),
            null,
            null,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            inventory);
        var result = new ActionResult(true, Output: summary, Warnings: new[] { new WarningItem("config-newer-than-runtime", "Config newer than runtime") });

        var envelope = CliResultFormatter.Build("gateway-status", result);
        var json = JsonSerializer.Serialize(envelope, CliResultFormatter.JsonOptions);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var invWarnings = root.GetProperty("inventoryWarnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToArray();
        Assert.Contains("config-newer-than-runtime", invWarnings);
    }
}
