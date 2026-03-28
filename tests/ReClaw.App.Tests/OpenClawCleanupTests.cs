using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Actions;
using ReClaw.App.Execution;
using ReClaw.Core;
using Xunit;

namespace ReClaw.App.Tests;

public sealed class OpenClawCleanupTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task CleanupPreview_ListsSafeArtifacts()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        var lockDir = Path.Combine(Path.GetTempPath(), "openclaw");
        Directory.CreateDirectory(lockDir);
        var lockPath = Path.Combine(lockDir, $"gateway-{Guid.NewGuid():N}.lock");
        File.WriteAllText(lockPath, "{ \"pid\": 123 }");

        var context = BuildContext(openClawHome, tempDir.FullName);
        var result = await InternalActionDispatcher.ExecuteAsync(
            "openclaw-cleanup-related",
            Guid.NewGuid(),
            context,
            new OpenClawCleanupInput(Apply: false, Confirm: false),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None,
            new BackupService(),
            new ProcessRunner());

        var summary = Assert.IsType<OpenClawCleanupSummary>(result.Output);
        Assert.False(summary.Applied);
        Assert.Contains(summary.Candidates, artifact => artifact.Path == lockPath && artifact.IsSafeToClean);
    }

    [Fact]
    public async Task CleanupConfirm_RemovesOnlySafeArtifacts()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        var lockDir = Path.Combine(Path.GetTempPath(), "openclaw");
        Directory.CreateDirectory(lockDir);
        var lockPath = Path.Combine(lockDir, $"gateway-{Guid.NewGuid():N}.lock");
        File.WriteAllText(lockPath, "{ \"pid\": 123 }");

        var logsDir = Path.Combine(tempDir.FullName, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(logsDir, "gateway.log"), "log");

        var runtimePath = Path.Combine(tempDir.FullName, "openclaw-active.cmd");
        File.WriteAllText(runtimePath, "echo active");
        var configPath = Path.Combine(tempDir.FullName, "config", "openclaw.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "{ }");

        var context = BuildContext(openClawHome, tempDir.FullName, runtimePath);
        var result = await InternalActionDispatcher.ExecuteAsync(
            "openclaw-cleanup-related",
            Guid.NewGuid(),
            context,
            new OpenClawCleanupInput(Apply: true, Confirm: true),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None,
            new BackupService(),
            new ProcessRunner());

        var summary = Assert.IsType<OpenClawCleanupSummary>(result.Output);
        Assert.True(summary.Applied);
        Assert.DoesNotContain(summary.Removed, path => string.Equals(path, logsDir, StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(lockPath));
        Assert.True(Directory.Exists(logsDir));
        Assert.True(File.Exists(runtimePath));
        Assert.True(File.Exists(configPath));
    }

    private static ActionContext BuildContext(string openClawHome, string root, string? executable = null)
    {
        return new ActionContext(
            Path.Combine(root, "config"),
            Path.Combine(root, "data"),
            Path.Combine(root, "backups"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "tmp"),
            openClawHome,
            executable,
            null);
    }
}
