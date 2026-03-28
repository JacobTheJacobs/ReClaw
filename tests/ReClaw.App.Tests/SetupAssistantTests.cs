using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Actions;
using ReClaw.App.Execution;
using ReClaw.Core;
using Xunit;

namespace ReClaw.App.Tests;

public sealed class SetupAssistantTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task SetupInstall_RunsRepairLadderAndFallback()
    {
        using var server = new HealthServer(failFirstProbe: OperatingSystem.IsWindows());
        var tempRoot = Directory.CreateTempSubdirectory();
        var binDir = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "bin")).FullName;
        var scriptsRoot = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "legacy")).FullName;
        var repoRoot = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "repo")).FullName;
        var openClawHome = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "home")).FullName;

        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"gateway\": { \"auth\": { \"token\": \"test-token\" } } }");
        File.WriteAllText(Path.Combine(scriptsRoot, "install-openclaw-cli.js"), "// stub");

        var isWindows = OperatingSystem.IsWindows();
        var openClawPath = Path.Combine(binDir, isWindows ? "openclaw.cmd" : "openclaw");
        var templatePath = Path.Combine(binDir, isWindows ? "openclaw.template.cmd" : "openclaw.template");
        var statePath = Path.Combine(tempRoot.FullName, "openclaw.state");

        WriteOpenClawTemplate(templatePath);
        WriteNodeStub(binDir);

        Assert.False(File.Exists(openClawPath));

        var originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        using var scope = new EnvScope()
            .Set("RECLAW_DISABLE_WSL", "1")
            .Set("RECLAW_GATEWAY_DETACHED_START_SIMULATE", "1")
            .Set("OPENCLAW_GATEWAY_PORT", server.Port.ToString())
            .Set("OPENCLAW_REPO", repoRoot)
            .Set("RECLAW_LEGACY_SCRIPTS", scriptsRoot)
            .Set("RECLAW_OPENCLAW_PATH", openClawPath)
            .Set("RECLAW_OPENCLAW_TEMPLATE", templatePath)
            .Set("RECLAW_OPENCLAW_STATE", statePath)
            .Set("OPENCLAW_ENTRY", null)
            .Set("OPENCLAW_EXE", null)
            .Set("PATH", $"{binDir}{Path.PathSeparator}{originalPath}");

        var context = new ActionContext(
            tempRoot.FullName,
            tempRoot.FullName,
            tempRoot.FullName,
            tempRoot.FullName,
            tempRoot.FullName,
            openClawHome,
            null,
            null);

        var result = await InternalActionDispatcher.ExecuteAsync(
            "setup-install",
            Guid.NewGuid(),
            context,
            new EmptyInput(),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None,
            new BackupService(),
            new ProcessRunner());

        var summary = Assert.IsType<SetupAssistantSummary>(result.Output);
        Assert.True(summary.RepairAttempted);
        Assert.True(summary.GatewayHealthy);
        Assert.Equal("Safe", summary.Security.Status);
        Assert.Contains(summary.Steps, step => step.Step == "install-openclaw" && step.Status == "success");
        Assert.Contains(summary.Steps, step => step.Step == "gateway-health" && step.Status == "failed");
        Assert.Contains(summary.Steps, step => step.Step == "repair-gateway-install");
        Assert.Contains(summary.Steps, step => step.Step == "repair-gateway-start");
        Assert.Contains(summary.Steps, step => step.Step == "repair-health-probe");

        if (OperatingSystem.IsWindows())
        {
            Assert.True(summary.DetachedFallback);
            Assert.Contains(summary.Steps, step => step.Step == "repair-detached-fallback");
        }
    }

    private static void WriteNodeStub(string binDir)
    {
        if (OperatingSystem.IsWindows())
        {
            var nodePath = Path.Combine(binDir, "node.cmd");
            var script = "@echo off\r\nsetlocal\r\n" +
                         "if \"%RECLAW_OPENCLAW_TEMPLATE%\"==\"\" exit /b 2\r\n" +
                         "if \"%RECLAW_OPENCLAW_PATH%\"==\"\" exit /b 3\r\n" +
                         "copy /Y \"%RECLAW_OPENCLAW_TEMPLATE%\" \"%RECLAW_OPENCLAW_PATH%\" >nul\r\n" +
                         "exit /b 0\r\n";
            File.WriteAllText(nodePath, script, Encoding.ASCII);
        }
        else
        {
            var nodePath = Path.Combine(binDir, "node");
            var script = "#!/usr/bin/env bash\n" +
                         "set -euo pipefail\n" +
                         "cp \"${RECLAW_OPENCLAW_TEMPLATE}\" \"${RECLAW_OPENCLAW_PATH}\"\n" +
                         "chmod +x \"${RECLAW_OPENCLAW_PATH}\"\n";
            File.WriteAllText(nodePath, script, Encoding.ASCII);
            File.SetUnixFileMode(nodePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static void WriteOpenClawTemplate(string templatePath)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = "@echo off\r\nsetlocal\r\n" +
                         "set \"state=%RECLAW_OPENCLAW_STATE%\"\r\n" +
                         "set \"port=%OPENCLAW_GATEWAY_PORT%\"\r\n" +
                         "if \"%port%\"==\"\" set \"port=18789\"\r\n" +
                         "if /I \"%1\"==\"gateway\" if /I \"%2\"==\"status\" if /I \"%3\"==\"--require-rpc\" (\r\n" +
                         "  if not \"%state%\"==\"\" (\r\n" +
                         "    if exist \"%state%\" (\r\n" +
                         "      echo Runtime: running\r\n" +
                         "      echo RPC probe: ok\r\n" +
                         "      echo port %port%\r\n" +
                         "      exit /b 0\r\n" +
                         "    ) else (\r\n" +
                         "      echo Runtime: stopped\r\n" +
                         "      echo RPC probe: failed\r\n" +
                         "      echo port %port%\r\n" +
                         "      echo.>\"%state%\"\r\n" +
                         "      exit /b 0\r\n" +
                         "    )\r\n" +
                         "  ) else (\r\n" +
                         "    echo Runtime: stopped\r\n" +
                         "    echo RPC probe: failed\r\n" +
                         "    echo port %port%\r\n" +
                         "    exit /b 0\r\n" +
                         "  )\r\n" +
                         "  exit /b 0\r\n" +
                         ")\r\n" +
                         "if /I \"%1\"==\"gateway\" if /I \"%2\"==\"status\" (\r\n" +
                         "  echo Runtime: running\r\n" +
                         "  echo RPC probe: ok\r\n" +
                         "  echo port %port%\r\n" +
                         "  exit /b 0\r\n" +
                         ")\r\n" +
                         "if /I \"%1\"==\"onboard\" exit /b 0\r\n" +
                         "if /I \"%1\"==\"doctor\" exit /b 0\r\n" +
                         "if /I \"%1\"==\"config\" if /I \"%2\"==\"set\" if /I \"%3\"==\"gateway.mode\" exit /b 0\r\n" +
                         "if /I \"%1\"==\"gateway\" if /I \"%2\"==\"install\" exit /b 0\r\n" +
                         "if /I \"%1\"==\"gateway\" if /I \"%2\"==\"start\" exit /b 0\r\n" +
                         "if /I \"%1\"==\"security\" if /I \"%2\"==\"audit\" (\r\n" +
                         "  echo Security audit ok\r\n" +
                         "  exit /b 0\r\n" +
                         ")\r\n" +
                         "if /I \"%1\"==\"secrets\" if /I \"%2\"==\"audit\" (\r\n" +
                         "  echo Secrets audit ok\r\n" +
                         "  exit /b 0\r\n" +
                         ")\r\n" +
                         "if /I \"%1\"==\"dashboard\" (\r\n" +
                         "  echo Dashboard: http://127.0.0.1:%port%\r\n" +
                         "  exit /b 0\r\n" +
                         ")\r\n" +
                         "exit /b 0\r\n";
            File.WriteAllText(templatePath, script, Encoding.ASCII);
        }
        else
        {
            var script = "#!/usr/bin/env bash\n" +
                         "set -euo pipefail\n" +
                         "state=\"${RECLAW_OPENCLAW_STATE:-}\"\n" +
                         "port=\"${OPENCLAW_GATEWAY_PORT:-18789}\"\n" +
                         "if [[ \"${1:-}\" == \"gateway\" && \"${2:-}\" == \"status\" && \"${3:-}\" == \"--require-rpc\" ]]; then\n" +
                         "  if [[ -n \"$state\" && -f \"$state\" ]]; then\n" +
                         "    echo \"Runtime: running\"\n" +
                         "    echo \"RPC probe: ok\"\n" +
                         "    echo \"port $port\"\n" +
                         "  else\n" +
                         "    echo \"Runtime: stopped\"\n" +
                         "    echo \"RPC probe: failed\"\n" +
                         "    echo \"port $port\"\n" +
                         "    if [[ -n \"$state\" ]]; then echo \"seen\" > \"$state\"; fi\n" +
                         "  fi\n" +
                         "  exit 0\n" +
                         "fi\n" +
                         "if [[ \"${1:-}\" == \"gateway\" && \"${2:-}\" == \"status\" ]]; then\n" +
                         "  echo \"Runtime: running\"\n" +
                         "  echo \"RPC probe: ok\"\n" +
                         "  echo \"port $port\"\n" +
                         "  exit 0\n" +
                         "fi\n" +
                         "if [[ \"${1:-}\" == \"onboard\" ]]; then exit 0; fi\n" +
                         "if [[ \"${1:-}\" == \"doctor\" ]]; then exit 0; fi\n" +
                         "if [[ \"${1:-}\" == \"config\" && \"${2:-}\" == \"set\" && \"${3:-}\" == \"gateway.mode\" ]]; then exit 0; fi\n" +
                         "if [[ \"${1:-}\" == \"gateway\" && \"${2:-}\" == \"install\" ]]; then exit 0; fi\n" +
                         "if [[ \"${1:-}\" == \"gateway\" && \"${2:-}\" == \"start\" ]]; then exit 0; fi\n" +
                         "if [[ \"${1:-}\" == \"security\" && \"${2:-}\" == \"audit\" ]]; then echo \"Security audit ok\"; exit 0; fi\n" +
                         "if [[ \"${1:-}\" == \"secrets\" && \"${2:-}\" == \"audit\" ]]; then echo \"Secrets audit ok\"; exit 0; fi\n" +
                         "if [[ \"${1:-}\" == \"dashboard\" ]]; then echo \"Dashboard: http://127.0.0.1:$port\"; exit 0; fi\n" +
                         "exit 0\n";
            File.WriteAllText(templatePath, script, Encoding.ASCII);
            File.SetUnixFileMode(templatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
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

    [Fact]
    [Trait("Category", "Live")]
    public void MissingInstall_ActivatesSetupMode()
    {
        using var scope = new EnvScope()
            .Set("RECLAW_DISABLE_WSL", "1")
            .Set("RECLAW_OPENCLAW_PATH", "/nonexistent/openclaw")
            .Set("OPENCLAW_EXE", null)
            .Set("OPENCLAW_ENTRY", null)
            .Set("OPENCLAW_REPO", null);

        var context = new ActionContext(
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            null,
            null);

        var candidate = OpenClawLocator.ResolveWithSource(context);
        Assert.True(candidate == null || candidate.Source == "repo-fallback",
            "When OpenClaw is not installed, locator should return null or repo-fallback.");
    }

    [Fact]
    public async Task SetupRestore_RequiresArchivePath()
    {
        var context = new ActionContext(
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            null,
            null);

        var result = await InternalActionDispatcher.ExecuteAsync(
            "setup-restore",
            Guid.NewGuid(),
            context,
            new BackupRestoreInput(ArchivePath: null),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None,
            new BackupService(),
            new ProcessRunner());

        Assert.False(result.Success);
        Assert.Contains("ArchivePath", result.Error);
    }

    [Fact]
    public async Task SetupAdvanced_FailsWhenLegacyScriptsMissing()
    {
        using var scope = new EnvScope()
            .Set("RECLAW_LEGACY_SCRIPTS", "/nonexistent/scripts/path");

        var context = new ActionContext(
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            null,
            null);

        var result = await InternalActionDispatcher.ExecuteAsync(
            "setup-advanced",
            Guid.NewGuid(),
            context,
            new EmptyInput(),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None,
            new BackupService(),
            new ProcessRunner());

        Assert.False(result.Success);
        Assert.True(
            result.Error?.Contains("Legacy scripts not found") == true ||
            result.Error?.Contains("Command exited with code") == true ||
            result.Error?.Contains("not found") == true,
            $"Expected error about missing scripts, got: {result.Error}");
    }

    [Fact]
    public async Task SetupInstall_SecurityPhase_ReportsStatus()
    {
        using var server = new HealthServer(failFirstProbe: false);
        var tempRoot = Directory.CreateTempSubdirectory();
        var binDir = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "bin")).FullName;
        var scriptsRoot = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "legacy")).FullName;
        var repoRoot = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "repo")).FullName;
        var openClawHome = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "home")).FullName;

        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"gateway\": { \"auth\": { \"token\": \"test-token\" } } }");
        File.WriteAllText(Path.Combine(scriptsRoot, "install-openclaw-cli.js"), "// stub");

        var isWindows = OperatingSystem.IsWindows();
        var openClawPath = Path.Combine(binDir, isWindows ? "openclaw.cmd" : "openclaw");
        var templatePath = Path.Combine(binDir, isWindows ? "openclaw.template.cmd" : "openclaw.template");
        var statePath = Path.Combine(tempRoot.FullName, "openclaw.state");

        WriteOpenClawTemplate(templatePath);
        WriteNodeStub(binDir);

        var originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        using var scope = new EnvScope()
            .Set("RECLAW_DISABLE_WSL", "1")
            .Set("RECLAW_GATEWAY_DETACHED_START_SIMULATE", "1")
            .Set("OPENCLAW_GATEWAY_PORT", server.Port.ToString())
            .Set("OPENCLAW_REPO", repoRoot)
            .Set("RECLAW_LEGACY_SCRIPTS", scriptsRoot)
            .Set("RECLAW_OPENCLAW_PATH", openClawPath)
            .Set("RECLAW_OPENCLAW_TEMPLATE", templatePath)
            .Set("RECLAW_OPENCLAW_STATE", statePath)
            .Set("OPENCLAW_ENTRY", null)
            .Set("OPENCLAW_EXE", null)
            .Set("PATH", $"{binDir}{Path.PathSeparator}{originalPath}");

        var context = new ActionContext(
            tempRoot.FullName,
            tempRoot.FullName,
            tempRoot.FullName,
            tempRoot.FullName,
            tempRoot.FullName,
            openClawHome,
            null,
            null);

        var result = await InternalActionDispatcher.ExecuteAsync(
            "setup-install",
            Guid.NewGuid(),
            context,
            new EmptyInput(),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None,
            new BackupService(),
            new ProcessRunner());

        var summary = Assert.IsType<SetupAssistantSummary>(result.Output);
        Assert.Contains(summary.Steps, step => step.Step == "security");
        Assert.NotNull(summary.Security);
        Assert.Contains(new[] { "Safe", "Needs attention", "Critical" }, s => s == summary.Security.Status);
        Assert.True(summary.Security.Checks.Count >= 3);
        Assert.Contains(summary.Security.Checks, c => c.Name == "Security audit");
        Assert.Contains(summary.Security.Checks, c => c.Name == "Secrets audit");
        Assert.Contains(summary.Security.Checks, c => c.Name == "Token readiness");
    }

    [Fact]
    public async Task SetupInstall_HealthyGateway_OpensDashboard()
    {
        using var server = new HealthServer(failFirstProbe: false);
        var tempRoot = Directory.CreateTempSubdirectory();
        var binDir = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "bin")).FullName;
        var scriptsRoot = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "legacy")).FullName;
        var repoRoot = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "repo")).FullName;
        var openClawHome = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "home")).FullName;

        File.WriteAllText(Path.Combine(openClawHome, "openclaw.json"), "{ \"gateway\": { \"auth\": { \"token\": \"test-token\" } } }");
        File.WriteAllText(Path.Combine(scriptsRoot, "install-openclaw-cli.js"), "// stub");

        var isWindows = OperatingSystem.IsWindows();
        var openClawPath = Path.Combine(binDir, isWindows ? "openclaw.cmd" : "openclaw");
        var templatePath = Path.Combine(binDir, isWindows ? "openclaw.template.cmd" : "openclaw.template");
        var statePath = Path.Combine(tempRoot.FullName, "openclaw.state");

        // Pre-create state file so gateway appears healthy on first probe
        File.WriteAllText(statePath, "ready");

        WriteOpenClawTemplate(templatePath);
        WriteNodeStub(binDir);

        var originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        using var scope = new EnvScope()
            .Set("RECLAW_DISABLE_WSL", "1")
            .Set("RECLAW_GATEWAY_DETACHED_START_SIMULATE", "1")
            .Set("OPENCLAW_GATEWAY_PORT", server.Port.ToString())
            .Set("OPENCLAW_REPO", repoRoot)
            .Set("RECLAW_LEGACY_SCRIPTS", scriptsRoot)
            .Set("RECLAW_OPENCLAW_PATH", openClawPath)
            .Set("RECLAW_OPENCLAW_TEMPLATE", templatePath)
            .Set("RECLAW_OPENCLAW_STATE", statePath)
            .Set("OPENCLAW_ENTRY", null)
            .Set("OPENCLAW_EXE", null)
            .Set("PATH", $"{binDir}{Path.PathSeparator}{originalPath}");

        var context = new ActionContext(
            tempRoot.FullName,
            tempRoot.FullName,
            tempRoot.FullName,
            tempRoot.FullName,
            tempRoot.FullName,
            openClawHome,
            null,
            null);

        var result = await InternalActionDispatcher.ExecuteAsync(
            "setup-install",
            Guid.NewGuid(),
            context,
            new EmptyInput(),
            new Progress<ActionEvent>(_ => { }),
            CancellationToken.None,
            new BackupService(),
            new ProcessRunner());

        var summary = Assert.IsType<SetupAssistantSummary>(result.Output);
        Assert.True(summary.GatewayHealthy);
        Assert.False(summary.RepairAttempted);
        Assert.Contains(summary.Steps, step => step.Step == "dashboard-open");
    }

    private sealed class HealthServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly Task loop;
        private int requestCount;
        private readonly int failureBudget;

        public int Port { get; }

        public HealthServer(bool failFirstProbe)
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            failureBudget = failFirstProbe ? 1 : 0;
            loop = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (client != null)
                {
                    _ = Task.Run(() => HandleClientAsync(client), cts.Token);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    var buffer = new byte[1024];
                    await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false);

                    var count = Interlocked.Increment(ref requestCount);
                    var status = count <= failureBudget ? "503 Service Unavailable" : "200 OK";
                    var body = "ok";
                    var response = $"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n{body}";
                    var bytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(bytes, 0, bytes.Length, cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // best effort
                }
            }
        }

        public void Dispose()
        {
            cts.Cancel();
            listener.Stop();
            try
            {
                loop.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // ignore
            }
            cts.Dispose();
        }
    }
}
