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

public sealed class GatewayRepairServiceTests
{
    [Fact]
    public async Task VersionMismatch_IsDetected()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        var configPath = Path.Combine(openClawHome, "openclaw.json");
        File.WriteAllText(configPath, "{ \"version\": \"2026.3.13\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(1, "Gateway service missing."),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-status",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "status" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.Equal("2026.3.13", summary.Detection.ConfigVersion);
        Assert.Equal("2026.3.9", summary.Detection.RuntimeVersion);
        Assert.NotNull(summary.Inventory);
        Assert.Contains(summary.Inventory!.Warnings, warning => warning.Code == "config-newer-than-runtime");
    }

    [Fact]
    public async Task MissingToken_IsGenerated()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"gateway\": { \"auth\": { } } }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "gateway status" => CommandResult(0, "Gateway active."),
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                _ => CommandResult(0, "ok")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var updated = File.ReadAllText(Path.Combine(openClawHome, "openclaw.json"));
        Assert.Contains("\"token\"", updated, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StaleGatewayLocks_AreRemoved()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        var configPath = Path.Combine(openClawHome, "openclaw.json");
        File.WriteAllText(configPath, "{ }");

        var tmpRoot = Path.Combine(Path.GetTempPath(), "openclaw");
        Directory.CreateDirectory(tmpRoot);
        var lockPath = Path.Combine(tmpRoot, "gateway-test.lock");
        File.WriteAllText(lockPath, $"{{ \"pid\": 999999, \"configPath\": \"{configPath.Replace("\\", "\\\\")}\" }}");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "gateway status" => CommandResult(0, "Gateway active."),
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                _ => CommandResult(0, "ok")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task CompatibleRuntime_IsSelected_Automatically()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_REPAIR_SKIP_SNAPSHOT", "1");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.13\" }");

