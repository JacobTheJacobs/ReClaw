using System;
using System.IO;
using ReClaw.App.Actions;
using ReClaw.App.Execution;
using Xunit;

namespace ReClaw.App.Tests;

public sealed class OpenClawInventoryTests
{
    [Fact]
    public void Inventory_ReturnsActiveRuntimeAndAlternates()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);

        var exeA = Path.Combine(tempDir.FullName, "openclaw-a.cmd");
        var exeB = Path.Combine(tempDir.FullName, "openclaw-b.cmd");
        File.WriteAllText(exeA, "echo a");
        File.WriteAllText(exeB, "echo b");

        var context = BuildContext(openClawHome, tempDir.FullName, exeA);
        var candidates = new[]
        {
            new OpenClawCandidate(new OpenClawCommand(exeA, Array.Empty<string>(), null), "configured-executable"),
            new OpenClawCandidate(new OpenClawCommand(exeB, Array.Empty<string>(), null), "path")
        };

        var inventory = OpenClawLocator.BuildInventory(
            context,
            runtimeVersion: "2026.3.9",
            configVersion: "2026.3.9",
            resolved: new OpenClawCommand(exeA, Array.Empty<string>(), null),
            candidates: candidates);

        Assert.NotNull(inventory.ActiveRuntime);
        Assert.True(inventory.ActiveRuntime!.IsSelected);
        Assert.Contains(inventory.CandidateRuntimes, runtime => string.Equals(runtime.ExecutablePath, exeB, StringComparison.OrdinalIgnoreCase));
    }

    private static ActionContext BuildContext(string openClawHome, string root, string executable)
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
