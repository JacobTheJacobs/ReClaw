using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using ReClaw.App.Actions;
using ReClaw.App.Platform;
using ReClaw.Core;
using ReClaw.Desktop;
using Xunit;

namespace ReClaw.Desktop.Tests;

public sealed class DesktopUiAutomationTests
{
    [AvaloniaFact]
    [Trait("Category", "Live")]
    public void Dashboard_Loads()
    {
        var window = new MainWindow();
        window.Show();

        var title = window.FindControl<TextBlock>("DashboardTitle");
        var actionSearch = window.FindControl<TextBox>("ActionSearchInput");
        var actionsGrid = window.FindControl<ItemsControl>("ActionsGrid");

        Assert.NotNull(title);
        Assert.Equal("ReClaw", title.Text);
        Assert.NotNull(actionSearch);
        Assert.NotNull(actionsGrid);
    }

    [AvaloniaFact]
    [Trait("Category", "Live")]
    public async Task RestorePreview_RendersImpactAndWarnings()
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
        var result = new ActionResult(true, Output: restore, Warnings: new[] { new WarningItem("preview-required", "Confirm required") });
        var viewModel = BuildViewModel(result, "backup-restore");
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        await ((IAsyncRelayCommand)viewModel.RunRestoreCommand).ExecuteAsync(null);

        var impact = window.FindControl<TextBlock>("ImpactSummaryText");
        var warnings = window.FindControl<TextBox>("WarningsBox");
        Assert.False(string.IsNullOrWhiteSpace(impact.Text));
        Assert.Contains("preview-required", warnings.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    [Trait("Category", "Live")]
    public async Task ConfirmationGates_RenderWarning()
    {
        var preview = new RestorePreview(
            "C:\\backups\\snap.tar.gz",
            "tar.gz",
            BackupArchiveKind.ReClaw,
            "C:\\dest",
            "full",
            1,
            1,
            1,
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
            ResetMode.PreserveBackups,
            null,
            "C:\\data\\journal.jsonl");
        var result = new ActionResult(false, Output: restore, Error: "Reset requires confirmation.", Warnings: new[] { new WarningItem("confirmation-required", "Confirm reset") });
        var viewModel = BuildViewModel(result, "backup-restore");
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        await ((IAsyncRelayCommand)viewModel.RunRestoreCommand).ExecuteAsync(null);

        var warnings = window.FindControl<TextBox>("WarningsBox");
        Assert.Contains("confirmation-required", warnings.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    [Trait("Category", "Live")]
    public async Task RollbackAndDiagnostics_Appear()
    {
        var preview = new RestorePreview(
            "C:\\backups\\snap.tar.gz",
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
            true,
            preview,
            "C:\\backups\\rollback.tar.gz",
            null,
            null,
            "C:\\data\\journal.jsonl");
        var recover = new RecoverSummary(
            restore.ArchivePath,
            restore.DestinationPath,
            restore.Scope,
            true,
            restore,
            null,
            null,
            "C:\\diag\\bundle.tar.gz",
            Array.Empty<RecoveryStep>(),
            "clean-install",
            "C:\\data\\journal.jsonl");
        var result = new ActionResult(true, Output: recover);
        var viewModel = BuildViewModel(result, "recover");
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        await ((IAsyncRelayCommand)viewModel.RunRecoverCommand).ExecuteAsync(null);

        var rollback = window.FindControl<TextBox>("RollbackSnapshotBox");
        var diagnostics = window.FindControl<TextBox>("DiagnosticsBundleBox");
        var escalation = window.FindControl<TextBox>("NextEscalationBox");
        Assert.Equal("C:\\backups\\rollback.tar.gz", rollback.Text);
        Assert.Equal("C:\\diag\\bundle.tar.gz", diagnostics.Text);
        Assert.Equal("clean-install", escalation.Text);
    }

    private static MainWindowViewModel BuildViewModel(ActionResult result, string actionId)
    {
        var context = PathDefaults.CreateDefaultContext();
        Func<string, object, Task<ActionResult>> executor = (id, _) =>
            Task.FromResult(id == actionId ? result : new ActionResult(true));
        return new MainWindowViewModel(executor, context, new Progress<ActionEvent>(_ => { }));
    }
}
