using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Actions;
using ReClaw.App.Execution;
using ReClaw.Core;
using Xunit;

namespace ReClaw.App.Tests;

public sealed class OpenClawRebuildServiceTests
{
    [Fact]
    public async Task Rebuild_CreatesVerifiedBackup_First()
    {
        using var _ = FastGatewayWait();
        var context = BuildContext("2026.3.9");
        var runner = new FakeRunner((actionId, args, _) =>
        {
            var key = string.Join(' ', args);
            if (key.StartsWith("backup create", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult(args, 1, "backup failed");
            }
            if (key == "dashboard --no-open")
            {
                return CommandResult(args, 0, "Dashboard: http://127.0.0.1:18789");
            }
            if (key == "gateway status")
            {
                return CommandResult(args, 0, "Gateway status: running");
            }
            if (key == "status --deep")
            {
                return CommandResult(args, 0, "http://127.0.0.1:18789");
            }
            return CommandResult(args, 0, "ok");
        });
        var processRunner = new FakeProcessRunner(_ => ProcessResult(0, false, "logs ok"));
        var service = new OpenClawRebuildService(runner, new BackupService(), processRunner, _ => Array.Empty<OpenClawCandidate>());

        var result = await service.RebuildAsync(
            "openclaw-rebuild",
            Guid.NewGuid(),
            context,
            new OpenClawRebuildInput(ConfirmDestructive: true),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<OpenClawRebuildSummary>(result.Output);
        var backupIndex = IndexOfStep(summary.Steps, "backup");
        var resetIndex = IndexOfStep(summary.Steps, "reset");
        var updateIndex = IndexOfStep(summary.Steps, "runtime-update");
        Assert.True(backupIndex >= 0);
        Assert.True(resetIndex < 0 || backupIndex < resetIndex);
        Assert.True(updateIndex < 0 || backupIndex < updateIndex);
        Assert.True(File.Exists(summary.BackupPath));
    }

    [Fact]
    public async Task Rebuild_Preserves_Selected_Scopes_Only()
    {
        using var _ = FastGatewayWait();
        var context = BuildContext("2026.3.9");
        var runner = new FakeRunner((_, args, _) =>
        {
            var key = string.Join(' ', args);
            if (key == "dashboard --no-open")
            {
                return CommandResult(args, 0, "Dashboard: http://127.0.0.1:18789");
            }
            if (key == "gateway status")
            {
                return CommandResult(args, 0, "Gateway status: running");
            }
            if (key == "status --deep")
            {
                return CommandResult(args, 0, "http://127.0.0.1:18789");
            }
            return CommandResult(args, 0, "ok");
        });
        var processRunner = new FakeProcessRunner(_ => ProcessResult(0, false, "logs ok"));
        var service = new OpenClawRebuildService(runner, new BackupService(), processRunner, _ => Array.Empty<OpenClawCandidate>());

        var input = new OpenClawRebuildInput(
            PreserveConfig: true,
            PreserveCredentials: false,
            PreserveSessions: true,
            PreserveWorkspace: false,
            ConfirmDestructive: true);

        var result = await service.RebuildAsync(
            "openclaw-rebuild",
            Guid.NewGuid(),
            context,
            input,
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<OpenClawRebuildSummary>(result.Output);
        Assert.Equal("config+sessions", summary.Scope);
        Assert.Equal(new[] { "config", "sessions" }, summary.PreserveScopes);
    }

    [Fact]
    public async Task Rebuild_Handles_Missing_Service()
    {
        using var _ = FastGatewayWait();
        var context = BuildContext("2026.3.9");
        var runner = new FakeRunner((actionId, args, _) =>
        {
            var key = string.Join(' ', args);
            if (actionId == "gateway-status" && key == "gateway status")
            {
                return CommandResult(args, 0, "Gateway service missing.");
            }
            if (key == "dashboard --no-open")
            {
                return CommandResult(args, 0, "Dashboard: http://127.0.0.1:18789");
            }
            if (key == "gateway status")
            {
                return CommandResult(args, 0, "Gateway status: running");
            }
            if (key == "status --deep")
            {
                return CommandResult(args, 0, "http://127.0.0.1:18789");
            }
            return CommandResult(args, 0, "ok");
        });
        var processRunner = new FakeProcessRunner(_ => ProcessResult(0, false, "logs ok"));
        var service = new OpenClawRebuildService(runner, new BackupService(), processRunner, _ => Array.Empty<OpenClawCandidate>());

        var result = await service.RebuildAsync(
            "openclaw-rebuild",
            Guid.NewGuid(),
            context,
            new OpenClawRebuildInput(ConfirmDestructive: true),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<OpenClawRebuildSummary>(result.Output);
        Assert.Contains(summary.Steps, step => step.StepId == "gateway-uninstall" && step.Status == "skipped");
    }

    [Fact]
    public async Task Rebuild_Handles_Runtime_Config_Mismatch()
    {
        using var _ = FastGatewayWait();
        var context = BuildContext("2026.3.13");
        var runner = new FakeRunner((_, args, _) =>
        {
            var key = string.Join(' ', args);
            if (key == "--version")
            {
                return CommandResult(args, 0, "OpenClaw 2026.3.9");
            }
            if (key == "dashboard --no-open")
            {
                return CommandResult(args, 0, "Dashboard: http://127.0.0.1:18789");
            }
            if (key == "gateway status")
            {
                return CommandResult(args, 0, "Gateway status: running");
            }
            if (key == "status --deep")
            {
                return CommandResult(args, 0, "http://127.0.0.1:18789");
            }
            return CommandResult(args, 0, "ok");
        });
        var processRunner = new FakeProcessRunner(_ => ProcessResult(0, false, "logs ok"));
        var service = new OpenClawRebuildService(runner, new BackupService(), processRunner, _ => Array.Empty<OpenClawCandidate>());

        var result = await service.RebuildAsync(
            "openclaw-rebuild",
            Guid.NewGuid(),
            context,
            new OpenClawRebuildInput(ConfirmDestructive: true),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.Contains(result.Warnings ?? Array.Empty<WarningItem>(), warning => warning.Code == "config-newer-than-runtime");
    }

    [Fact]
    public async Task Rebuild_Reports_Reinstall_Remove_Steps()
    {
        using var _ = FastGatewayWait();
        var context = BuildContext("2026.3.9");
        var runner = new FakeRunner((actionId, args, _) =>
        {
            var key = string.Join(' ', args);
            if (actionId == "gateway-status" && key == "gateway status")
            {
                return CommandResult(args, 0, "Gateway service entrypoint does not match the current install. (C:\\\\old\\\\openclaw -> C:\\\\new\\\\openclaw)");
            }
            if (key == "dashboard --no-open")
            {
                return CommandResult(args, 0, "Dashboard: http://127.0.0.1:18789");
            }
            if (key == "gateway status")
            {
                return CommandResult(args, 0, "Gateway status: running");
            }
            if (key == "status --deep")
            {
                return CommandResult(args, 0, "http://127.0.0.1:18789");
            }
            return CommandResult(args, 0, "ok");
        });
        var processRunner = new FakeProcessRunner(_ => ProcessResult(0, false, "logs ok"));
        var service = new OpenClawRebuildService(runner, new BackupService(), processRunner, _ => Array.Empty<OpenClawCandidate>());

        var result = await service.RebuildAsync(
            "openclaw-rebuild",
            Guid.NewGuid(),
            context,
            new OpenClawRebuildInput(ConfirmDestructive: true),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<OpenClawRebuildSummary>(result.Output);
        Assert.Contains("gateway-service", summary.RemovedItems);
        Assert.Contains("gateway-service", summary.InstalledItems);
    }

    [Fact]
    public async Task Rebuild_Verifies_Gateway_Status_After_Completion()
    {
        using var _ = FastGatewayWait();
        var context = BuildContext("2026.3.9");
        var runner = new FakeRunner((_, args, _) =>
        {
            var key = string.Join(' ', args);
            if (key == "dashboard --no-open")
            {
                return CommandResult(args, 0, "Dashboard: http://127.0.0.1:18789");
            }
            if (key == "gateway status")
            {
                return CommandResult(args, 0, "Gateway status: running");
            }
            if (key == "status --deep")
            {
                return CommandResult(args, 0, "http://127.0.0.1:18789");
            }
            return CommandResult(args, 0, "ok");
        });
        var processRunner = new FakeProcessRunner(_ => ProcessResult(0, false, "logs ok"));
        var service = new OpenClawRebuildService(runner, new BackupService(), processRunner, _ => Array.Empty<OpenClawCandidate>());

        var result = await service.RebuildAsync(
            "openclaw-rebuild",
            Guid.NewGuid(),
            context,
            new OpenClawRebuildInput(ConfirmDestructive: true),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<OpenClawRebuildSummary>(result.Output);
        Assert.True(result.Success);
        Assert.True(summary.Verification.GatewayHealthy);
        Assert.True(summary.Verification.LogsAvailable);
        Assert.True(summary.Verification.BrowserReady);
        Assert.NotNull(summary.Verification.GatewayStatus);
        var restoreIndex = IndexOfStep(summary.Steps, "restore");
        var verifyIndex = IndexOfStep(summary.Steps, "verify-gateway-status");
        Assert.True(restoreIndex >= 0 && verifyIndex > restoreIndex);
    }

    [Fact]
    public async Task Rebuild_Fails_Safely_When_Install_Step_Fails()
    {
        using var _ = FastGatewayWait();
        var context = BuildContext("2026.3.9");
        var runner = new FakeRunner((_, args, _) =>
        {
            var key = string.Join(' ', args);
            if (key == "update")
            {
                return CommandResult(args, 1, "update failed");
            }
            if (key == "dashboard --no-open")
            {
                return CommandResult(args, 0, "Dashboard: http://127.0.0.1:18789");
            }
            if (key == "gateway status")
            {
                return CommandResult(args, 0, "Gateway status: running");
            }
            if (key == "status --deep")
            {
                return CommandResult(args, 0, "http://127.0.0.1:18789");
            }
            return CommandResult(args, 0, "ok");
        });
        var processRunner = new FakeProcessRunner(_ => ProcessResult(0, false, "logs ok"));
        var service = new OpenClawRebuildService(runner, new BackupService(), processRunner, _ => Array.Empty<OpenClawCandidate>());

        var result = await service.RebuildAsync(
            "openclaw-rebuild",
            Guid.NewGuid(),
            context,
            new OpenClawRebuildInput(ConfirmDestructive: true),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<OpenClawRebuildSummary>(result.Output);
        Assert.False(result.Success);
        Assert.DoesNotContain(summary.Steps, step => step.StepId == "doctor");
    }

    [Fact]
    public async Task Rebuild_Returns_Backup_Metadata()
    {
        using var _ = FastGatewayWait();
        var context = BuildContext("2026.3.9");
        var runner = new FakeRunner((_, args, _) =>
        {
            var key = string.Join(' ', args);
            if (key == "dashboard --no-open")
            {
                return CommandResult(args, 0, "Dashboard: http://127.0.0.1:18789");
            }
            if (key == "gateway status")
            {
                return CommandResult(args, 0, "Gateway status: running");
            }
            if (key == "status --deep")
            {
                return CommandResult(args, 0, "http://127.0.0.1:18789");
            }
            return CommandResult(args, 0, "ok");
        });
        var processRunner = new FakeProcessRunner(_ => ProcessResult(0, false, "logs ok"));
        var service = new OpenClawRebuildService(runner, new BackupService(), processRunner, _ => Array.Empty<OpenClawCandidate>());

        var result = await service.RebuildAsync(
            "openclaw-rebuild",
            Guid.NewGuid(),
            context,
            new OpenClawRebuildInput(ConfirmDestructive: true),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<OpenClawRebuildSummary>(result.Output);
        Assert.False(string.IsNullOrWhiteSpace(summary.BackupPath));
        Assert.True(File.Exists(summary.BackupPath));
    }

    [Fact]
    public async Task Rebuild_Fails_When_Gateway_Not_Running_After_Rebuild()
    {
        using var _ = FastGatewayWait();
        var statusCalls = 0;
        var context = BuildContext("2026.3.9");
        var runner = new FakeRunner((_, args, _) =>
        {
            var key = string.Join(' ', args);
            if (key == "gateway status")
            {
                statusCalls++;
                return CommandResult(args, 0, "Service is loaded but not running.");
            }
            if (key == "dashboard --no-open")
            {
                return CommandResult(args, 0, "Dashboard: http://127.0.0.1:18789");
            }
            if (key == "status --deep")
            {
                return CommandResult(args, 1, "status failed");
            }
            return CommandResult(args, 0, "ok");
        });
        var processRunner = new FakeProcessRunner(spec =>
        {
            if (spec.Arguments.Contains("--follow"))
            {
                return ProcessResult(1, false, "logs unavailable");
            }
            return ProcessResult(0, false, "service stopped");
        });
        var service = new OpenClawRebuildService(runner, new BackupService(), processRunner, _ => Array.Empty<OpenClawCandidate>());

        var result = await service.RebuildAsync(
            "openclaw-rebuild",
            Guid.NewGuid(),
            context,
            new OpenClawRebuildInput(ConfirmDestructive: true),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<OpenClawRebuildSummary>(result.Output);
        Assert.False(result.Success);
        Assert.True(statusCalls >= 2);
        Assert.Contains(summary.Steps, step => step.StepId == "gateway-start-retry");
        Assert.NotNull(summary.Verification.GatewayDiagnostics);
        Assert.NotNull(summary.Verification.GatewayDiagnostics?.ServiceTaskStatus);
        Assert.Contains(summary.Verification.VerificationFailures ?? Array.Empty<string>(), failure => failure.Contains("Gateway", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Rebuild_Windows_AutoRemediation_Uses_ForceInstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var _ = FastGatewayWait();
        var commands = new List<string>();
        var context = BuildContext("2026.3.9");
        var runner = new FakeRunner((_, args, _) =>
        {
            var key = string.Join(' ', args);
            commands.Add(key);
            if (key == "gateway status")
            {
                return CommandResult(args, 0, "Service is loaded but not running.");
            }
            if (key == "dashboard --no-open")
            {
                return CommandResult(args, 0, "Dashboard: http://127.0.0.1:18789");
            }
            if (key == "status --deep")
            {
                return CommandResult(args, 1, "status failed");
            }
            return CommandResult(args, 0, "ok");
        });
        var processSpecs = new List<ProcessRunSpec>();
        var processRunner = new FakeProcessRunner(spec =>
        {
            processSpecs.Add(spec);
            return ProcessResult(0, false, "ok");
        });
        var service = new OpenClawRebuildService(runner, new BackupService(), processRunner, _ => Array.Empty<OpenClawCandidate>());

        var result = await service.RebuildAsync(
            "openclaw-rebuild",
            Guid.NewGuid(),
            context,
            new OpenClawRebuildInput(ConfirmDestructive: true),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<OpenClawRebuildSummary>(result.Output);
        Assert.Contains(summary.Steps, step => step.StepId == "gateway-autostart-disable");
        Assert.Contains(summary.Steps, step => step.StepId == "gateway-install-force");
        Assert.Contains(commands, cmd => cmd == "gateway install --force");
        Assert.Contains(processSpecs, spec => string.Equals(spec.FileName, "schtasks", StringComparison.OrdinalIgnoreCase));
    }

    private static ActionContext BuildContext(string configVersion)
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var root = tempDir.FullName;
        var openClawHome = Path.Combine(root, "home");
        var configDir = Path.Combine(root, "config");
        var dataDir = Path.Combine(root, "data");
        var backupDir = Path.Combine(root, "backups");
        var logsDir = Path.Combine(root, "logs");
        var temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(openClawHome);
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(temp);

        var configPath = Path.Combine(openClawHome, "openclaw.json");
        File.WriteAllText(configPath, $"{{ \"version\": \"{configVersion}\" }}");
        File.WriteAllText(Path.Combine(openClawHome, "workspace.txt"), "data");

        var exePath = Path.Combine(root, "openclaw.exe");
        File.WriteAllText(exePath, "stub");

        return new ActionContext(
            configDir,
            dataDir,
            backupDir,
            logsDir,
            temp,
            openClawHome,
            exePath,
            null);
    }

    private static EnvVarScope FastGatewayWait()
    {
        return new EnvVarScope(
            ("RECLAW_GATEWAY_WAIT_ATTEMPTS", "1"),
            ("RECLAW_GATEWAY_WAIT_DELAY_MS", "1"),
            ("RECLAW_GATEWAY_HEALTH_TIMEOUT_MS", "10"),
            ("RECLAW_GATEWAY_DETACHED_START", "0"));
    }

    private static ActionResult CommandResult(string[] args, int exitCode, params string[] stdout)
    {
        var summary = new OpenClawCommandSummary(
            $"openclaw {string.Join(' ', args)}",
            exitCode,
            false,
            stdout,
            Array.Empty<string>(),
            stdout.Length,
            0,
            false);

        return exitCode == 0
            ? new ActionResult(true, Output: summary, ExitCode: exitCode)
            : new ActionResult(false, Output: summary, Error: "failed", ExitCode: exitCode);
    }

    private static ProcessResult ProcessResult(int exitCode, bool timedOut, params string[] stdout)
    {
        return new ProcessResult(exitCode, timedOut, stdout, Array.Empty<string>(), stdout.Length, 0, false);
    }

    private static int IndexOfStep(IReadOnlyList<RebuildStep> steps, string stepId)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (string.Equals(steps[i].StepId, stepId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private sealed class EnvVarScope : IDisposable
    {
        private readonly Dictionary<string, string?> original = new(StringComparer.OrdinalIgnoreCase);

        public EnvVarScope(params (string Key, string Value)[] entries)
        {
            foreach (var (key, value) in entries)
            {
                original[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in original)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private sealed class FakeRunner : IOpenClawActionRunner
    {
        private readonly Func<string, string[], ActionContext, ActionResult> handler;

        public FakeRunner(Func<string, string[], ActionContext, ActionResult> handler)
        {
            this.handler = handler;
        }

        public Task<ActionResult> RunAsync(
            string actionId,
            Guid correlationId,
            ActionContext context,
            string[] args,
            IProgress<ActionEvent> events,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(actionId, args, context));
        }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Func<ProcessRunSpec, ProcessResult> handler;

        public FakeProcessRunner(Func<ProcessRunSpec, ProcessResult> handler)
        {
            this.handler = handler;
        }

        public Task<ProcessResult> RunAsync(
            string actionId,
            Guid correlationId,
            ProcessRunSpec spec,
            IProgress<ActionEvent> events,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(spec));
        }
    }
}
