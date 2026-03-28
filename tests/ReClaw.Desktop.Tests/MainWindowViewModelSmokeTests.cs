using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ReClaw.App.Actions;
using ReClaw.App.Execution;
using ReClaw.App.Platform;
using ReClaw.Core;
using ReClaw.Desktop;
using Xunit;

namespace ReClaw.Desktop.Tests;

public sealed class MainWindowViewModelSmokeTests
{
    [Fact]
    public void DefaultState_IsReadyForLaunch()
    {
        var viewModel = CreateViewModel(new ActionResult(true));

        Assert.Equal("ReClaw", viewModel.Title);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.DestinationPath));
        Assert.True(viewModel.Preview);
        Assert.True(viewModel.RunDoctor);
        Assert.True(viewModel.RunFix);
        Assert.True(viewModel.ExportDiagnostics);
        Assert.True(viewModel.RollbackPreview);
        Assert.NotEmpty(viewModel.Actions);
    }

    [Fact]
    public async Task RestorePreview_UpdatesImpactWarningsAndJournal()
    {
        var preview = new RestorePreview(
            "C:\\backups\\snap.tar.gz",
            "tar.gz",
            BackupArchiveKind.ReClaw,
            "C:\\dest",
            "config",
            2,
            2,
            1,
            null,
            2,
            Array.Empty<RestoreAssetImpact>());
        var restore = new RestoreSummary(
            preview.ArchivePath,
            preview.DestinationPath,
            preview.Scope,
            false,
            preview,
            null,
            null,
            null,
            "C:\\data\\journal.jsonl");
        var result = new ActionResult(true, Output: restore, Warnings: new[] { new WarningItem("preview-only", "Preview only") });
        var viewModel = CreateViewModel(result, "backup-restore");

        await ((IAsyncRelayCommand)viewModel.RunRestoreCommand).ExecuteAsync(null);

        Assert.NotNull(viewModel.ImpactSummary);
        Assert.Contains("preview-only", viewModel.WarningsText ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("C:\\data\\journal.jsonl", viewModel.JournalPath);
    }

    [Fact]
    public async Task RecoverOutput_ShowsStepsRollbackDiagnosticsAndEscalation()
    {
        var preview = new RestorePreview(
            "C:\\backups\\snap.tar.gz",
            "tar.gz",
            BackupArchiveKind.ReClaw,
            "C:\\dest",
            "config",
            3,
            3,
            1,
            null,
            3,
            Array.Empty<RestoreAssetImpact>());
        var restore = new RestoreSummary(
            preview.ArchivePath,
            preview.DestinationPath,
            preview.Scope,
            true,
            preview,
            "C:\\backups\\rollback.tar.gz",
            null,
            null,
            "C:\\data\\journal.jsonl");
        var steps = new List<RecoveryStep>
        {
            new("doctor", "success", null, "fix"),
            new("fix", "success", null, "restore"),
            new("restore", "success", null, null)
        };
        var recover = new RecoverSummary(
            restore.ArchivePath,
            restore.DestinationPath,
            restore.Scope,
            true,
            restore,
            null,
            null,
            "C:\\diag\\bundle.tar.gz",
            steps,
            null,
            "C:\\data\\journal.jsonl");
        var result = new ActionResult(true, Output: recover);
        var viewModel = CreateViewModel(result, "recover");

        await ((IAsyncRelayCommand)viewModel.RunRecoverCommand).ExecuteAsync(null);

        Assert.Equal("C:\\backups\\rollback.tar.gz", viewModel.RollbackSnapshot);
        Assert.Equal("C:\\diag\\bundle.tar.gz", viewModel.DiagnosticsBundle);
        Assert.Contains("doctor: success", viewModel.StepsText ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("C:\\data\\journal.jsonl", viewModel.JournalPath);
    }

    [Fact]
    public async Task RollbackOutput_ShowsSnapshotAndImpact()
    {
        var preview = new RestorePreview(
            "C:\\backups\\rollback.tar.gz",
            "tar.gz",
            BackupArchiveKind.ReClaw,
            "C:\\dest",
            "full",
            1,
            1,
            0,
            null,
            1,
            Array.Empty<RestoreAssetImpact>());
        var rollback = new RollbackSummary(
            "C:\\backups\\rollback.tar.gz",
            "C:\\dest",
            "full",
            true,
            preview,
            "C:\\data\\journal.jsonl");
        var result = new ActionResult(true, Output: rollback);
        var viewModel = CreateViewModel(result, "rollback");
        viewModel.RollbackSourcePath = "C:\\backups\\rollback.tar.gz";

        await ((IAsyncRelayCommand)viewModel.RunRollbackCommand).ExecuteAsync(null);

        Assert.NotNull(viewModel.ImpactSummary);
        Assert.Equal("C:\\data\\journal.jsonl", viewModel.JournalPath);
    }

    [Fact]
    public async Task GatewayRepairOutput_PopulatesInventorySections()
    {
        var inventory = new OpenClawInventory(
            new OpenClawRuntimeInfo("configured", "openclaw", null, "2026.3.9", true, 100),
            Array.Empty<OpenClawRuntimeInfo>(),
            new OpenClawConfigInfo("C:\\Users\\me\\.openclaw\\openclaw.json", null, null, null, "2026.3.9", true),
            new OpenClawGatewayInfo(false, false, false, "inactive", null),
            new[] { new OpenClawServiceInfo("schtasks", "openclaw-gateway", null, false, false, false, false) },
            new[] { new OpenClawArtifactInfo("lock", "C:\\temp\\gateway.lock", true, "stale lock") },
            Array.Empty<OpenClawWarning>());
        var summary = new GatewayRepairSummary(
            "gateway-status",
            "status",
            new OpenClawDetectionSummary(null, null, null, "2026.3.9", "2026.3.9", false, false, Array.Empty<OpenClawCandidateSummary>()),
            Array.Empty<GatewayRepairStep>(),
            Array.Empty<GatewayRepairAttempt>(),
            null,
            null,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            inventory);
        var result = new ActionResult(true, Output: summary);
        var viewModel = CreateViewModel(result, "gateway-status");

        await ((IAsyncRelayCommand)viewModel.RunGatewayStatusCommand).ExecuteAsync(null);

        Assert.False(string.IsNullOrWhiteSpace(viewModel.GatewayHealthText));
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ServiceStateText));
    }

    [Fact]
    public async Task GatewayTroubleshootOutput_ShowsStartupReasonAndLadder()
    {
        var status = new OpenClawCommandSummary(
            "openclaw status",
            1,
            false,
            new[] { "Runtime: stopped" },
            Array.Empty<string>(),
            1,
            0,
            false);
        var gatewayStatus = new OpenClawCommandSummary(
            "openclaw gateway status",
            1,
            false,
            new[] { "Runtime: stopped", "RPC probe: failed" },
            Array.Empty<string>(),
            2,
            0,
            false);
        var logs = new OpenClawCommandSummary(
            "openclaw logs --follow",
            1,
            false,
            new[] { "fatal: port already in use" },
            Array.Empty<string>(),
            1,
            0,
            false);
        var doctor = new OpenClawCommandSummary(
            "openclaw doctor",
            1,
            false,
            new[] { "error: config missing" },
            Array.Empty<string>(),
            1,
            0,
            false);
        var channels = new OpenClawCommandSummary(
            "openclaw channels status --probe",
            0,
            false,
            new[] { "probe ok" },
            Array.Empty<string>(),
            1,
            0,
            false);
        var summary = new GatewayTroubleshootSummary(
            status,
            gatewayStatus,
            logs,
            doctor,
            channels,
            "fatal: port already in use",
            false);
        var result = new ActionResult(false, Output: summary);
        var viewModel = CreateViewModel(result, "gateway-troubleshoot");

        await ((IAsyncRelayCommand)viewModel.RunGatewayTroubleshootCommand).ExecuteAsync(null);

        Assert.Equal("fatal: port already in use", viewModel.GatewayStartupReason);
        Assert.Contains("openclaw status", viewModel.GatewayTroubleshootDetails ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openclaw gateway status", viewModel.GatewayTroubleshootDetails ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openclaw logs --follow", viewModel.GatewayTroubleshootDetails ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openclaw doctor", viewModel.GatewayTroubleshootDetails ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openclaw channels status --probe", viewModel.GatewayTroubleshootDetails ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsGatewayHealthy);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task GatewayStatusOutput_UpdatesHealthAndLastChecked()
    {
        var gatewayStatus = new OpenClawCommandSummary(
            "openclaw gateway status --require-rpc",
            1,
            false,
            new[] { "Service: Scheduled Task (registered)", "Runtime: stopped", "RPC probe: failed" },
            Array.Empty<string>(),
            3,
            0,
            false);
        var result = new ActionResult(false, Output: gatewayStatus, Error: "Gateway not ready for browser.", Warnings: new[]
        {
            new WarningItem("gateway-not-ready", "Gateway not ready for browser. Last check 13:17:30: Runtime: stopped | RPC probe: failed.")
        });
        var viewModel = CreateViewModel(result, "gateway-status");

        await ((IAsyncRelayCommand)viewModel.RunGatewayStatusCommand).ExecuteAsync(null);

        Assert.False(viewModel.IsGatewayHealthy);
        Assert.Equal("Broken", viewModel.GatewayStateText);
        Assert.NotEqual("Never", viewModel.LastCheckedTime);
        Assert.Contains("Gateway not ready for browser", viewModel.LastFailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GatewayNotReadyWarning_ForcesUnhealthyState()
    {
        var gatewayStatus = new OpenClawCommandSummary(
            "openclaw gateway status --require-rpc",
            0,
            false,
            new[] { "Runtime: running", "RPC probe: ok", "Service: Scheduled Task (registered)" },
            Array.Empty<string>(),
            3,
            0,
            false);
        var result = new ActionResult(false, Output: gatewayStatus, Error: "Gateway not ready for browser.", Warnings: new[]
        {
            new WarningItem("gateway-not-ready", "Gateway not ready for browser. Last check 13:17:30.")
        });
        var viewModel = CreateViewModel(result, "gateway-status");
        viewModel.IsGatewayHealthy = true;

        await ((IAsyncRelayCommand)viewModel.RunGatewayStatusCommand).ExecuteAsync(null);

        Assert.False(viewModel.IsGatewayHealthy);
        Assert.Equal("Unstable", viewModel.GatewayStateText);
    }

    [Fact]
    public void BackupLevelSelection_MapsScope()
    {
        var viewModel = CreateViewModel(new ActionResult(true));

        Assert.Equal("full", viewModel.BackupCreateScope);

        viewModel.SelectedBackupLevel = BackupLevel.OpenClawOnly;
        Assert.Equal("config+creds+sessions", viewModel.BackupCreateScope);

        viewModel.SelectedBackupLevel = BackupLevel.ConfigOnly;
        Assert.Equal("config", viewModel.BackupCreateScope);
    }

    [Fact]
    public void MissingInstall_ForcesSetupMode()
    {
        var original = Environment.GetEnvironmentVariable("RECLAW_SETUP_FORCE");
        Environment.SetEnvironmentVariable("RECLAW_SETUP_FORCE", "1");
        try
        {
            var viewModel = CreateViewModel(new ActionResult(true));
            Assert.True(viewModel.IsSetupMode);
            Assert.False(viewModel.IsOperatorMode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RECLAW_SETUP_FORCE", original);
        }
    }

    private static MainWindowViewModel CreateViewModel(ActionResult result, string actionId = "backup-restore")
    {
        var context = PathDefaults.CreateDefaultContext();
        Func<string, object, Task<ActionResult>> executor = (id, _) =>
            Task.FromResult(id == actionId ? result : new ActionResult(true));
        return new MainWindowViewModel(executor, context, new Progress<ActionEvent>(_ => { }));
    }
}
