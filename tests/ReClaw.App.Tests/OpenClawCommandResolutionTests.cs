using System;
using System.IO;
using System.Runtime.InteropServices;
using ReClaw.App.Actions;
using ReClaw.App.Execution;
using Xunit;

namespace ReClaw.App.Tests;

public sealed class OpenClawCommandResolutionTests
{
    [Fact]
    public void ConfiguredExecutable_UsesProvidedPath()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var exePath = Path.Combine(tempDir.FullName, "openclaw.exe");
        File.WriteAllText(exePath, "stub");

        var context = BuildContext(openClawExecutable: exePath, openClawEntry: null);
        var (_, spec, _) = OpenClawRunner.BuildRunSpec(context, new[] { "gateway", "start" });

        Assert.Equal(exePath, spec.FileName);
        Assert.Equal(new[] { "gateway", "start" }, spec.Arguments);
        Assert.Null(spec.WorkingDirectory);
    }

    [Fact]
    public void PathInstalledOpenClaw_UsesCmdOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var tempDir = Directory.CreateTempSubdirectory();
        var appData = Path.Combine(tempDir.FullName, "AppData");
        var npmDir = Path.Combine(appData, "npm");
        Directory.CreateDirectory(npmDir);
        var cmdPath = Path.Combine(npmDir, "openclaw.cmd");
        File.WriteAllText(cmdPath, "stub");

        using var scope = new EnvScope()
            .Set("RECLAW_OPENCLAW_PATH", null)
            .Set("OPENCLAW_EXE", null)
            .Set("OPENCLAW_ENTRY", null)
            .Set("OPENCLAW_REPO", null)
            .Set("APPDATA", appData)
            .Set("ProgramFiles", tempDir.FullName)
            .Set("ProgramFiles(x86)", tempDir.FullName);

        var context = BuildContext(null, null);
        var (_, spec, _) = OpenClawRunner.BuildRunSpec(context, new[] { "gateway", "status" });

        Assert.Equal(cmdPath, spec.FileName);
        Assert.Equal(new[] { "gateway", "status" }, spec.Arguments);
        Assert.Null(spec.WorkingDirectory);
    }

    [Fact]
    public void LocalRepoFallback_UsesNodeEntryWithWorkingDirectory()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var repoRoot = tempDir.FullName;
        var entryPath = Path.Combine(repoRoot, "openclaw.mjs");
        File.WriteAllText(entryPath, "stub");
        var child = Directory.CreateDirectory(Path.Combine(repoRoot, "subdir"));

        var originalCwd = Environment.CurrentDirectory;
        using var scope = new EnvScope()
            .Set("RECLAW_OPENCLAW_PATH", null)
            .Set("OPENCLAW_EXE", null)
            .Set("OPENCLAW_ENTRY", null)
            .Set("OPENCLAW_REPO", null)
            .Set("APPDATA", null)
            .Set("ProgramFiles", tempDir.FullName)
            .Set("ProgramFiles(x86)", tempDir.FullName);

        try
        {
            Environment.CurrentDirectory = child.FullName;
            var context = BuildContext(null, null);
            var (_, spec, _) = OpenClawRunner.BuildRunSpec(context, new[] { "gateway", "status" });

            var expectedNode = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";
            Assert.Equal(expectedNode, spec.FileName);
            Assert.Equal(new[] { entryPath, "gateway", "status" }, spec.Arguments);
            Assert.Equal(repoRoot, spec.WorkingDirectory);
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
        }
    }

    [Fact]
    public void GatewayStart_DoesNotDuplicateOpenClawToken()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var exePath = Path.Combine(tempDir.FullName, "openclaw.exe");
        File.WriteAllText(exePath, "stub");

        var context = BuildContext(openClawExecutable: exePath, openClawEntry: null);
        var (_, spec, _) = OpenClawRunner.BuildRunSpec(context, new[] { "gateway", "start" });

        Assert.Equal("gateway", spec.Arguments[0]);
        Assert.DoesNotContain("openclaw", spec.Arguments);
    }

    [Fact]
    public async System.Threading.Tasks.Task NonZeroExit_IsReportedAsFailed()
    {
        var registry = new ActionRegistry();
        var validators = new ReClaw.App.Validation.ActionValidatorRegistry();
        var executor = new ActionExecutor(registry, validators);
        var descriptor = new ActionDescriptor(
            "fake-action",
            "Fake",
            "Fake",
            "tools",
            "⚠️",
            ExecutionMode.Internal,
            ActionCapability.None,
            typeof(EmptyInput),
            typeof(EmptyOutput),
            null);

        registry.Register(descriptor, (_, _, _, _, _, _) =>
            System.Threading.Tasks.Task.FromResult(new ActionResult(false, Error: "fail", ExitCode: 2)));

        var events = new System.Collections.Generic.List<ActionEvent>();
        var sink = new Progress<ActionEvent>(events.Add);
        var context = BuildContext(null, null);

        var result = await executor.ExecuteAsync("fake-action", new EmptyInput(), context, sink, System.Threading.CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(events, e => e is ActionFailed);
        Assert.DoesNotContain(events, e => e is ActionCompleted);
    }

    [Fact]
    public void AnsiOutput_IsStripped_ForUiLogs()
    {
        var raw = "\u001b[31mERROR\u001b[0m Something broke";
        var sanitized = ProcessRunner.SanitizeOutput(raw);
        Assert.Equal("ERROR Something broke", sanitized);
    }

    private static ActionContext BuildContext(string? openClawExecutable, string? openClawEntry)
    {
        return new ActionContext(
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            openClawExecutable,
            openClawEntry);
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly System.Collections.Generic.Dictionary<string, string?> originals = new(StringComparer.OrdinalIgnoreCase);

        public EnvScope Set(string name, string? value)
        {
            if (!originals.ContainsKey(name))
            {
                originals[name] = Environment.GetEnvironmentVariable(name);
            }
            Environment.SetEnvironmentVariable(name, value);
            return this;
        }

        public void Dispose()
        {
            foreach (var (key, value) in originals)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