        var candidates = new[]
        {
            new OpenClawCandidate(new OpenClawCommand("openclaw-old", Array.Empty<string>(), null), "old"),
            new OpenClawCandidate(new OpenClawCommand("openclaw-new", Array.Empty<string>(), null), "new")
        };

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            if (key == "--version" && context.OpenClawExecutable == "openclaw-old")
            {
                return CommandResult(0, "OpenClaw 2026.3.9");
            }
            if (key == "--version" && context.OpenClawExecutable == "openclaw-new")
            {
                return CommandResult(0, "OpenClaw 2026.3.13");
            }
            if (key == "gateway status" && context.OpenClawExecutable == "openclaw-new")
            {
                return CommandResult(0, "Gateway active.");
            }
            return CommandResult(1, "Gateway service missing.");
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => candidates);
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.Contains(summary.Steps, step => step.Step == "select-runtime" && step.Status == "success");
        Assert.Equal("new (openclaw-new)", summary.SelectedRuntime);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task MissingGatewayService_IsRepaired()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var installCalled = false;
        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            if (key == "gateway install" || key == "gateway install --force")
            {
                installCalled = true;
                return CommandResult(0, "Gateway installed.");
            }
            if (key == "gateway uninstall")
            {
                return CommandResult(0, "Gateway uninstalled.");
            }
            if ((key == "gateway start" || key.StartsWith("gateway start")) && installCalled)
            {
                return CommandResult(0, "Gateway started.");
            }
            if ((key == "gateway status" || key == "gateway status --require-rpc") && installCalled)
            {
                return CommandResult(0, "Gateway active.");
            }
            if (key == "--version")
            {
                return CommandResult(0, "OpenClaw 2026.3.9");
            }
            if (key.StartsWith("doctor"))
            {
                return CommandResult(0, "Doctor ok.");
            }
            return CommandResult(1, "Gateway service missing.");
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.Contains(summary.Steps, step => (step.Step == "gateway-install" || step.Step == "gateway-install-force") && step.Status == "success");
        Assert.True(result.Success);
    }

    [Fact]
    public async Task LogsRequest_WithInactiveGateway_ReturnsGuidance()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "gateway status" => CommandResult(1, "Gateway inactive."),
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-logs",
            Guid.NewGuid(),
            context,
            new[] { "logs", "--follow" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.DoesNotContain(runner.Calls, call => string.Join(' ', call.Args) == "logs --follow");
        Assert.Contains(result.Warnings ?? Array.Empty<WarningItem>(), warning => warning.Code == "gateway-inactive");
    }

    [Fact]
    public async Task RepairStopsAfterSuccess()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var doctorCalled = false;
        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            if (key == "doctor --non-interactive --yes")
            {
                doctorCalled = true;
                return CommandResult(0, "Doctor ok.");
            }
            if (key == "gateway start" && doctorCalled)
            {
                return CommandResult(0, "Gateway started.");
            }
            if (key == "gateway status" && doctorCalled)
            {
                return CommandResult(0, "Gateway active.");
            }
            if (key == "--version")
            {
                return CommandResult(0, "OpenClaw 2026.3.9");
            }
            return CommandResult(1, "Gateway service missing.");
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.DoesNotContain(summary.Steps, step => step.Step == "doctor-fix");
    }

    [Fact]
    public async Task DestructiveEscalation_RequiresConfirmation()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.13\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(1, "Gateway service missing."),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.False(result.Success);
        var warnings = result.Warnings ?? Array.Empty<WarningItem>();
        Assert.Contains(warnings, warning =>
            warning.Code == "confirmation-required" ||
            warning.Code == "unable-to-repair" ||
            warning.Code == "gateway-unhealthy");
    }

    [Fact]
    public async Task MissingGatewayService_AddsSuggestedInstall()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "gateway start" => CommandResult(1, "Service not installed. Run: openclaw gateway install"),
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                _ => CommandResult(1, "Gateway service missing.")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.Contains("openclaw gateway install", summary.SuggestedActions);
    }

    [Fact]
    public async Task GatewayModeUnset_IsSurfacedWithFix()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "gateway start" => CommandResult(1, "Gateway start blocked: set gateway.mode=local (current: unset) or pass --allow-unconfigured."),
                "gateway status" => CommandResult(1, "Gateway inactive."),
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.Contains(result.Warnings ?? Array.Empty<WarningItem>(), warning => warning.Code == "gateway-mode-unset");
        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.Contains("openclaw config set gateway.mode local", summary.SuggestedActions);
    }

    [Fact]
    public async Task GatewayModeUnset_FromDoctor_TriggersConfigSet()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_ATTEMPTS", "1");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_DELAY_MS", "1");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var doctorCalls = 0;
        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(1, "Runtime: stopped", "RPC probe: failed"),
                "doctor --non-interactive --yes" => ++doctorCalls == 1
                    ? CommandResult(1, "gateway.mode is unset; gateway start will be blocked")
                    : CommandResult(0, "Doctor ok."),
                "config set gateway.mode local" => CommandResult(0, "ok"),
                "gateway uninstall" => CommandResult(0, "Gateway uninstalled."),
                "gateway install --force" => CommandResult(0, "Gateway installed."),
                "gateway start" => CommandResult(0, "Gateway started."),
                "gateway status --require-rpc" => CommandResult(0, "Runtime: running", "RPC probe: ok"),
                _ => CommandResult(0, "ok")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.True(result.Success);
        var calls = runner.Calls.Select(call => string.Join(' ', call.Args)).ToList();
        Assert.Contains("config set gateway.mode local", calls);
        Assert.True(calls.IndexOf("config set gateway.mode local") < calls.IndexOf("gateway uninstall"));
    }

    [Fact]
    public async Task ServiceInstalledNotRunning_CapturesStartupReason()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_ATTEMPTS", "1");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_DELAY_MS", "1");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(1, "Service is loaded but not running (likely exited immediately).", "Runtime: stopped", "RPC probe: failed"),
                "gateway status --deep" => CommandResult(1, "Runtime: stopped", "RPC probe: failed"),
                "logs --follow" => CommandResult(1, "fatal: port already in use"),
                "doctor --non-interactive --yes" => CommandResult(0, "Doctor ok."),
                "gateway uninstall" => CommandResult(0, "Gateway uninstalled."),
                "gateway install --force" => CommandResult(0, "Gateway installed."),
                "gateway start" => CommandResult(0, "Gateway started."),
                "gateway status --require-rpc" => CommandResult(1, "Runtime: stopped", "RPC probe: failed"),
                "gateway --port 18789 --verbose" => CommandResult(1, "fatal: port already in use"),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Warnings ?? Array.Empty<WarningItem>(), warning => warning.Code == "gateway-startup-reason");
        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.Contains(summary.Notes, note => note.Contains("Startup reason", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task SessionStoreMissing_IsRecreated_AndSurfaced()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_ATTEMPTS", "1");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_DELAY_MS", "1");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var doctorCalls = 0;
        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(1, "Runtime: stopped", "RPC probe: failed"),
                "doctor --non-interactive --yes" => ++doctorCalls == 1
                    ? CommandResult(1, "CRITICAL: Session store dir missing (~\\.openclaw\\agents\\main\\sessions).")
                    : CommandResult(0, "Doctor ok."),
                "gateway uninstall" => CommandResult(0, "Gateway uninstalled."),
                "gateway install --force" => CommandResult(0, "Gateway installed."),
                "gateway start" => CommandResult(0, "Gateway started."),
                "gateway status --require-rpc" => CommandResult(0, "Runtime: running", "RPC probe: ok"),
                _ => CommandResult(0, "ok")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.True(result.Success);
        var warnings = result.Warnings ?? Array.Empty<WarningItem>();
        Assert.Contains(warnings, warning =>
            warning.Code == "session-store-missing" ||
            warning.Code == "gateway-startup-reason");
        Assert.True(Directory.Exists(Path.Combine(openClawHome, "agents", "main", "sessions")));
    }

    [Fact]
    public async Task ConfigSetFailure_BlocksUninstallInstallStart()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(1, "gateway.mode is unset; gateway start will be blocked"),
                "config set gateway.mode local" => CommandResult(1, "failed to set"),
                _ => CommandResult(0, "ok")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(runner.Calls, call => string.Join(' ', call.Args) == "gateway uninstall");
        Assert.DoesNotContain(runner.Calls, call => string.Join(' ', call.Args) == "gateway install --force");
        Assert.DoesNotContain(runner.Calls, call => string.Join(' ', call.Args) == "gateway start");
    }

    [Fact]
    public async Task GatewayModeUnset_AutoFixes_AndPollsHealthy()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_ATTEMPTS", "3");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_DELAY_MS", "1");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var pollCalls = 0;
        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(1, "gateway.mode is unset; gateway start will be blocked"),
                "config set gateway.mode local" => CommandResult(0, "ok"),
                "doctor --non-interactive --yes" => CommandResult(0, "Doctor ok."),
                "gateway install --force" => CommandResult(0, "Gateway installed."),
                "gateway start" => CommandResult(0, "Gateway started."),
                "gateway status --require-rpc" => ++pollCalls < 2
                    ? CommandResult(1, "Runtime: stopped", "RPC probe: failed")
                    : CommandResult(0, "Runtime: running", "RPC probe: ok"),
                _ => CommandResult(0, "ok")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(runner.Calls, call => string.Join(' ', call.Args) == "config set gateway.mode local");
        Assert.Contains(runner.Calls, call => string.Join(' ', call.Args) == "doctor --non-interactive --yes");
        Assert.Contains(runner.Calls, call => string.Join(' ', call.Args) == "gateway uninstall");
        Assert.Contains(runner.Calls, call => string.Join(' ', call.Args) == "gateway install --force");
        Assert.Contains(runner.Calls, call => string.Join(' ', call.Args) == "gateway start");
        Assert.Contains(runner.Calls, call => string.Join(' ', call.Args) == "gateway status --require-rpc");
    }

    [Fact]
    public async Task InteractivePromptFromDoctor_BlocksAutoRepair()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_ATTEMPTS", "1");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_DELAY_MS", "1");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(1, "Runtime: stopped", "RPC probe: failed"),
                "doctor --non-interactive --yes" => CommandResult(0, "Start gateway service now?", "Yes / No"),
                _ => CommandResult(0, "ok")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Warnings ?? Array.Empty<WarningItem>(), warning => warning.Code == "interactive-prompt");
        Assert.DoesNotContain(runner.Calls, call => string.Join(' ', call.Args) == "gateway uninstall");
    }

    [Fact]
    public async Task GatewayAutoFix_FallbackRunsForeground_WhenStillUnhealthy()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_ATTEMPTS", "1");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_DELAY_MS", "1");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(1, "Runtime: stopped", "RPC probe: failed"),
                "doctor --non-interactive --yes" => CommandResult(0, "Doctor ok."),
                "gateway uninstall" => CommandResult(0, "Gateway uninstalled."),
                "gateway install --force" => CommandResult(0, "Gateway installed."),
                "gateway start" => CommandResult(0, "Gateway started."),
                "gateway status --require-rpc" => CommandResult(1, "Runtime: stopped", "RPC probe: failed"),
                "gateway --port 18789 --verbose" => CommandResult(1, "fatal: port already in use"),
                "status" => CommandResult(1, "status failed"),
                "gateway status --deep --json" => CommandResult(1, "{ \"runtime\": \"stopped\" }"),
                "logs --follow" => CommandResult(1, "Gateway not reachable"),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(runner.Calls, call => string.Join(' ', call.Args) == "gateway --port 18789 --verbose");
        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.Contains(summary.Notes, note => note.Contains("openclaw gateway --port 18789 --verbose", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GatewayLogs_Blocked_WhenRpcProbeFails()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(1, "Runtime: running", "RPC probe: failed"),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-logs",
            Guid.NewGuid(),
            context,
            new[] { "logs", "--follow" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(runner.Calls, call => string.Join(' ', call.Args) == "logs --follow");
    }

    [Fact]
    public async Task GatewayModeUnset_Failure_AddsDiagnosticsNotes()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_ATTEMPTS", "2");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_WAIT_DELAY_MS", "1");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(1, "gateway.mode is unset; gateway start will be blocked"),
                "config set gateway.mode local" => CommandResult(0, "ok"),
                "doctor --non-interactive --yes" => CommandResult(0, "Doctor ok."),
                "gateway install --force" => CommandResult(0, "Gateway installed."),
                "gateway start" => CommandResult(0, "Gateway started."),
                "gateway status --require-rpc" => CommandResult(1, "Runtime: stopped", "RPC probe: failed"),
                "status" => CommandResult(1, "status failed"),
                "gateway status --deep --json" => CommandResult(1, "{ \"runtime\": \"stopped\" }"),
                "logs --follow" => CommandResult(1, "Gateway not reachable"),
                _ => CommandResult(0, "ok")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.False(result.Success);
        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.Contains(summary.Notes, note => note.Contains("openclaw status", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(summary.Notes, note => note.Contains("openclaw gateway status --deep --json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(summary.Notes, note => note.Contains("openclaw logs --follow", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DoctorWarnings_AddSuggestedFix()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "doctor --non-interactive --yes" => CommandResult(1, "Run \"openclaw doctor --fix\" to apply changes."),
                "gateway status" => CommandResult(1, "Gateway service missing."),
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.Contains("openclaw doctor --fix", summary.SuggestedActions);
    }

    [Fact]
    public async Task GatewayStatus_DoesNotStartOrRepair()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.13\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(1, "Gateway service missing."),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        await service.RunWithRepairAsync(
            "gateway-status",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "status" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.DoesNotContain(runner.Calls, call => string.Join(' ', call.Args).StartsWith("gateway start", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(runner.Calls, call => string.Join(' ', call.Args).StartsWith("doctor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GatewayStop_DoesNotStart()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(0, "Gateway inactive."),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        await service.RunWithRepairAsync(
            "gateway-stop",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "stop" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.DoesNotContain(runner.Calls, call => string.Join(' ', call.Args).StartsWith("gateway start", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(runner.Calls, call => string.Join(' ', call.Args).StartsWith("doctor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GatewayLogs_Inactive_ReturnsStructuredFailure()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                "gateway status" => CommandResult(0, "Gateway inactive."),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-logs",
            Guid.NewGuid(),
            context,
            new[] { "logs", "--follow" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.False(result.Success);
        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.Equal("inactive", summary.Outcome);
        Assert.Contains(result.Warnings ?? Array.Empty<WarningItem>(), warning => warning.Code == "gateway-inactive");
        Assert.DoesNotContain(runner.Calls, call => string.Join(' ', call.Args) == "logs --follow");
    }

    [Fact]
    public async Task PreflightWarnings_DoNotFailStatus()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.13\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(1, "Config was last written by a newer OpenClaw (2026.3.13); current version is 2026.3.9."),
                "gateway status" => CommandResult(1, "Gateway service missing."),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-status",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "status" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Warnings ?? Array.Empty<WarningItem>(), warning => warning.Code == "config-newer-than-runtime");
    }

    [Fact]
    public async Task VersionDetection_UsesGatewayStatusOutput()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "--version" => CommandResult(1, "no version"),
                "gateway status" => CommandResult(1, "Config was last written by a newer OpenClaw (2026.3.13); current version is 2026.3.9."),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-status",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "status" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.Equal("2026.3.9", summary.Detection.RuntimeVersion);
        Assert.Equal("2026.3.13", summary.Detection.ConfigVersion);
    }

    [Fact]
    public async Task EntrypointMismatch_AddsWarningAndNote()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "doctor --non-interactive --yes" => CommandResult(1, "Gateway service entrypoint does not match the current install. (C:\\old\\openclaw\\dist\\index.js -> C:\\new\\openclaw\\dist\\index.js)"),
                "gateway status" => CommandResult(1, "Gateway service missing."),
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.Contains(result.Warnings ?? Array.Empty<WarningItem>(), warning => warning.Code == "gateway-entrypoint-mismatch");
        Assert.Contains(summary.Notes, note => note.Contains("entrypoint mismatch", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("openclaw gateway install", summary.SuggestedActions);
        Assert.NotNull(summary.Inventory);
        Assert.Contains(summary.Inventory!.Services, service => service.IsMismatched);
        Assert.Contains(summary.Inventory.Artifacts, artifact => artifact.Kind == "orphan-service");
    }

    [Fact]
    public async Task ServiceEntrypointMismatch_FromStatus_IsSurfaced()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            return key switch
            {
                "gateway status" => CommandResult(1, "Gateway service entrypoint does not match the current install. (C:\\old\\openclaw\\dist\\index.js -> C:\\new\\openclaw\\dist\\index.js)"),
                "--version" => CommandResult(0, "OpenClaw 2026.3.9"),
                _ => CommandResult(1, "fail")
            };
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-status",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "status" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.NotNull(summary.Inventory);
        Assert.Contains(summary.Inventory!.Services, svc => svc.IsMismatched);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task GatewayStart_RepairsThenStarts()
    {
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var startCalled = false;
        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            if (key == "doctor --non-interactive --yes")
            {
                return CommandResult(0, "Doctor ok.");
            }
            if (key.StartsWith("gateway start", StringComparison.OrdinalIgnoreCase))
            {
                startCalled = true;
                return CommandResult(0, "Gateway started.");
            }
            if (key == "gateway status")
            {
                return CommandResult(1, "Gateway service missing.");
            }
            if (key == "--version")
            {
                return CommandResult(0, "OpenClaw 2026.3.9");
            }
            return CommandResult(1, "fail");
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        var summary = Assert.IsType<GatewayRepairSummary>(result.Output);
        Assert.True(result.Success);
        Assert.True(startCalled);
    }

    [Fact]
    public async Task ServiceStartFailure_UsesDetachedFallback_WhenHealthy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START", "1");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START_SIMULATE", "1");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK", "0");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_SERVICE_NOT_RUNNING_CUTOFF", "1");
        Environment.SetEnvironmentVariable("RECLAW_GATEWAY_REPAIR_SKIP_SNAPSHOT", "1");

        var tempDir = Directory.CreateTempSubdirectory();
        var openClawHome = Path.Combine(tempDir.FullName, "home");
        Directory.CreateDirectory(openClawHome);
        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"version\": \"2026.3.9\" }");

        var requireRpcCalls = 0;
        var runner = new FakeRunner((actionId, args, context) =>
        {
            var key = string.Join(' ', args);
            if (key == "--version")
            {
                return CommandResult(0, "OpenClaw 2026.3.9");
            }
            if (key == "doctor --non-interactive --yes")
            {
                return CommandResult(0, "Doctor ok.");
            }
            if (key == "gateway uninstall" || key == "gateway install --force" || key == "gateway start")
            {
                return CommandResult(0, "ok");
            }
            if (key == "gateway status --require-rpc")
            {
                requireRpcCalls++;
                return requireRpcCalls == 1
                    ? CommandResult(1, "Service: Scheduled Task (registered)", "Runtime: stopped", "RPC probe: failed", "Service is loaded but not running")
                    : CommandResult(0, "Runtime: running", "RPC probe: ok");
            }
            if (key == "gateway status")
            {
                return CommandResult(1, "Service: Scheduled Task (registered)", "Runtime: stopped", "RPC probe: failed");
            }
            return CommandResult(0, "ok");
        });

        var context = BuildContext(openClawHome, tempDir.FullName);
        var service = new GatewayRepairService(runner, new BackupService(), _ => Array.Empty<OpenClawCandidate>());
        var result = await service.RunWithRepairAsync(
            "gateway-start",
            Guid.NewGuid(),
            context,
            new[] { "gateway", "start" },
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(result.Warnings ?? Array.Empty<WarningItem>(), warning => warning.Code == "gateway-detached-fallback");
    }

    private static ActionContext BuildContext(string openClawHome, string backupDirectory)
    {
        return new ActionContext(
            Path.GetTempPath(),
            Path.GetTempPath(),
            backupDirectory,
            Path.GetTempPath(),
            Path.GetTempPath(),
            openClawHome,
            null,
            null);
    }

    private static ActionResult CommandResult(int exitCode, params string[] stdout)
    {
        var summary = new OpenClawCommandSummary(
            "openclaw",
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

    private sealed class FakeRunner : IOpenClawActionRunner
    {
        private readonly Func<string, string[], ActionContext, ActionResult> handler;

        public FakeRunner(Func<string, string[], ActionContext, ActionResult> handler)
        {
            this.handler = handler;
        }

        public List<RunnerCall> Calls { get; } = new();

        public Task<ActionResult> RunAsync(
            string actionId,
            Guid correlationId,
            ActionContext context,
            string[] args,
            IProgress<ActionEvent> events,
            CancellationToken cancellationToken)
        {
            Calls.Add(new RunnerCall(actionId, args.ToArray(), context));
            return Task.FromResult(handler(actionId, args, context));
        }
    }

    private sealed record RunnerCall(string ActionId, string[] Args, ActionContext Context);
}
