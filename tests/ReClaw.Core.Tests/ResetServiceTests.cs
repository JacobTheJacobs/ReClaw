using System;
using System.IO;
using System.Linq;
using ReClaw.Core;
using Xunit;

namespace ReClaw.Core.Tests;

public sealed class ResetServiceTests
{
    [Fact]
    public void ResetPlan_PreserveBackups_KeepsBackupDirectory()
    {
        using var temp = new TempDir();
        var config = Path.Combine(temp.Path, "config");
        var data = Path.Combine(temp.Path, "data");
        var backups = Path.Combine(data, "backups");
        var logs = Path.Combine(data, "logs");
        var openClaw = Path.Combine(temp.Path, "openclaw");

        Directory.CreateDirectory(config);
        Directory.CreateDirectory(backups);
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(openClaw);

        var service = new ResetService();
        var plan = service.BuildPlan(new ResetContext(openClaw, config, data, backups), ResetMode.PreserveBackups);

        Assert.Contains(Normalize(backups), plan.PreservePaths.Select(Normalize));
        Assert.DoesNotContain(Normalize(backups), plan.DeletePaths.Select(Normalize));
        Assert.Contains(Normalize(config), plan.DeletePaths.Select(Normalize));
        Assert.Contains(Normalize(openClaw), plan.DeletePaths.Select(Normalize));
        Assert.Contains(Normalize(logs), plan.DeletePaths.Select(Normalize));
    }

    [Fact]
    public void ResetPlan_PreserveConfig_DeletesData()
    {
        using var temp = new TempDir();
        var config = Path.Combine(temp.Path, "config");
        var data = Path.Combine(temp.Path, "data");
        var backups = Path.Combine(data, "backups");
        var openClaw = Path.Combine(temp.Path, "openclaw");

        Directory.CreateDirectory(config);
        Directory.CreateDirectory(backups);
        Directory.CreateDirectory(openClaw);

        var service = new ResetService();
        var plan = service.BuildPlan(new ResetContext(openClaw, config, data, backups), ResetMode.PreserveConfig);

        Assert.Contains(Normalize(config), plan.PreservePaths.Select(Normalize));
        Assert.Contains(Normalize(data), plan.DeletePaths.Select(Normalize));
        Assert.Contains(Normalize(openClaw), plan.DeletePaths.Select(Normalize));
    }

    [Fact]
    public void ResetPlan_FullReset_DeletesAllRoots()
    {
        using var temp = new TempDir();
        var config = Path.Combine(temp.Path, "config");
        var data = Path.Combine(temp.Path, "data");
        var backups = Path.Combine(data, "backups");
        var openClaw = Path.Combine(temp.Path, "openclaw");

        Directory.CreateDirectory(config);
        Directory.CreateDirectory(backups);
        Directory.CreateDirectory(openClaw);

        var service = new ResetService();
        var plan = service.BuildPlan(new ResetContext(openClaw, config, data, backups), ResetMode.FullLocalReset);

        Assert.Contains(Normalize(config), plan.DeletePaths.Select(Normalize));
        Assert.Contains(Normalize(data), plan.DeletePaths.Select(Normalize));
        Assert.Contains(Normalize(openClaw), plan.DeletePaths.Select(Normalize));
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

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
