using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Actions;
using ReClaw.Cli;
using ReClaw.Core;
using ReClaw.Core.IO;
using Xunit;

namespace ReClaw.Interop.Tests;

public sealed class BackupRestoreInteropTests
{
    [Fact]
    public async Task ExportVerifyRestore_RoundTrip_WritesSnapshot()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");
        WriteFile(Path.Combine(sourceDir.Path, "credentials", "tokens.json"), "{ \"token\": \"x\" }");
        WriteFile(Path.Combine(sourceDir.Path, "sessions", "session.json"), "{ \"id\": 1 }");

        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, sourceDir.Path);
        var executor = DefaultActionRegistry.Create().Executor;

        var password = "test-pass";
        var exportResult = await ExecuteAsync(executor, "backup-export",
            new BackupExportInput("config+creds+sessions", true, null, password), context);
        if (!exportResult.Success)
        {
            throw new InvalidOperationException($"Export failed: {exportResult.Error ?? "<null>"} | Output: {exportResult.Output}");
        }
        var exportSummary = Assert.IsType<BackupExportSummary>(exportResult.Output);

        var verifyResult = await ExecuteAsync(executor, "backup-verify",
            new BackupVerifyInput(exportSummary.ArchivePath, password), context);
        Assert.True(verifyResult.Success, verifyResult.Error ?? verifyResult.Output?.ToString());

        using var restoreDest = new TempDir();
        WriteFile(Path.Combine(restoreDest.Path, "openclaw.json"), "{ \"ok\": \"old\" }");

        var restoreResult = await ExecuteAsync(executor, "backup-restore",
            new BackupRestoreInput(exportSummary.ArchivePath, restoreDest.Path, password, "config+creds+sessions"),
            context);
        Assert.True(restoreResult.Success, restoreResult.Error ?? restoreResult.Output?.ToString());
        var restoreSummary = Assert.IsType<RestoreSummary>(restoreResult.Output);

        Assert.False(string.IsNullOrWhiteSpace(restoreSummary.SnapshotPath));
        Assert.True(File.Exists(restoreSummary.SnapshotPath!));
        Assert.Equal("{ \"ok\": true }", File.ReadAllText(Path.Combine(restoreDest.Path, "openclaw.json")));
        Assert.True(File.Exists(Path.Combine(restoreDest.Path, "credentials", "tokens.json")));
        Assert.True(File.Exists(Path.Combine(restoreDest.Path, "sessions", "session.json")));

        var envelope = CliResultFormatter.Build("backup-restore", restoreResult);
        Assert.Equal(restoreSummary.SnapshotPath, envelope.RollbackPoint);
        Assert.Contains("openclaw.json", envelope.Changes);
    }

    [Fact]
    public async Task LegacyArchiveVerify_Succeeds()
    {
        using var temp = new TempDir();
        var legacyRoot = Path.Combine(temp.Path, "legacy");
        Directory.CreateDirectory(legacyRoot);
        WriteFile(Path.Combine(legacyRoot, "openclaw.json"), "{ \"ok\": true }");

        var manifest = new
        {
            files = new[] { "openclaw.json" },
            timestamp = "legacy-1"
        };
        File.WriteAllText(Path.Combine(legacyRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        var archivePath = Path.Combine(temp.Path, "legacy.tar.gz");
        TarUtils.CreateTarGzDirectory(legacyRoot, archivePath);

        var context = CreateContext(temp.Path, legacyRoot);
        var executor = DefaultActionRegistry.Create().Executor;

        var result = await ExecuteAsync(executor, "backup-verify",
            new BackupVerifyInput(archivePath), context);
        Assert.True(result.Success);
        var summary = Assert.IsType<BackupVerificationSummary>(result.Output);
        Assert.Equal(0, summary.SchemaVersion);
    }

    [Fact]
    public async Task Restore_WrongPassword_FailsCleanly()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, sourceDir.Path);
        var executor = DefaultActionRegistry.Create().Executor;

        var exportResult = await ExecuteAsync(executor, "backup-export",
            new BackupExportInput("config", true, null, "correct-password"), context);
        Assert.True(exportResult.Success);
        var exportSummary = Assert.IsType<BackupExportSummary>(exportResult.Output);

        using var restoreDest = new TempDir();
        WriteFile(Path.Combine(restoreDest.Path, "openclaw.json"), "{ \"ok\": \"old\" }");

        var restoreResult = await ExecuteAsync(executor, "backup-restore",
            new BackupRestoreInput(exportSummary.ArchivePath, restoreDest.Path, "wrong-password", "config"),
            context);
        Assert.False(restoreResult.Success);
        Assert.Equal("{ \"ok\": \"old\" }", File.ReadAllText(Path.Combine(restoreDest.Path, "openclaw.json")));
    }

    [Fact]
    public async Task Restore_TamperedArchive_FailsCleanly()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        var tamperedPath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}-tampered.tar.gz");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, scope: "full");
            using var extracted = new TempDir();
            TarUtils.ExtractTarGzToDirectory(archivePath, extracted.Path);
            WriteFile(Path.Combine(extracted.Path, "openclaw.json"), "{ \"ok\": false }");
            TarUtils.CreateTarGzDirectory(extracted.Path, tamperedPath);

            using var contextRoot = new TempDir();
            var context = CreateContext(contextRoot.Path, sourceDir.Path);
            var executor = DefaultActionRegistry.Create().Executor;

            using var restoreDest = new TempDir();
            WriteFile(Path.Combine(restoreDest.Path, "openclaw.json"), "{ \"ok\": \"old\" }");

            var restoreResult = await ExecuteAsync(executor, "backup-restore",
                new BackupRestoreInput(tamperedPath, restoreDest.Path, null, "config"),
                context);
            Assert.False(restoreResult.Success);
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteFile(tamperedPath);
        }
    }

    [Fact]
    public async Task ScopedRestore_RoundTrip()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");
        WriteFile(Path.Combine(sourceDir.Path, "credentials", "tokens.json"), "{ \"token\": \"x\" }");
        WriteFile(Path.Combine(sourceDir.Path, "sessions", "session.json"), "{ \"id\": 1 }");
        WriteFile(Path.Combine(sourceDir.Path, "workspace", "notes.txt"), "skip");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, scope: "full");

            using var contextRoot = new TempDir();
            var context = CreateContext(contextRoot.Path, sourceDir.Path);
            var executor = DefaultActionRegistry.Create().Executor;

            using var restoreDest = new TempDir();
            WriteFile(Path.Combine(restoreDest.Path, "openclaw.json"), "{ \"ok\": \"old\" }");

            var restoreResult = await ExecuteAsync(executor, "backup-restore",
                new BackupRestoreInput(archivePath, restoreDest.Path, null, "config"),
                context);
            Assert.True(restoreResult.Success);

            Assert.True(File.Exists(Path.Combine(restoreDest.Path, "openclaw.json")));
            Assert.False(Directory.Exists(Path.Combine(restoreDest.Path, "credentials")));
            Assert.False(Directory.Exists(Path.Combine(restoreDest.Path, "sessions")));
            Assert.False(Directory.Exists(Path.Combine(restoreDest.Path, "workspace")));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task Preview_Matches_Restore_Impact()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, scope: "full");

            using var contextRoot = new TempDir();
            var context = CreateContext(contextRoot.Path, sourceDir.Path);
            var executor = DefaultActionRegistry.Create().Executor;

            using var restoreDest = new TempDir();
            WriteFile(Path.Combine(restoreDest.Path, "openclaw.json"), "{ \"ok\": \"old\" }");

            var previewResult = await ExecuteAsync(executor, "backup-restore",
                new BackupRestoreInput(archivePath, restoreDest.Path, null, "config", Preview: true),
                context);
            Assert.True(previewResult.Success);
            var previewSummary = Assert.IsType<RestoreSummary>(previewResult.Output);
            Assert.Equal(1, previewSummary.Preview.OverwritePayloadEntries);
            Assert.Contains(previewResult.Warnings ?? Array.Empty<WarningItem>(), w => w.Code == "preview-only");

            var restoreResult = await ExecuteAsync(executor, "backup-restore",
                new BackupRestoreInput(archivePath, restoreDest.Path, null, "config"),
                context);
            Assert.True(restoreResult.Success);
            var restoreSummary = Assert.IsType<RestoreSummary>(restoreResult.Output);

            Assert.Equal(previewSummary.Preview.OverwritePayloadEntries, restoreSummary.Preview.OverwritePayloadEntries);
            Assert.Equal("{ \"ok\": true }", File.ReadAllText(Path.Combine(restoreDest.Path, "openclaw.json")));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task SafeReset_WithoutConfirmation_Fails_NoSnapshot()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, sourceDir.Path);
        var executor = DefaultActionRegistry.Create().Executor;

        var password = "test-pass";
        var exportResult = await ExecuteAsync(executor, "backup-export",
            new BackupExportInput("config", true, null, password), context);
        var exportSummary = Assert.IsType<BackupExportSummary>(exportResult.Output);
        var beforeBackups = Directory.GetFiles(context.BackupDirectory, "*.tar.gz*")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var restoreDest = new TempDir();
        WriteFile(Path.Combine(restoreDest.Path, "openclaw.json"), "{ \"ok\": \"old\" }");

        var restoreResult = await ExecuteAsync(executor, "backup-restore",
            new BackupRestoreInput(exportSummary.ArchivePath, restoreDest.Path, password, "config", SafeReset: true),
            context);
        Assert.False(restoreResult.Success);

        var afterBackups = Directory.GetFiles(context.BackupDirectory, "*.tar.gz*")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(beforeBackups.SetEquals(afterBackups));
        Assert.Equal("{ \"ok\": \"old\" }", File.ReadAllText(Path.Combine(restoreDest.Path, "openclaw.json")));
    }

    [Fact]
    public async Task SnapshotFailure_BlocksRestore()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, sourceDir.Path);
        var executor = DefaultActionRegistry.Create().Executor;

        var password = "test-pass";
        var exportResult = await ExecuteAsync(executor, "backup-export",
            new BackupExportInput("config", true, null, password), context);
        var exportSummary = Assert.IsType<BackupExportSummary>(exportResult.Output);

        var backupDirFile = Path.Combine(contextRoot.Path, "backups-file");
        WriteFile(backupDirFile, "not a directory");
        var badContext = context with { BackupDirectory = backupDirFile };

        using var restoreDest = new TempDir();
        WriteFile(Path.Combine(restoreDest.Path, "openclaw.json"), "{ \"ok\": \"old\" }");

        var restoreResult = await ExecuteAsync(executor, "backup-restore",
            new BackupRestoreInput(exportSummary.ArchivePath, restoreDest.Path, password, "config"),
            badContext);
        Assert.False(restoreResult.Success);
        Assert.Equal("{ \"ok\": \"old\" }", File.ReadAllText(Path.Combine(restoreDest.Path, "openclaw.json")));
    }

    [Fact]
    public async Task UnicodeAndSpacePaths_RoundTrip()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "notes ünicode.txt"), "hello");
        WriteFile(Path.Combine(sourceDir.Path, "folder with space", "file.txt"), "space");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, scope: "full");
            await service.VerifySnapshotAsync(archivePath);

            using var restoreDest = new TempDir();
            await service.RestoreAsync(archivePath, restoreDest.Path, scope: "full");

            Assert.Equal("hello", File.ReadAllText(Path.Combine(restoreDest.Path, "notes ünicode.txt")));
            Assert.Equal("space", File.ReadAllText(Path.Combine(restoreDest.Path, "folder with space", "file.txt")));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task Recover_Preview_ReturnsImpact_And_Diagnostics()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, scope: "full");

            using var contextRoot = new TempDir();
            var context = CreateContext(contextRoot.Path, sourceDir.Path);
            var executor = DefaultActionRegistry.Create().Executor;

            using var restoreDest = new TempDir();

            var result = await ExecuteAsync(executor, "recover",
                new RecoverInput(
                    archivePath,
                    restoreDest.Path,
                    null,
                    "config",
                    Preview: true,
                    SafeReset: false,
                    RunDoctor: false,
                    RunFix: false,
                    ExportDiagnostics: true),
                context);

            Assert.True(result.Success);
            var summary = Assert.IsType<RecoverSummary>(result.Output);
            Assert.NotNull(summary.Restore);
            Assert.False(summary.Restore!.Applied);
            Assert.False(string.IsNullOrWhiteSpace(summary.DiagnosticsBundlePath));
            Assert.True(File.Exists(summary.DiagnosticsBundlePath!));
            Assert.Contains(summary.Steps, step => step.Step == "restore" && step.Status == "preview");
            Assert.Equal("confirm-reset", summary.NextEscalation);
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task Recover_WithoutConfirm_ReturnsPreview_Error()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, scope: "full");

            using var contextRoot = new TempDir();
            var context = CreateContext(contextRoot.Path, sourceDir.Path);
            var executor = DefaultActionRegistry.Create().Executor;

            var result = await ExecuteAsync(executor, "recover",
                new RecoverInput(
                    archivePath,
                    null,
                    null,
                    "config",
                    Preview: false,
                    SafeReset: true,
                    ConfirmReset: false,
                    RunDoctor: false,
                    RunFix: false,
                    ExportDiagnostics: false),
                context);

            Assert.False(result.Success);
            var summary = Assert.IsType<RecoverSummary>(result.Output);
            Assert.NotNull(summary.Restore);
            Assert.False(summary.Restore!.Applied);
            Assert.Contains(summary.Steps, step => step.Step == "restore" && step.Status == "blocked");
            Assert.Equal("confirm-reset", summary.NextEscalation);
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task Recover_Can_Rerun_After_Blocked_Reset()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, scope: "full");

            using var contextRoot = new TempDir();
            var context = CreateContext(contextRoot.Path, sourceDir.Path);
            var executor = DefaultActionRegistry.Create().Executor;

            var blocked = await ExecuteAsync(executor, "recover",
                new RecoverInput(
                    archivePath,
                    null,
                    null,
                    "config",
                    Preview: false,
                    SafeReset: true,
                    ConfirmReset: false,
                    RunDoctor: false,
                    RunFix: false,
                    ExportDiagnostics: false),
                context);

            Assert.False(blocked.Success);

            var rerun = await ExecuteAsync(executor, "recover",
                new RecoverInput(
                    archivePath,
                    null,
                    null,
                    "config",
                    Preview: false,
                    SafeReset: true,
                    ConfirmReset: true,
                    RunDoctor: false,
                    RunFix: false,
                    ExportDiagnostics: false),
                context);

            Assert.True(rerun.Success);
            var summary = Assert.IsType<RecoverSummary>(rerun.Output);
            Assert.True(summary.Applied);
            Assert.Contains(rerun.Warnings ?? Array.Empty<WarningItem>(), w => w.Code == "safe-reset");
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task Recover_SelectsNewestValidBackup_WhenMultipleExist()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, sourceDir.Path);
        var executor = DefaultActionRegistry.Create().Executor;

        var service = new BackupService();
        var validOld = Path.Combine(context.BackupDirectory, "valid-old.tar.gz");
        var validNew = Path.Combine(context.BackupDirectory, "valid-new.tar.gz");
        var invalid = Path.Combine(context.BackupDirectory, "invalid.tar.gz");

        await service.CreateBackupAsync(sourceDir.Path, validOld, scope: "full");
        File.SetLastWriteTimeUtc(validOld, DateTime.UtcNow.AddMinutes(-10));

        await service.CreateBackupAsync(sourceDir.Path, validNew, scope: "full");
        File.SetLastWriteTimeUtc(validNew, DateTime.UtcNow.AddMinutes(-1));

        File.WriteAllText(invalid, "not a tar");
        File.SetLastWriteTimeUtc(invalid, DateTime.UtcNow.AddMinutes(-2));

        using var restoreDest = new TempDir();
        var result = await ExecuteAsync(executor, "recover",
            new RecoverInput(
                null,
                restoreDest.Path,
                null,
                "config",
                Preview: true,
                SafeReset: false,
                RunDoctor: false,
                RunFix: false,
                ExportDiagnostics: false),
            context);

        Assert.True(result.Success);
        var summary = Assert.IsType<RecoverSummary>(result.Output);
        Assert.NotNull(summary.Restore);
        Assert.Equal(validNew, summary.Restore!.ArchivePath);
    }

    [Fact]
    public async Task Recover_UsesExplicitArchive_EvenIfNotNewest()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, sourceDir.Path);
        var executor = DefaultActionRegistry.Create().Executor;

        var service = new BackupService();
        var explicitArchive = Path.Combine(context.BackupDirectory, "explicit.tar.gz");
        var newerArchive = Path.Combine(context.BackupDirectory, "newer.tar.gz");

        await service.CreateBackupAsync(sourceDir.Path, explicitArchive, scope: "full");
        File.SetLastWriteTimeUtc(explicitArchive, DateTime.UtcNow.AddMinutes(-10));

        await service.CreateBackupAsync(sourceDir.Path, newerArchive, scope: "full");
        File.SetLastWriteTimeUtc(newerArchive, DateTime.UtcNow.AddMinutes(-1));

        using var restoreDest = new TempDir();
        var result = await ExecuteAsync(executor, "recover",
            new RecoverInput(
                explicitArchive,
                restoreDest.Path,
                null,
                "config",
                Preview: true,
                SafeReset: false,
                RunDoctor: false,
                RunFix: false,
                ExportDiagnostics: false),
            context);

        Assert.True(result.Success);
        var summary = Assert.IsType<RecoverSummary>(result.Output);
        Assert.NotNull(summary.Restore);
        Assert.Equal(explicitArchive, summary.Restore!.ArchivePath);
    }

    [Fact]
    public async Task Recover_Fails_WhenNoValidBackups()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, sourceDir.Path);
        var executor = DefaultActionRegistry.Create().Executor;

        var invalid = Path.Combine(context.BackupDirectory, "invalid.tar.gz");
        File.WriteAllText(invalid, "not a tar");

        var result = await ExecuteAsync(executor, "recover",
            new RecoverInput(
                null,
                null,
                null,
                "config",
                Preview: true,
                SafeReset: false,
                RunDoctor: false,
                RunFix: false,
                ExportDiagnostics: false),
            context);

        Assert.False(result.Success);
        Assert.Contains("Provide --snapshot", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OperationJournal_Redacts_Passwords()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, sourceDir.Path);
        var executor = DefaultActionRegistry.Create().Executor;

        var result = await ExecuteAsync(executor, "backup-export",
            new BackupExportInput("config", true, null, "super-secret"), context);
        Assert.True(result.Success);

        var journalPath = Path.Combine(context.DataDirectory, "journal.jsonl");
        Assert.True(File.Exists(journalPath));
        var journalContent = File.ReadAllText(journalPath);
        Assert.DoesNotContain("super-secret", journalContent, StringComparison.Ordinal);
        Assert.Contains("Password=***redacted***", journalContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_ReadOnlyDestination_Fails_WithRollbackWarning()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, sourceDir.Path);
        var executor = DefaultActionRegistry.Create().Executor;

        var password = "test-pass";
        var exportResult = await ExecuteAsync(executor, "backup-export",
            new BackupExportInput("config", true, null, password), context);
        var exportSummary = Assert.IsType<BackupExportSummary>(exportResult.Output);

        using var restoreDest = new TempDir();
        var destFile = Path.Combine(restoreDest.Path, "openclaw.json");
        var originalContent = "{ \"ok\": \"old\" }";
        WriteFile(destFile, originalContent);
        File.SetAttributes(destFile, FileAttributes.ReadOnly);

        var restoreResult = await ExecuteAsync(executor, "backup-restore",
            new BackupRestoreInput(exportSummary.ArchivePath, restoreDest.Path, password, "config"),
            context);

        Assert.False(restoreResult.Success);
        var restoreSummary = Assert.IsType<RestoreSummary>(restoreResult.Output);
        Assert.False(string.IsNullOrWhiteSpace(restoreSummary.SnapshotPath));
        Assert.Contains(restoreResult.Warnings ?? Array.Empty<WarningItem>(), w => w.Code == "rollback-available");
        Assert.Equal(originalContent, File.ReadAllText(destFile));
    }

    [Fact]
    public async Task Restore_Creates_Missing_Destination_Directory()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, sourceDir.Path);
        var executor = DefaultActionRegistry.Create().Executor;

        var password = "test-pass";
        var exportResult = await ExecuteAsync(executor, "backup-export",
            new BackupExportInput("config", true, null, password), context);
        var exportSummary = Assert.IsType<BackupExportSummary>(exportResult.Output);

        using var restoreRoot = new TempDir();
        var missingDest = Path.Combine(restoreRoot.Path, "missing", "nested");
        Assert.False(Directory.Exists(missingDest));

        var restoreResult = await ExecuteAsync(executor, "backup-restore",
            new BackupRestoreInput(exportSummary.ArchivePath, missingDest, password, "config"),
            context);

        Assert.True(restoreResult.Success, restoreResult.Error ?? restoreResult.Output?.ToString());
        Assert.True(File.Exists(Path.Combine(missingDest, "openclaw.json")));
    }

    [Fact]
    public async Task Restore_Repeated_Runs_Cleanly()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, sourceDir.Path);
        var executor = DefaultActionRegistry.Create().Executor;

        var password = "test-pass";
        var exportResult = await ExecuteAsync(executor, "backup-export",
            new BackupExportInput("config", true, null, password), context);
        var exportSummary = Assert.IsType<BackupExportSummary>(exportResult.Output);

        using var restoreDest = new TempDir();
        WriteFile(Path.Combine(restoreDest.Path, "openclaw.json"), "{ \"ok\": \"old\" }");

        var restoreResult1 = await ExecuteAsync(executor, "backup-restore",
            new BackupRestoreInput(exportSummary.ArchivePath, restoreDest.Path, password, "config"),
            context);
        Assert.True(restoreResult1.Success, restoreResult1.Error ?? restoreResult1.Output?.ToString());

        var restoreResult2 = await ExecuteAsync(executor, "backup-restore",
            new BackupRestoreInput(exportSummary.ArchivePath, restoreDest.Path, password, "config"),
            context);
        Assert.True(restoreResult2.Success, restoreResult2.Error ?? restoreResult2.Output?.ToString());
    }

    [Fact]
    public async Task Restore_SnapshotFailure_StopsBeforeApply()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        var creator = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");

        try
        {
            await creator.CreateBackupAsync(sourceDir.Path, archivePath, scope: "full");

            using var contextRoot = new TempDir();
            var context = CreateContext(contextRoot.Path, sourceDir.Path);
            var injector = new TestFaultInjector
            {
                FailSnapshot = true
            };
            var faultyService = new BackupService(injector);
            var executor = DefaultActionRegistry.Create(backupService: faultyService).Executor;

            using var restoreDest = new TempDir();
            var original = "{ \"ok\": \"old\" }";
            WriteFile(Path.Combine(restoreDest.Path, "openclaw.json"), original);

            var restoreResult = await ExecuteAsync(executor, "backup-restore",
                new BackupRestoreInput(archivePath, restoreDest.Path, null, "config"),
                context);

            Assert.False(restoreResult.Success);
            var restoreSummary = Assert.IsType<RestoreSummary>(restoreResult.Output);
            Assert.True(string.IsNullOrWhiteSpace(restoreSummary.SnapshotPath));
            Assert.Equal(original, File.ReadAllText(Path.Combine(restoreDest.Path, "openclaw.json")));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task Restore_LockedFile_Fails_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, sourceDir.Path);
        var executor = DefaultActionRegistry.Create().Executor;

        var password = "test-pass";
        var exportResult = await ExecuteAsync(executor, "backup-export",
            new BackupExportInput("config", true, null, password), context);
        var exportSummary = Assert.IsType<BackupExportSummary>(exportResult.Output);

        using var restoreDest = new TempDir();
        var destFile = Path.Combine(restoreDest.Path, "openclaw.json");
        WriteFile(destFile, "{ \"ok\": \"old\" }");

        using var lockStream = new FileStream(destFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var restoreResult = await ExecuteAsync(executor, "backup-restore",
            new BackupRestoreInput(exportSummary.ArchivePath, restoreDest.Path, password, "config"),
            context);

        Assert.False(restoreResult.Success);
    }

    [Fact]
    public async Task DiagnosticsBundle_Redacts_Logs_And_Env()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, sourceDir.Path);

        var logPath = Path.Combine(context.LogsDirectory, "gateway.log");
        WriteFile(logPath, "Bearer supersecret\nTOKEN=abc123\npassword=hello");

        Environment.SetEnvironmentVariable("RECLAW_TEST_TOKEN", "abc123");
        var executor = DefaultActionRegistry.Create().Executor;

        var result = await ExecuteAsync(executor, "diagnostics-export", new DiagnosticsExportInput(), context);
        Assert.True(result.Success);
        var summary = Assert.IsType<DiagnosticsBundleSummary>(result.Output);
        Assert.True(File.Exists(summary.BundlePath));

        using var extracted = new TempDir();
        TarUtils.ExtractTarGzToDirectory(summary.BundlePath, extracted.Path);
        var redactedLog = File.ReadAllText(Path.Combine(extracted.Path, "logs", "logs", "gateway.log"));
        Assert.DoesNotContain("supersecret", redactedLog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", redactedLog, StringComparison.OrdinalIgnoreCase);

        var envText = File.ReadAllText(Path.Combine(extracted.Path, "env.redacted.txt"));
        Assert.DoesNotContain("abc123", envText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Journal_Redacts_Bearer_And_QueryParams()
    {
        using var contextRoot = new TempDir();
        var context = CreateContext(contextRoot.Path, contextRoot.Path);
        var descriptor = new ActionDescriptor(
            "doctor",
            "Doctor",
            "Doctor",
            "tools",
            "🩺",
            ExecutionMode.Internal,
            ActionCapability.None,
            typeof(EmptyInput),
            typeof(EmptyOutput),
            null,
            null,
            false,
            false);

        var result = new ActionResult(false, Error: "Bearer abc123 token=def456");
        ReClaw.App.Journal.OperationJournal.TryAppend(context, descriptor, new EmptyInput(), result, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var journalPath = Path.Combine(context.DataDirectory, "journal.jsonl");
        var journalContent = File.ReadAllText(journalPath);
        Assert.DoesNotContain("abc123", journalContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("def456", journalContent, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ActionResult> ExecuteAsync(ActionExecutor executor, string actionId, object input, ActionContext context)
    {
        return await executor.ExecuteAsync(
            actionId,
            input,
            context,
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);
    }

    private static ActionContext CreateContext(string rootPath, string openClawHome)
    {
        var configDir = Path.Combine(rootPath, "config");
        var dataDir = Path.Combine(rootPath, "data");
        var backupDir = Path.Combine(dataDir, "backups");
        var logsDir = Path.Combine(dataDir, "logs");
        var tempDir = Path.Combine(rootPath, "tmp");
        var fakeOpenClaw = Path.Combine(rootPath, "fake-openclaw.txt");

        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(fakeOpenClaw, "not an executable");

        return new ActionContext(
            configDir,
            dataDir,
            backupDir,
            logsDir,
            tempDir,
            openClawHome,
            fakeOpenClaw,
            null);
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, content);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private sealed class TestFaultInjector : IFileFaultInjector
    {
        public Func<string, bool> FailCopyPredicate { get; init; } = _ => false;
        public bool FailSnapshot { get; init; }
        public Func<string, bool> FailDeletePredicate { get; init; } = _ => false;

        public void BeforeSnapshotCreate(string snapshotPath)
        {
            if (FailSnapshot)
            {
                throw new IOException("Simulated snapshot creation failure.");
            }
        }

        public void BeforeCreateDirectory(string path)
        {
        }

        public void BeforeCopyFile(string sourcePath, string destinationPath)
        {
            if (FailCopyPredicate(destinationPath))
            {
                throw new IOException("Simulated restore write failure.");
            }
        }

        public Stream WrapWriteStream(string destinationPath, Stream inner) => inner;

        public void BeforeDeletePath(string path)
        {
            if (FailDeletePredicate(path))
            {
                throw new IOException("Simulated reset delete failure.");
            }
        }
    }


    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}");

        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }
}
