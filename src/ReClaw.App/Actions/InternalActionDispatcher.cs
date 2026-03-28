using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Diagnostics;
using ReClaw.App.Execution;
using ReClaw.Core;

namespace ReClaw.App.Actions;

public static class InternalActionDispatcher
{
    private const string GatewayModeUnsetMessage = "gateway.mode is unset; gateway start is blocked. Fix: openclaw config set gateway.mode local (or run openclaw configure).";
    private static readonly HttpClient Http = new();
    private sealed record SetupInstallResult(ActionResult Result, string Runtime, string Detail);
    private sealed record GatewayHealthResult(bool Healthy, string? Detail, OpenClawCommandSummary? StatusSummary, int Port);
    private sealed record SetupRepairResult(bool Healthy, bool DetachedFallback, IReadOnlyList<SetupStepSummary> Steps, OpenClawCommandSummary? StatusSummary);

    private static bool UseWslRuntime(ActionContext context)
    {
        var resolved = OpenClawLocator.ResolveWithSource(context);
        return resolved != null && string.Equals(resolved.Source, "wsl", StringComparison.OrdinalIgnoreCase);
    }
    public static async Task<ActionResult> ExecuteAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        object? input,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        BackupService backupService,
        ProcessRunner processRunner)
    {
        switch (actionId)
        {
            case "backup":
            case "backup-create":
                return await RunBackupCreate(actionId, correlationId, context, input as BackupCreateInput, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "restore-latest":
                return await RunRestoreLatest(actionId, correlationId, context, input as BackupRestoreInput, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "restore-archive":
                return await RunRestoreArchive(actionId, correlationId, context, input as BackupRestoreInput, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "backup-restore":
                return await RunBackupRestore(actionId, correlationId, context, input as BackupRestoreInput, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "backup-diff":
                return await RunBackupDiff(actionId, correlationId, context, input as BackupDiffInput, events, cancellationToken).ConfigureAwait(false);
            case "backup-schedule-create":
                return await RunBackupScheduleCreate(actionId, correlationId, context, input as BackupScheduleInput, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "backup-schedule-list":
                return await RunBackupScheduleList(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "backup-schedule-remove":
                return await RunBackupScheduleRemove(actionId, correlationId, context, input as BackupScheduleRemoveInput, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "reclaw-backup-list":
                return RunBackupList(actionId, correlationId, context, events);
            case "reclaw-backup-prune-plan":
                return RunBackupPrunePlan(actionId, correlationId, context, input as BackupPruneInput, events);
            case "backup-prune":
                return RunBackupPrunePlan(actionId, correlationId, context, input as BackupPruneInput, events);
            case "reclaw-backup-verify":
            case "backup-verify":
                return await RunBackupVerify(actionId, correlationId, context, input as BackupVerifyInput, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "verify-all":
                return await RunBackupVerify(actionId, correlationId, context, input as BackupVerifyInput, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "reclaw-backup-export":
            case "backup-export":
                return await RunBackupExport(actionId, correlationId, context, input as BackupExportInput, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "doctor":
                return await RunDoctor(actionId, correlationId, context, input as DoctorInput, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "fix":
                return await RunFix(actionId, correlationId, context, input as FixInput, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "status":
                return RunStatus(actionId, correlationId, context, events);
            case "recover":
                return await RunRecover(actionId, correlationId, context, input as RecoverInput, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "openclaw-rebuild":
                return await RunOpenClawRebuild(actionId, correlationId, context, input as OpenClawRebuildInput, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "rollback":
                return await RunRollback(actionId, correlationId, context, input as RollbackInput, events, backupService).ConfigureAwait(false);
            case "reset":
                return await RunReset(actionId, correlationId, context, input as ResetInput, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "diagnostics-export":
                return await RunDiagnosticsExport(actionId, correlationId, context, input as DiagnosticsExportInput, events, cancellationToken).ConfigureAwait(false);
            case "openclaw-terminal":
                return await RunOpenClawTerminal(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "openclaw-cleanup-related":
                return RunOpenClawCleanup(actionId, correlationId, context, input as OpenClawCleanupInput, events);
            case "gateway-url":
                return await RunGatewayUrl(actionId, correlationId, context, input as GatewayUrlInput, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "dashboard-open":
                return await RunDashboardOpen(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "gateway-token-show":
                return await RunGatewayTokenShow(actionId, correlationId, context, input as GatewayTokenInput, events, cancellationToken).ConfigureAwait(false);
            case "gateway-token-generate":
                return await RunGatewayTokenGenerate(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "browser-diagnostics":
            case "gateway-browser-diagnostics":
                return await RunBrowserDiagnostics(actionId, correlationId, context, input as BrowserDiagnosticsInput, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "gateway-start":
                return await RunGatewayCommand(actionId, correlationId, context, new[] { "gateway", "start" }, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "gateway-stop":
                return await RunGatewayCommand(actionId, correlationId, context, new[] { "gateway", "stop" }, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "gateway-status":
                return await RunGatewayCommand(actionId, correlationId, context, new[] { "gateway", "status" }, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "gateway-troubleshoot":
                return await RunGatewayTroubleshoot(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "gateway-logs":
                return await RunGatewayCommand(actionId, correlationId, context, new[] { "logs", "--follow" }, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "gateway-repair":
                return await RunGatewayCommand(actionId, correlationId, context, new[] { "gateway", "start" }, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "setup-install":
                return await RunSetupInstall(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "setup-restore":
                return await RunSetupRestore(actionId, correlationId, context, input as BackupRestoreInput, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            case "setup-advanced":
                return await RunSetupAdvanced(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "oc-fix-missing-plugins":
                return await RunFixMissingPlugins(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "oc-gateway-disable-autostart":
                return await RunGatewayDisableAutostart(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "oc-gateway-run":
                return await RunGatewayRun(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "oc-gateway-install-start":
                return await RunGatewayInstallStart(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "oc-gateway-kill":
                return await RunGatewayKill(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "install-openclaw-cli":
                return await RunInstallOpenClawCli(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "oc-update-pull":
                return await RunOpenClawUpdatePull(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "fresh-install":
                return await RunFreshInstall(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "clone-openclaw":
                return await RunCloneOpenClaw(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "drill":
                return await RunRecoveryDrill(actionId, correlationId, context, input as BackupCreateInput, events, processRunner, cancellationToken).ConfigureAwait(false);
            case "nuke":
                return await RunNuke(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            default:
                return new ActionResult(false, Error: $"Action '{actionId}' not implemented yet.");
        }
    }

    private static async Task<ActionResult> RunSetupInstall(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        return await RunSetupFlowAsync(
            actionId,
            correlationId,
            context,
            restoreInput: null,
            events,
            backupService: null,
            processRunner,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunSetupRestore(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupRestoreInput? input,
        IProgress<ActionEvent> events,
        BackupService backupService,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input?.ArchivePath))
        {
            return new ActionResult(false, Error: "ArchivePath is required.");
        }

        return await RunSetupFlowAsync(
            actionId,
            correlationId,
            context,
            restoreInput: input,
            events,
            backupService,
            processRunner,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunSetupAdvanced(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var warnings = new List<WarningItem>();
        var scriptsRoot = FindLegacyScriptsRoot();
        if (string.IsNullOrWhiteSpace(scriptsRoot))
        {
            return new ActionResult(false, Error: "Legacy scripts not found. Set RECLAW_LEGACY_SCRIPTS to the ReClaw/scripts folder.");
        }

        ActionResult result;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var script = Path.Combine(scriptsRoot, "fresh-install-openclaw-local-windows.ps1");
            result = await RunScriptAsync(actionId, correlationId, processRunner, events, cancellationToken,
                "powershell.exe",
                new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script },
                Path.GetDirectoryName(script));
        }
        else
        {
            var script = Path.Combine(scriptsRoot, "fresh-install-openclaw-local-mac.sh");
            result = await RunScriptAsync(actionId, correlationId, processRunner, events, cancellationToken,
                "bash",
                new[] { script },
                Path.GetDirectoryName(script));
        }

        return MergeWarnings(result, warnings);
    }

    private static async Task<ActionResult> RunFixMissingPlugins(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var scriptsRoot = FindLegacyScriptsRoot();
        if (string.IsNullOrWhiteSpace(scriptsRoot))
        {
            return new ActionResult(false, Error: "Legacy scripts not found. Set RECLAW_LEGACY_SCRIPTS to the ReClaw/scripts folder.");
        }

        var script = Path.Combine(scriptsRoot, "cleanup-openclaw-plugins.js");
        if (!File.Exists(script))
        {
            return new ActionResult(false, Error: $"Legacy script not found: {script}");
        }

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var configPath = ResolveConfigPath(context);
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            env["OPENCLAW_CONFIG"] = configPath;
            env["OPENCLAW_CONFIG_PATH"] = configPath;
        }

        return await RunNodeScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, script, env).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunGatewayDisableAutostart(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        if (UseWslRuntime(context))
        {
            var warnings = new List<WarningItem>
            {
                new("wsl-autostart-skip", "WSL runtime detected; Windows autostart is not applicable.")
            };
            return new ActionResult(true, Output: new EmptyOutput(), Warnings: warnings);
        }

        var scriptsRoot = FindLegacyScriptsRoot();
        if (string.IsNullOrWhiteSpace(scriptsRoot))
        {
            return new ActionResult(false, Error: "Legacy scripts not found. Set RECLAW_LEGACY_SCRIPTS to the ReClaw/scripts folder.");
        }

        var script = Path.Combine(scriptsRoot, "disable-gateway-autostart.js");
        if (!File.Exists(script))
        {
            return new ActionResult(false, Error: $"Legacy script not found: {script}");
        }

        return await RunNodeScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, script).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunGatewayKill(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        if (UseWslRuntime(context))
        {
            var warnings = new List<WarningItem>
            {
                new("wsl-runtime", "WSL runtime detected; stopping gateway inside WSL.")
            };

            var wslRunner = new OpenClawRunner(processRunner);
            var stopResult = await wslRunner.RunAsync(actionId, correlationId, context, new[] { "gateway", "stop" }, events, cancellationToken).ConfigureAwait(false);
            if (!stopResult.Success)
            {
                warnings.Add(new WarningItem("gateway-stop-failed", stopResult.Error ?? "Gateway stop failed."));
            }

            var wslStatusResult = await wslRunner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status" }, events, cancellationToken).ConfigureAwait(false);
            return MergeWarnings(wslStatusResult, warnings);
        }

        var scriptsRoot = FindLegacyScriptsRoot();
        if (string.IsNullOrWhiteSpace(scriptsRoot))
        {
            return new ActionResult(false, Error: "Legacy scripts not found. Set RECLAW_LEGACY_SCRIPTS to the ReClaw/scripts folder.");
        }

        var script = Path.Combine(scriptsRoot, "kill-gateway-processes.js");
        if (!File.Exists(script))
        {
            return new ActionResult(false, Error: $"Legacy script not found: {script}");
        }

        var killResult = await RunNodeScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, script).ConfigureAwait(false);
        if (!killResult.Success)
        {
            return killResult;
        }

        var resolved = OpenClawLocator.Resolve(context);
        if (resolved == null)
        {
            return new ActionResult(true, Output: new EmptyOutput());
        }

        var runner = new OpenClawRunner(processRunner);
        var statusResult = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status" }, events, cancellationToken).ConfigureAwait(false);
        return statusResult;
    }

    private static async Task<ActionResult> RunGatewayRun(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        if (UseWslRuntime(context))
        {
            return await RunGatewayRunWsl(actionId, correlationId, context, events, processRunner, cancellationToken, installStart: false).ConfigureAwait(false);
        }

        var scriptsRoot = FindLegacyScriptsRoot();
        if (string.IsNullOrWhiteSpace(scriptsRoot))
        {
            return new ActionResult(false, Error: "Legacy scripts not found. Set RECLAW_LEGACY_SCRIPTS to the ReClaw/scripts folder.");
        }

        var installResult = await EnsureOpenClawCliAsync(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
        if (installResult != null)
        {
            return installResult;
        }

        var warnings = new List<WarningItem>();
        var disableScript = Path.Combine(scriptsRoot, "disable-gateway-autostart.js");
        var killScript = Path.Combine(scriptsRoot, "kill-gateway-processes.js");
        var detachedScript = Path.Combine(scriptsRoot, "start-gateway-detached.js");
        if (!File.Exists(disableScript) || !File.Exists(killScript) || !File.Exists(detachedScript))
        {
            return new ActionResult(false, Error: "Legacy gateway scripts missing. Ensure ReClaw/scripts is present.");
        }

        var disableResult = await RunNodeScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, disableScript).ConfigureAwait(false);
        if (!disableResult.Success)
        {
            warnings.Add(new WarningItem("gateway-autostart-disable-failed", disableResult.Error ?? "Disable gateway autostart failed."));
        }

        var killResult = await RunNodeScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, killScript).ConfigureAwait(false);
        if (!killResult.Success)
        {
            warnings.Add(new WarningItem("gateway-kill-failed", killResult.Error ?? "Kill gateway processes failed."));
        }

        var detachedResult = await RunNodeScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, detachedScript).ConfigureAwait(false);
        if (!detachedResult.Success)
        {
            return MergeWarnings(detachedResult, warnings);
        }

        var runner = new OpenClawRunner(processRunner);
        var statusResult = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status" }, events, cancellationToken).ConfigureAwait(false);
        return MergeWarnings(statusResult, warnings);
    }

    private static async Task<ActionResult> RunGatewayInstallStart(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        if (UseWslRuntime(context))
        {
            return await RunGatewayRunWsl(actionId, correlationId, context, events, processRunner, cancellationToken, installStart: true).ConfigureAwait(false);
        }

        var scriptsRoot = FindLegacyScriptsRoot();
        if (string.IsNullOrWhiteSpace(scriptsRoot))
        {
            return new ActionResult(false, Error: "Legacy scripts not found. Set RECLAW_LEGACY_SCRIPTS to the ReClaw/scripts folder.");
        }

        var installResult = await EnsureOpenClawCliAsync(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
        if (installResult != null)
        {
            return installResult;
        }

        var warnings = new List<WarningItem>();
        var disableScript = Path.Combine(scriptsRoot, "disable-gateway-autostart.js");
        var killScript = Path.Combine(scriptsRoot, "kill-gateway-processes.js");
        var detachedScript = Path.Combine(scriptsRoot, "start-gateway-detached.js");
        if (!File.Exists(disableScript) || !File.Exists(killScript) || !File.Exists(detachedScript))
        {
            return new ActionResult(false, Error: "Legacy gateway scripts missing. Ensure ReClaw/scripts is present.");
        }

        var disableResult = await RunNodeScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, disableScript).ConfigureAwait(false);
        if (!disableResult.Success)
        {
            warnings.Add(new WarningItem("gateway-autostart-disable-failed", disableResult.Error ?? "Disable gateway autostart failed."));
        }

        var killResult = await RunNodeScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, killScript).ConfigureAwait(false);
        if (!killResult.Success)
        {
            warnings.Add(new WarningItem("gateway-kill-failed", killResult.Error ?? "Kill gateway processes failed."));
        }

        var runner = new OpenClawRunner(processRunner);
        var installGatewayResult = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "install", "--force" }, events, cancellationToken).ConfigureAwait(false);
        if (!installGatewayResult.Success)
        {
            return MergeWarnings(installGatewayResult, warnings);
        }

        var disableAgainResult = await RunNodeScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, disableScript).ConfigureAwait(false);
        if (!disableAgainResult.Success)
        {
            warnings.Add(new WarningItem("gateway-autostart-disable-failed", disableAgainResult.Error ?? "Disable gateway autostart failed."));
        }

        var startResult = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "start" }, events, cancellationToken).ConfigureAwait(false);
        if (!startResult.Success)
        {
            return MergeWarnings(startResult, warnings);
        }

        var detachedResult = await RunNodeScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, detachedScript).ConfigureAwait(false);
        if (!detachedResult.Success)
        {
            warnings.Add(new WarningItem("gateway-detached-start-failed", detachedResult.Error ?? "Detached gateway run failed."));
        }

        var statusResult = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status" }, events, cancellationToken).ConfigureAwait(false);
        return MergeWarnings(statusResult, warnings);
    }

    private static async Task<ActionResult> RunGatewayRunWsl(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken,
        bool installStart)
    {
        var warnings = new List<WarningItem>
        {
            new(
                "wsl-runtime",
                installStart
                    ? "WSL runtime detected; using gateway run (no autostart) instead of Windows install/start."
                    : "WSL runtime detected; using gateway run (no autostart) inside WSL.")
        };

        var runner = new OpenClawRunner(processRunner);
        var stopResult = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "stop" }, events, cancellationToken).ConfigureAwait(false);
        if (!stopResult.Success)
        {
            warnings.Add(new WarningItem("gateway-stop-failed", stopResult.Error ?? "Gateway stop failed."));
        }

        var detachedCommand = "nohup openclaw gateway run --port 18789 >/tmp/openclaw-gateway.log 2>&1 &";
        var detachedResult = await RunScriptAsync(
            actionId,
            correlationId,
            processRunner,
            events,
            cancellationToken,
            "wsl.exe",
            new[] { "--", "bash", "-lc", detachedCommand },
            workingDirectory: null).ConfigureAwait(false);

        if (!detachedResult.Success)
        {
            return MergeWarnings(detachedResult, warnings);
        }

        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        var statusResult = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status" }, events, cancellationToken).ConfigureAwait(false);
        return MergeWarnings(statusResult, warnings);
    }

    private static async Task<ActionResult> RunInstallOpenClawCli(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var warnings = new List<WarningItem>();
        if (OperatingSystem.IsWindows() && (OpenClawLocator.IsWslForced() || OpenClawLocator.IsWsl2Default()))
        {
            var wslArgs = new[] { "--", "npm", "install", "-g", "openclaw@latest", "--no-fund", "--no-audit", "--loglevel=error" };
            var wslInstall = await RunScriptAsync(
                actionId,
                correlationId,
                processRunner,
                events,
                cancellationToken,
                "wsl.exe",
                wslArgs,
                workingDirectory: null).ConfigureAwait(false);

            if (wslInstall.Success)
            {
                return wslInstall;
            }

            if (OpenClawLocator.IsWslForced())
            {
                return wslInstall;
            }

            warnings.Add(new WarningItem("wsl-install-failed", wslInstall.Error ?? "WSL install failed; falling back to native."));
        }

        var scriptsRoot = FindLegacyScriptsRoot();
        if (string.IsNullOrWhiteSpace(scriptsRoot))
        {
            return new ActionResult(false, Error: "Legacy scripts not found. Set RECLAW_LEGACY_SCRIPTS to the ReClaw/scripts folder.");
        }

        var script = Path.Combine(scriptsRoot, "install-openclaw-cli.js");
        if (!File.Exists(script))
        {
            return new ActionResult(false, Error: $"Legacy script not found: {script}");
        }

        var result = await RunNodeScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, script).ConfigureAwait(false);
        return MergeWarnings(result, warnings);
    }

    private static async Task<ActionResult> RunOpenClawUpdatePull(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var repoRoot = OpenClawLocator.ResolveRepoRoot(context);
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return new ActionResult(false, Error: "OpenClaw repo not found. Set OPENCLAW_REPO or OPENCLAW_ENTRY.");
        }

        return await RunScriptAsync(
            actionId,
            correlationId,
            processRunner,
            events,
            cancellationToken,
            "git",
            new[] { "-C", repoRoot, "pull", "--ff-only" },
            repoRoot).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunFreshInstall(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var scriptsRoot = FindLegacyScriptsRoot();
        if (string.IsNullOrWhiteSpace(scriptsRoot))
        {
            return new ActionResult(false, Error: "Legacy scripts not found. Set RECLAW_LEGACY_SCRIPTS to the ReClaw/scripts folder.");
        }

        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(scriptsRoot, "fresh-install-openclaw-local-windows.ps1");
            if (!File.Exists(script))
            {
                return new ActionResult(false, Error: $"Legacy script not found: {script}");
            }

            return await RunPowerShellScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, script).ConfigureAwait(false);
        }

        var unixScript = Path.Combine(scriptsRoot, "fresh-install-openclaw-local-mac.sh");
        if (!File.Exists(unixScript))
        {
            return new ActionResult(false, Error: $"Legacy script not found: {unixScript}");
        }

        return await RunBashScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, unixScript).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunCloneOpenClaw(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var scriptsRoot = FindLegacyScriptsRoot();
        if (string.IsNullOrWhiteSpace(scriptsRoot))
        {
            return new ActionResult(false, Error: "Legacy scripts not found. Set RECLAW_LEGACY_SCRIPTS to the ReClaw/scripts folder.");
        }

        var script = Path.Combine(scriptsRoot, "clone-openclaw-repo.js");
        if (!File.Exists(script))
        {
            return new ActionResult(false, Error: $"Legacy script not found: {script}");
        }

        return await RunNodeScriptAsync(actionId, correlationId, processRunner, events, cancellationToken, script).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunRecoveryDrill(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupCreateInput? input,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ActionResult(false, Error: "Full drill action is currently available on Windows only.");
        }

        if (string.IsNullOrWhiteSpace(input?.Password))
        {
            return new ActionResult(false, Error: "Password is required for the drill action.");
        }

        var scriptsRoot = FindLegacyScriptsRoot();
        if (string.IsNullOrWhiteSpace(scriptsRoot))
        {
            return new ActionResult(false, Error: "Legacy scripts not found. Set RECLAW_LEGACY_SCRIPTS to the ReClaw/scripts folder.");
        }

        var script = Path.Combine(scriptsRoot, "test-openclaw-recovery-windows.ps1");
        if (!File.Exists(script))
        {
            return new ActionResult(false, Error: $"Legacy script not found: {script}");
        }

        return await RunPowerShellScriptAsync(
            actionId,
            correlationId,
            processRunner,
            events,
            cancellationToken,
            script,
            "-Password",
            input.Password,
            "-Yes").ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunNuke(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var scriptsRoot = FindLegacyScriptsRoot();
        if (string.IsNullOrWhiteSpace(scriptsRoot))
        {
            return new ActionResult(false, Error: "Legacy scripts not found. Set RECLAW_LEGACY_SCRIPTS to the ReClaw/scripts folder.");
        }

        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(scriptsRoot, "full-nuke-openclaw.ps1");
            if (!File.Exists(script))
            {
                return new ActionResult(false, Error: $"Legacy script not found: {script}");
            }

            return await RunPowerShellScriptAsync(
                actionId,
                correlationId,
                processRunner,
                events,
                cancellationToken,
                script,
                "-Yes",
                "-RemoveOpenClawRepo").ConfigureAwait(false);
        }

        var unixScript = Path.Combine(scriptsRoot, "full-nuke-openclaw.sh");
        if (!File.Exists(unixScript))
        {
            return new ActionResult(false, Error: $"Legacy script not found: {unixScript}");
        }

        return await RunBashScriptAsync(
            actionId,
            correlationId,
            processRunner,
            events,
            cancellationToken,
            unixScript,
            "--yes",
            "--remove-openclaw-repo").ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunSetupFlowAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupRestoreInput? restoreInput,
        IProgress<ActionEvent> events,
        BackupService? backupService,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var warnings = new List<WarningItem>();
        var steps = new List<SetupStepSummary>();

        var envDetail = BuildSetupEnvironmentDetail(context);
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Environment", envDetail));
        steps.Add(new SetupStepSummary("detect-env", "success", envDetail));

        var install = await EnsureOpenClawInstalledAsync(
            actionId,
            correlationId,
            context,
            events,
            processRunner,
            cancellationToken,
            warnings).ConfigureAwait(false);
        if (install.Result.Warnings is { Count: > 0 } installWarnings)
        {
            warnings.AddRange(installWarnings);
        }
        steps.Add(new SetupStepSummary("install-openclaw", install.Result.Success ? "success" : "failed", install.Detail));

        if (!install.Result.Success)
        {
            var securitySkipped = BuildSkippedSecuritySummary("Install failed.");
            var summary = BuildSetupSummary(
                install.Runtime,
                gatewayHealthy: false,
                repairAttempted: false,
                detachedFallback: false,
                securitySkipped,
                steps,
                dashboardUrl: null,
                note: install.Result.Error ?? "Install failed.");
            return new ActionResult(false, Output: summary, Error: install.Result.Error, ExitCode: install.Result.ExitCode, Warnings: warnings);
        }

        var onboard = await RunOnboardInstallDaemonAsync(
            actionId,
            correlationId,
            context,
            events,
            processRunner,
            cancellationToken,
            warnings).ConfigureAwait(false);
        if (onboard.Warnings is { Count: > 0 } onboardWarnings)
        {
            warnings.AddRange(onboardWarnings);
        }
        steps.Add(new SetupStepSummary("onboard", onboard.Success ? "success" : "failed", onboard.Error));

        if (!onboard.Success)
        {
            var securitySkipped = BuildSkippedSecuritySummary("Onboarding failed.");
            var summary = BuildSetupSummary(
                install.Runtime,
                gatewayHealthy: false,
                repairAttempted: false,
                detachedFallback: false,
                securitySkipped,
                steps,
                dashboardUrl: null,
                note: onboard.Error ?? "Onboarding failed.");
            return new ActionResult(false, Output: summary, Error: onboard.Error, ExitCode: onboard.ExitCode, Warnings: warnings);
        }

        if (restoreInput != null)
        {
            if (backupService == null)
            {
                return new ActionResult(false, Error: "Backup service unavailable for restore.");
            }

            var restore = await RunBackupRestore(actionId, correlationId, context, restoreInput, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            if (restore.Warnings is { Count: > 0 } restoreWarnings)
            {
                warnings.AddRange(restoreWarnings);
            }
            steps.Add(new SetupStepSummary("restore", restore.Success ? "success" : "failed", restore.Error));

            if (!restore.Success)
            {
                var securitySkipped = BuildSkippedSecuritySummary("Restore failed.");
                var summary = BuildSetupSummary(
                    install.Runtime,
                    gatewayHealthy: false,
                    repairAttempted: false,
                    detachedFallback: false,
                    securitySkipped,
                    steps,
                    dashboardUrl: null,
                    note: restore.Error ?? "Restore failed.");
                return new ActionResult(false, Output: summary, Error: restore.Error, ExitCode: restore.ExitCode, Warnings: warnings);
            }
        }

        var health = await VerifyGatewayHealthAsync(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
        steps.Add(new SetupStepSummary("gateway-health", health.Healthy ? "success" : "failed", health.Detail));

        var repairAttempted = false;
        var detachedFallback = false;
        var gatewayHealthy = health.Healthy;
        var statusSummary = health.StatusSummary;

        if (!gatewayHealthy)
        {
            repairAttempted = true;
            var repair = await RunSetupRepairLadderAsync(actionId, correlationId, context, events, processRunner, cancellationToken, statusSummary).ConfigureAwait(false);
            detachedFallback = repair.DetachedFallback;
            steps.AddRange(repair.Steps);
            gatewayHealthy = repair.Healthy;
            statusSummary = repair.StatusSummary ?? statusSummary;

            if (!gatewayHealthy)
            {
                warnings.Add(new WarningItem("gateway-not-ready", "Gateway did not become healthy after repair ladder."));
            }
        }

        var security = await RunSetupSecurityPhaseAsync(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
        steps.Add(new SetupStepSummary("security", security.Status, $"Security: {security.Status}"));

        string? dashboardUrl = null;
        if (gatewayHealthy)
        {
            var dashboard = await RunDashboardOpen(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
            if (dashboard.Output is OpenClawCommandSummary dashboardSummary)
            {
                dashboardUrl = TryExtractUrl(dashboardSummary);
            }
            steps.Add(new SetupStepSummary("dashboard-open", dashboard.Success ? "success" : "failed", dashboard.Error));
            if (!dashboard.Success)
            {
                warnings.Add(new WarningItem("dashboard-open-failed", dashboard.Error ?? "Dashboard open failed."));
            }
        }
        else
        {
            steps.Add(new SetupStepSummary("dashboard-open", "skipped", "Gateway not healthy."));
        }

        var summaryText = gatewayHealthy
            ? "Setup complete. Gateway healthy."
            : "Setup complete, but gateway needs attention.";
        summaryText += security.Status switch
        {
            "Critical" => " Security: Critical.",
            "Needs attention" => " Security: Needs attention.",
            _ => " Security: Safe."
        };

        var setupSummary = BuildSetupSummary(
            install.Runtime,
            gatewayHealthy,
            repairAttempted,
            detachedFallback,
            security,
            steps,
            dashboardUrl,
            summaryText);

        return new ActionResult(true, Output: setupSummary, Warnings: warnings);
    }

    private static string BuildSetupEnvironmentDetail(ActionContext context)
    {
        var os = RuntimeInformation.OSDescription.Trim();
        var arch = RuntimeInformation.OSArchitecture.ToString();
        var dotnet = Environment.Version.ToString();
        var wsl = OperatingSystem.IsWindows() && OpenClawLocator.IsWsl2Default()
            ? "WSL2 available"
            : "WSL2 unavailable";
        var configPath = ResolveConfigPath(context) ?? "(unknown)";
        return $"OS: {os} | Arch: {arch} | .NET: {dotnet} | {wsl} | Config: {configPath}";
    }

    private static SetupAssistantSummary BuildSetupSummary(
        string runtime,
        bool gatewayHealthy,
        bool repairAttempted,
        bool detachedFallback,
        SetupSecuritySummary security,
        IReadOnlyList<SetupStepSummary> steps,
        string? dashboardUrl,
        string? note)
    {
        var summary = string.IsNullOrWhiteSpace(note) ? "Setup finished." : note;
        return new SetupAssistantSummary(
            summary,
            runtime,
            gatewayHealthy,
            repairAttempted,
            detachedFallback,
            security,
            steps,
            dashboardUrl);
    }

    private static SetupSecuritySummary BuildSkippedSecuritySummary(string reason)
    {
        return new SetupSecuritySummary("Needs attention", new[]
        {
            new SetupSecurityCheck("Security audit", -1, false, reason),
            new SetupSecurityCheck("Secrets audit", -1, false, reason),
            new SetupSecurityCheck("Token readiness", -1, false, reason)
        });
    }

    private static async Task<GatewayHealthResult> VerifyGatewayHealthAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var runner = new OpenClawRunner(processRunner);
        var statusResult = await runner.RunAsync(
            $"{actionId}-gateway-health",
            correlationId,
            context,
            new[] { "gateway", "status", "--require-rpc" },
            events,
            cancellationToken).ConfigureAwait(false);

        var summary = statusResult.Output as OpenClawCommandSummary;
        var healthy = IsGatewayHealthy(summary);
        var port = ResolveGatewayPort(summary);
        var probeOk = healthy && await ProbeGatewayHealthAsync(port, cancellationToken).ConfigureAwait(false);
        var detail = healthy
            ? probeOk ? $"Gateway healthy on port {port}." : $"Gateway status ok but health probe failed at http://127.0.0.1:{port}/healthz."
            : statusResult.Error ?? "Gateway not healthy.";

        return new GatewayHealthResult(healthy && probeOk, detail, summary, port);
    }

    private static async Task<SetupRepairResult> RunSetupRepairLadderAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken,
        OpenClawCommandSummary? seedStatus)
    {
        var runner = new OpenClawRunner(processRunner);
        var steps = new List<SetupStepSummary>();
        var detachedFallback = false;

        var statusResult = await runner.RunAsync(
            $"{actionId}-repair-status",
            correlationId,
            context,
            new[] { "gateway", "status" },
            events,
            cancellationToken).ConfigureAwait(false);
        var statusSummary = statusResult.Output as OpenClawCommandSummary ?? seedStatus;
        steps.Add(new SetupStepSummary("repair-gateway-status", statusResult.Success ? "success" : "failed", statusResult.Error));

        var doctorResult = await runner.RunAsync(
            $"{actionId}-repair-doctor",
            correlationId,
            context,
            new[] { "doctor", "--non-interactive", "--yes" },
            events,
            cancellationToken).ConfigureAwait(false);
        steps.Add(new SetupStepSummary("repair-doctor", doctorResult.Success ? "success" : "failed", doctorResult.Error));

        var modeStep = await EnsureGatewayModeAsync(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
        steps.Add(modeStep);

        var installResult = await runner.RunAsync(
            $"{actionId}-repair-gateway-install",
            correlationId,
            context,
            new[] { "gateway", "install", "--force" },
            events,
            cancellationToken).ConfigureAwait(false);
        steps.Add(new SetupStepSummary("repair-gateway-install", installResult.Success ? "success" : "failed", installResult.Error));

        var startResult = await runner.RunAsync(
            $"{actionId}-repair-gateway-start",
            correlationId,
            context,
            new[] { "gateway", "start" },
            events,
            cancellationToken).ConfigureAwait(false);
        steps.Add(new SetupStepSummary("repair-gateway-start", startResult.Success ? "success" : "failed", startResult.Error));

        var port = ResolveGatewayPort(statusSummary);
        var healthOk = await ProbeGatewayHealthAsync(port, cancellationToken).ConfigureAwait(false);
        steps.Add(new SetupStepSummary("repair-health-probe", healthOk ? "success" : "failed", $"healthz port {port}"));

        if (!healthOk && OperatingSystem.IsWindows())
        {
            var launched = GatewayLaunchHelper.TryLaunchDetachedGateway(context, port);
            detachedFallback = launched;
            steps.Add(new SetupStepSummary("repair-detached-fallback", launched ? "success" : "failed", $"gateway run --port {port}"));
            if (launched)
            {
                healthOk = await ProbeGatewayHealthAsync(port, cancellationToken).ConfigureAwait(false);
                steps.Add(new SetupStepSummary("repair-detached-probe", healthOk ? "success" : "failed", $"healthz port {port}"));
            }
        }

        return new SetupRepairResult(healthOk, detachedFallback, steps, statusSummary);
    }

    private static async Task<SetupStepSummary> EnsureGatewayModeAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var configPath = ResolveConfigPath(context);
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return new SetupStepSummary("repair-gateway-mode", "skipped", "Config missing.");
        }

        try
        {
            var raw = File.ReadAllText(configPath);
            if (!Json5Reader.TryParse(raw, out var doc))
            {
                return new SetupStepSummary("repair-gateway-mode", "failed", "Config parse failed.");
            }

            using (doc)
            {
                var root = doc!.RootElement;
                var mode = TryFindGatewayMode(root);
                if (!string.IsNullOrWhiteSpace(mode))
                {
                    return new SetupStepSummary("repair-gateway-mode", "skipped", $"Gateway mode already '{mode}'.");
                }
            }
        }
        catch (Exception ex)
        {
            return new SetupStepSummary("repair-gateway-mode", "failed", ex.Message);
        }

        var runner = new OpenClawRunner(processRunner);
        var result = await runner.RunAsync(
            $"{actionId}-repair-gateway-mode",
            correlationId,
            context,
            new[] { "config", "set", "gateway.mode", "local" },
            events,
            cancellationToken).ConfigureAwait(false);

        return new SetupStepSummary(
            "repair-gateway-mode",
            result.Success ? "success" : "failed",
            result.Success ? "Set gateway.mode=local." : result.Error);
    }

    private static string? TryFindGatewayMode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("gateway", out var gateway) && gateway.ValueKind == JsonValueKind.Object)
        {
            if (gateway.TryGetProperty("mode", out var mode))
            {
                return mode.GetString();
            }
        }
        return null;
    }

    private static async Task<SetupSecuritySummary> RunSetupSecurityPhaseAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var runner = new OpenClawRunner(processRunner);
        var checks = new List<SetupSecurityCheck>();

        var audit = await runner.RunAsync(
            $"{actionId}-security-audit",
            correlationId,
            context,
            new[] { "security", "audit" },
            events,
            cancellationToken).ConfigureAwait(false);
        checks.Add(BuildSecurityCheck("Security audit", audit));

        var secrets = await runner.RunAsync(
            $"{actionId}-secrets-audit",
            correlationId,
            context,
            new[] { "secrets", "audit" },
            events,
            cancellationToken).ConfigureAwait(false);
        checks.Add(BuildSecurityCheck("Secrets audit", secrets));

        var (token, _) = TryReadGatewayToken(context);
        var tokenPresent = !string.IsNullOrWhiteSpace(token);
        checks.Add(new SetupSecurityCheck(
            "Token readiness",
            tokenPresent ? 0 : 1,
            tokenPresent,
            tokenPresent ? "Token present." : "Token missing."));

        var status = ResolveSecurityStatus(checks, audit.Output as OpenClawCommandSummary, secrets.Output as OpenClawCommandSummary);
        return new SetupSecuritySummary(status, checks);
    }

    private static SetupSecurityCheck BuildSecurityCheck(string name, ActionResult result)
    {
        var summary = result.Output as OpenClawCommandSummary;
        var exitCode = summary?.ExitCode ?? result.ExitCode ?? (result.Success ? 0 : 1);
        var detail = result.Success ? null : result.Error;
        return new SetupSecurityCheck(name, exitCode, result.Success, detail);
    }

    private static string ResolveSecurityStatus(
        IReadOnlyList<SetupSecurityCheck> checks,
        OpenClawCommandSummary? auditSummary,
        OpenClawCommandSummary? secretsSummary)
    {
        if (ContainsCritical(auditSummary) || ContainsCritical(secretsSummary))
        {
            return "Critical";
        }

        if (checks.Any(check => !check.Success))
        {
            return "Needs attention";
        }

        return "Safe";
    }

    private static bool ContainsCritical(OpenClawCommandSummary? summary)
    {
        if (summary == null) return false;
        return summary.StdOut.Concat(summary.StdErr)
            .Any(line => line.Contains("critical", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<SetupInstallResult> EnsureOpenClawInstalledAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken,
        List<WarningItem> warnings)
    {
        var resolved = OpenClawLocator.ResolveWithSource(context);
        if (resolved != null && !string.Equals(resolved.Source, "repo-fallback", StringComparison.OrdinalIgnoreCase))
        {
            var runtimeLabel = string.Equals(resolved.Source, "wsl", StringComparison.OrdinalIgnoreCase) ? "wsl2" : "native";
            return new SetupInstallResult(new ActionResult(true, Output: resolved.Command), runtimeLabel, $"OpenClaw runtime detected ({resolved.Source}).");
        }

        var preferWsl = OperatingSystem.IsWindows() && OpenClawLocator.IsWsl2Default();
        if (preferWsl)
        {
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Installer", "WSL2 detected; preferring WSL2 install."));
            var wslArgs = new[] { "--", "npm", "install", "-g", "openclaw@latest", "--no-fund", "--no-audit", "--loglevel=error" };
            var wslInstall = await RunScriptAsync(actionId, correlationId, processRunner, events, cancellationToken,
                "wsl.exe",
                wslArgs,
                Environment.CurrentDirectory).ConfigureAwait(false);

            if (wslInstall.Success)
            {
                resolved = OpenClawLocator.ResolveWithSource(context);
                if (resolved != null && string.Equals(resolved.Source, "wsl", StringComparison.OrdinalIgnoreCase))
                {
                    return new SetupInstallResult(new ActionResult(true, Output: wslInstall.Output, ExitCode: wslInstall.ExitCode), "wsl2", "Installed via WSL2.");
                }

                warnings.Add(new WarningItem("wsl-runtime-missing", "WSL2 install completed but OpenClaw CLI not detected inside WSL2."));
            }
            else
            {
                warnings.Add(new WarningItem("wsl-install-failed", wslInstall.Error ?? "WSL2 install failed; falling back to native."));
            }
        }

        var scriptsRoot = FindLegacyScriptsRoot();
        ActionResult installResult;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !string.IsNullOrWhiteSpace(scriptsRoot))
        {
            var script = Path.Combine(scriptsRoot, "install-openclaw-cli.js");
            installResult = await RunScriptAsync(actionId, correlationId, processRunner, events, cancellationToken,
                "node",
                new[] { script },
                Path.GetDirectoryName(script)).ConfigureAwait(false);
        }
        else
        {
            var args = new[] { "install", "-g", "openclaw@latest", "--no-fund", "--no-audit", "--loglevel=error" };
            installResult = await RunScriptAsync(actionId, correlationId, processRunner, events, cancellationToken,
                "npm",
                args,
                Environment.CurrentDirectory).ConfigureAwait(false);
        }

        if (!installResult.Success)
        {
            return new SetupInstallResult(installResult, "native", installResult.Error ?? "Install failed.");
        }

        resolved = OpenClawLocator.ResolveWithSource(context);
        if (resolved == null || string.Equals(resolved.Source, "repo-fallback", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new WarningItem("openclaw-missing", "OpenClaw CLI not detected after install. Restart ReClaw after installation."));
            return new SetupInstallResult(new ActionResult(false, Output: installResult.Output, Error: "OpenClaw CLI not detected after install."), "native", "OpenClaw CLI not detected after install.");
        }

        return new SetupInstallResult(new ActionResult(true, Output: installResult.Output, ExitCode: installResult.ExitCode), "native", "Installed via native runtime.");
    }

    private static async Task<ActionResult> RunOnboardInstallDaemonAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken,
        List<WarningItem> warnings)
    {
        var token = Guid.NewGuid().ToString("N");
        var args = new List<string>
        {
            "onboard",
            "--install-daemon",
            "--flow",
            "quickstart",
            "--mode",
            "local",
            "--non-interactive",
            "--auth-choice",
            "skip",
            "--gateway-auth",
            "token",
            "--gateway-token",
            token
        };

        var runner = new OpenClawRunner(processRunner);
        var result = await runner.RunAsync(actionId, correlationId, context, args.ToArray(), events, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            warnings.Add(new WarningItem("onboard-failed", "OpenClaw onboarding failed. Re-run onboard in a terminal if needed."));
        }
        return result;
    }

    private static string? FindLegacyScriptsRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("RECLAW_LEGACY_SCRIPTS");
        if (!string.IsNullOrWhiteSpace(explicitRoot) && Directory.Exists(explicitRoot))
        {
            return explicitRoot;
        }

        var searchRoots = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };

        foreach (var root in searchRoots)
        {
            var current = new DirectoryInfo(root);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "ReClaw", "scripts");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                var direct = Path.Combine(current.FullName, "scripts");
                if (File.Exists(Path.Combine(direct, "install-openclaw-cli.js")))
                {
                    return direct;
                }

                current = current.Parent;
            }
        }

        return null;
    }

    private static async Task<ActionResult> RunScriptAsync(
        string actionId,
        Guid correlationId,
        ProcessRunner processRunner,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory,
        IDictionary<string, string>? environment = null)
    {
        var spec = new ProcessRunSpec(fileName, args, workingDirectory, environment);
        var commandLine = string.Join(' ', new[] { fileName }.Concat(args));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Command", commandLine));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Executable", fileName));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Arguments", string.Join(' ', args)));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "WorkingDir", workingDirectory ?? "(null)"));

        var result = await processRunner.RunAsync(actionId, correlationId, spec, events, cancellationToken).ConfigureAwait(false);
        var summary = new OpenClawCommandSummary(
            commandLine,
            result.ExitCode,
            result.TimedOut,
            result.StdOut,
            result.StdErr,
            result.StdOutLineCount,
            result.StdErrLineCount,
            result.OutputTruncated);

        if (result.TimedOut)
        {
            return new ActionResult(false, Output: summary, Error: "Command timed out.", ExitCode: result.ExitCode);
        }

        return result.ExitCode == 0
            ? new ActionResult(true, Output: summary, ExitCode: result.ExitCode)
            : new ActionResult(false, Output: summary, Error: $"Command exited with code {result.ExitCode}.", ExitCode: result.ExitCode);
    }

    private static async Task<ActionResult> RunNodeScriptAsync(
        string actionId,
        Guid correlationId,
        ProcessRunner processRunner,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        string scriptPath,
        IDictionary<string, string>? environment = null)
    {
        var node = ResolveNodeExecutable();
        return await RunScriptAsync(
            actionId,
            correlationId,
            processRunner,
            events,
            cancellationToken,
            node,
            new[] { scriptPath },
            Path.GetDirectoryName(scriptPath),
            environment).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunPowerShellScriptAsync(
        string actionId,
        Guid correlationId,
        ProcessRunner processRunner,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        string scriptPath,
        params string[] extraArgs)
    {
        var powershell = ResolvePowerShellExecutable();
        var args = new List<string>
        {
            "-NoProfile",
            "-WindowStyle",
            "Hidden",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath
        };
        if (extraArgs is { Length: > 0 })
        {
            args.AddRange(extraArgs);
        }

        return await RunScriptAsync(
            actionId,
            correlationId,
            processRunner,
            events,
            cancellationToken,
            powershell,
            args,
            Path.GetDirectoryName(scriptPath)).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunBashScriptAsync(
        string actionId,
        Guid correlationId,
        ProcessRunner processRunner,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        string scriptPath,
        params string[] extraArgs)
    {
        var args = new List<string> { scriptPath };
        if (extraArgs is { Length: > 0 })
        {
            args.AddRange(extraArgs);
        }

        return await RunScriptAsync(
            actionId,
            correlationId,
            processRunner,
            events,
            cancellationToken,
            "bash",
            args,
            Path.GetDirectoryName(scriptPath)).ConfigureAwait(false);
    }

    private static async Task<ActionResult?> EnsureOpenClawCliAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        if (OpenClawLocator.Resolve(context) != null)
        {
            return null;
        }

        var install = await RunInstallOpenClawCli(actionId, correlationId, context, events, processRunner, cancellationToken).ConfigureAwait(false);
        if (!install.Success)
        {
            return install;
        }

        return null;
    }

    private static string ResolveNodeExecutable()
    {
        var envNode = Environment.GetEnvironmentVariable("RECLAW_NODE_PATH")
            ?? Environment.GetEnvironmentVariable("NODE");
        if (!string.IsNullOrWhiteSpace(envNode) && File.Exists(envNode))
        {
            return envNode;
        }
        return OperatingSystem.IsWindows() ? "node.exe" : "node";
    }

    private static string ResolvePowerShellExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PowerShell", "7", "pwsh.exe"),
            "powershell.exe"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "powershell.exe";
    }

    private static ActionResult MergeWarnings(ActionResult result, List<WarningItem> warnings)
    {
        if (warnings.Count == 0)
        {
            return result;
        }

        var merged = result.Warnings?.ToList() ?? new List<WarningItem>();
        merged.AddRange(warnings);
        return result with { Warnings = merged };
    }

    private static async Task<ActionResult> RunBackupCreate(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupCreateInput? input,
        IProgress<ActionEvent> events,
        BackupService backupService,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var ignoreEncryption = input?.NoEncrypt == true;
        var effectivePassword = ignoreEncryption ? null : input?.Password;
        var scope = input?.Scope;
        var tokens = ParseScopeTokens(scope, "full");
        var wantsOnlyConfig = tokens.SetEquals(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "config" });
        var includeWorkspace = tokens.Contains("full") || tokens.Contains("workspace");
        var openClawSupported = tokens.Contains("full") || wantsOnlyConfig || !includeWorkspace;

        if (!openClawSupported)
        {
            var source = input?.SourcePath ?? context.OpenClawHome;
            var output = input?.BackupPath ?? input?.OutputPath ?? BuildBackupOutputPath(context.BackupDirectory);
            var password = effectivePassword;
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Creating backup (legacy)", output));
            await backupService.CreateBackupAsync(source, output, password, input?.Scope).ConfigureAwait(false);
            if (input?.Verify == true)
            {
                await backupService.VerifySnapshotAsync(output, password).ConfigureAwait(false);
            }
            if (ignoreEncryption && !string.IsNullOrWhiteSpace(input?.Password))
            {
                return new ActionResult(true, Output: output, Warnings: new[] { new WarningItem("backup-no-encrypt", "Password ignored because --no-encrypt was set.") });
            }
            return new ActionResult(true, Output: output);
        }

        var outputPath = input?.BackupPath ?? input?.OutputPath ?? context.BackupDirectory;
        EnsureOutputDirectory(outputPath);

        var args = new List<string> { "backup", "create", "--verify", "--json", "--output", outputPath };
        if (wantsOnlyConfig)
        {
            args.Add("--only-config");
        }
        else if (!includeWorkspace)
        {
            args.Add("--no-include-workspace");
        }

        var openClawRunner = new OpenClawRunner(processRunner);
        ActionResult result;
        try
        {
            result = await openClawRunner.RunAsync(actionId, correlationId, context, args.ToArray(), events, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new ActionResult(false, Error: ex.Message);
        }
        if (!result.Success)
        {
            if (ShouldFallbackToLegacy(context, result))
            {
                var source = input?.SourcePath ?? context.OpenClawHome;
                var output = input?.BackupPath ?? input?.OutputPath ?? BuildBackupOutputPath(context.BackupDirectory);
                var password = effectivePassword;
                events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Creating backup (fallback)", output));
                await backupService.CreateBackupAsync(source, output, password, input?.Scope).ConfigureAwait(false);
                if (input?.Verify == true)
                {
                    await backupService.VerifySnapshotAsync(output, password).ConfigureAwait(false);
                }

                var fallbackWarnings = new List<WarningItem>
                {
                    new("openclaw-missing", "OpenClaw CLI unavailable; used legacy backup format.")
                };
                if (ignoreEncryption && !string.IsNullOrWhiteSpace(input?.Password))
                {
                    fallbackWarnings.Add(new WarningItem("backup-no-encrypt", "Password ignored because --no-encrypt was set."));
                }
                return new ActionResult(true, Output: output, Warnings: fallbackWarnings);
            }

            return result;
        }

        if (result.Output is not OpenClawCommandSummary summary)
        {
            return result;
        }

        OpenClawBackupCreateSummary parsed;
        try
        {
            parsed = OpenClawBackupParser.ParseCreate(summary);
        }
        catch (Exception ex)
        {
            if (ShouldFallbackToLegacy(context, result))
            {
                var source = input?.SourcePath ?? context.OpenClawHome;
                var output = input?.BackupPath ?? input?.OutputPath ?? BuildBackupOutputPath(context.BackupDirectory);
                var password = effectivePassword;
                events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Creating backup (fallback)", output));
                await backupService.CreateBackupAsync(source, output, password, input?.Scope).ConfigureAwait(false);
                if (input?.Verify == true)
                {
                    await backupService.VerifySnapshotAsync(output, password).ConfigureAwait(false);
                }

                var fallbackWarnings = new List<WarningItem>
                {
                    new("openclaw-missing", "OpenClaw CLI unavailable; used legacy backup format.")
                };
                if (ignoreEncryption && !string.IsNullOrWhiteSpace(input?.Password))
                {
                    fallbackWarnings.Add(new WarningItem("backup-no-encrypt", "Password ignored because --no-encrypt was set."));
                }
                return new ActionResult(true, Output: output, Warnings: fallbackWarnings);
            }

            return new ActionResult(false, Output: summary, Error: ex.Message);
        }

        var warnings = new List<WarningItem>
        {
            new("backup-sensitive", "Backups may contain credentials and secrets. Encrypt before sharing or uploading.")
        };
        if (ignoreEncryption && !string.IsNullOrWhiteSpace(input?.Password))
        {
            warnings.Add(new WarningItem("backup-no-encrypt", "Password ignored because --no-encrypt was set."));
        }

        return new ActionResult(true, Output: parsed, Warnings: warnings);
    }

    private static async Task<ActionResult> RunRestoreLatest(string actionId, Guid correlationId, ActionContext context, BackupRestoreInput? input, IProgress<ActionEvent> events, BackupService backupService, ProcessRunner processRunner, CancellationToken cancellationToken)
    {
        var latest = FindLatestBackup(context.BackupDirectory);
        if (latest == null)
        {
            return new ActionResult(false, Error: "No backups found.");
        }

        return await RunRestoreInternal(actionId, correlationId, latest, input, context, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunRestoreArchive(string actionId, Guid correlationId, ActionContext context, BackupRestoreInput? input, IProgress<ActionEvent> events, BackupService backupService, ProcessRunner processRunner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input?.ArchivePath))
        {
            return new ActionResult(false, Error: "ArchivePath is required.");
        }

        return await RunRestoreInternal(actionId, correlationId, input.ArchivePath, input, context, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunBackupRestore(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupRestoreInput? input,
        IProgress<ActionEvent> events,
        BackupService backupService,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input?.ArchivePath))
        {
            return new ActionResult(false, Error: "ArchivePath is required.");
        }

        return await RunRestoreInternal(actionId, correlationId, input.ArchivePath, input, context, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunOpenClawRestore(
        string actionId,
        Guid correlationId,
        string archivePath,
        string dest,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        List<WarningItem> warnings,
        CancellationToken cancellationToken)
    {
        // OpenClaw backup zips may use compression methods unsupported by .NET's ZipFile.
        // Use PowerShell Expand-Archive on Windows, unzip on other platforms.
        try
        {
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Info", $"Extracting OpenClaw backup to {dest}..."));

            if (!Directory.Exists(dest))
                Directory.CreateDirectory(dest);

            ProcessStartInfo psi;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var script = $"Expand-Archive -LiteralPath '{archivePath.Replace("'", "''")}' -DestinationPath '{dest.Replace("'", "''")}' -Force";
                psi = new ProcessStartInfo("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                psi = new ProcessStartInfo("unzip", $"-o \"{archivePath}\" -d \"{dest}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            using var proc = Process.Start(psi);
            if (proc == null)
                return new ActionResult(false, Error: "Failed to start extraction process.", Warnings: warnings);

            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                return new ActionResult(false, Error: $"Extraction failed (exit {proc.ExitCode}): {stderr.Trim()}", Warnings: warnings);
            }

            warnings.Add(new WarningItem("openclaw-restore", "Restored by extracting OpenClaw backup archive (not ReClaw legacy format)."));
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Info", "OpenClaw backup extracted successfully."));

            return new ActionResult(
                true,
                Output: new RestoreSummary(archivePath, dest, "full", true, null, null, null, null, GetJournalPath(context)),
                Warnings: warnings);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, Error: $"Failed to extract OpenClaw backup: {ex.Message}", Warnings: warnings);
        }
    }

    private static async Task<ActionResult> RunRestoreInternal(string actionId, Guid correlationId, string archivePath, BackupRestoreInput? input, ActionContext context, IProgress<ActionEvent> events, BackupService backupService, ProcessRunner processRunner, CancellationToken cancellationToken)
    {
        var dest = input?.DestinationPath ?? context.OpenClawHome;
        var password = input?.Password;
        var scope = input?.Scope;
        var previewOnly = input?.Preview == true;
        var safeReset = input?.SafeReset == true;
        var resetMode = input?.ResetMode ?? ResetMode.PreserveBackups;
        var warnings = new List<WarningItem>();
        var verifyFirst = input?.VerifyFirst != false;

        // Try ReClaw-format verify; if it fails, try OpenClaw CLI restore instead
        if (verifyFirst)
        {
            try
            {
                await backupService.VerifySnapshotAsync(archivePath, password).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                // Archive is not in ReClaw format — try OpenClaw CLI restore
                events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Info", "Archive is not in ReClaw format; trying OpenClaw restore..."));
                return await RunOpenClawRestore(actionId, correlationId, archivePath, dest, context, events, processRunner, warnings, cancellationToken).ConfigureAwait(false);
            }
        }

        RestorePreview preview;
        try
        {
            preview = await backupService.PreviewRestoreAsync(archivePath, dest, password, scope, skipVerify: true).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // Preview also failed — try OpenClaw CLI restore
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Info", "Archive preview failed; trying OpenClaw restore..."));
            return await RunOpenClawRestore(actionId, correlationId, archivePath, dest, context, events, processRunner, warnings, cancellationToken).ConfigureAwait(false);
        }

        // preview is valid from here
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Impact summary", BuildRestoreImpactSummary(preview)));

        ResetPlan? resetPlan = null;
        if (safeReset)
        {
            resetPlan = BuildResetPlan(context, resetMode);
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Reset plan", $"{resetPlan.DeletePaths.Count} paths"));
        }

        if (previewOnly)
        {
            warnings.Add(new WarningItem("preview-only", "Restore preview only; no changes applied."));
            warnings.Add(new WarningItem("preview-required", "Run without --preview and confirm to apply changes."));
            return new ActionResult(
                true,
                Output: new RestoreSummary(archivePath, dest, preview.Scope, false, preview, null, safeReset ? resetMode : null, resetPlan, GetJournalPath(context)),
                Warnings: warnings);
        }

        if (safeReset && input?.ConfirmReset != true)
        {
            warnings.Add(new WarningItem("confirmation-required", "Reset requires confirmation before restore."));
            warnings.Add(new WarningItem("preview-required", "Review the impact summary and confirm reset to proceed."));
            return new ActionResult(
                false,
                Output: new RestoreSummary(archivePath, dest, preview.Scope, false, preview, null, resetMode, resetPlan, GetJournalPath(context)),
                Error: "Reset requires confirmation. Pass --confirm-reset to proceed.",
                Warnings: warnings);
        }

        string? snapshotPath = null;
        string? createdSnapshotPath = null;
        try
        {
            if (Directory.Exists(dest))
            {
                snapshotPath = BuildPreRestoreSnapshotPath(context.BackupDirectory);
                events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Snapshot", snapshotPath));
                snapshotPath = await CreateOpenClawSnapshotAsync(actionId, correlationId, context, events, backupService, processRunner, snapshotPath, cancellationToken).ConfigureAwait(false);
                createdSnapshotPath = snapshotPath;
            }

            if (safeReset)
            {
                var resetService = backupService.CreateResetService();
                resetPlan ??= BuildResetPlan(context, resetMode);
                events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Resetting", resetMode.ToString()));
                await resetService.ExecuteAsync(resetPlan).ConfigureAwait(false);
                warnings.Add(new WarningItem("safe-reset", $"Safe reset applied ({resetMode})."));
            }

            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Restoring", archivePath));
            await backupService.RestoreAsync(archivePath, dest, password, scope, preview).ConfigureAwait(false);
            return new ActionResult(
                true,
                Output: new RestoreSummary(archivePath, dest, preview.Scope, true, preview, createdSnapshotPath, safeReset ? resetMode : null, resetPlan, GetJournalPath(context)),
                Warnings: warnings);
        }
        catch (Exception ex)
        {
            if (ex is BackupService.RestoreApplyException restoreEx && restoreEx.PartialWrite)
            {
                var detail = restoreEx.OverwroteExisting
                    ? "Restore failed after overwriting existing files; use the rollback snapshot to revert."
                    : restoreEx.CleanupSucceeded
                        ? "Restore failed after writing some files; newly created outputs were cleaned up."
                        : "Restore failed after writing some files; cleanup was incomplete.";
                warnings.Add(new WarningItem("partial-failure", detail));
                if (!restoreEx.CleanupSucceeded)
                {
                    warnings.Add(new WarningItem("cleanup-incomplete", "Restore cleanup did not fully complete; inspect destination before retrying."));
                }
            }

            if (!string.IsNullOrWhiteSpace(createdSnapshotPath))
            {
                warnings.Add(new WarningItem("rollback-available", "Restore failed after snapshot; rollback is available."));
            }

            return new ActionResult(
                false,
                Output: new RestoreSummary(archivePath, dest, preview.Scope, false, preview, createdSnapshotPath, safeReset ? resetMode : null, resetPlan, GetJournalPath(context)),
                Error: ex.Message,
                Warnings: warnings);
        }
    }

    private static async Task<ActionResult> RunDoctor(
        string actionId,
        Guid correlationId,
        ActionContext context,
        DoctorInput? input,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var warnings = new List<WarningItem>();
        var diagnostics = await MaybeExportDiagnosticsAsync(context, input?.ExportDiagnostics, input?.DiagnosticsOutputPath, events, actionId, correlationId, cancellationToken).ConfigureAwait(false);
        if (diagnostics.Warning != null) warnings.Add(diagnostics.Warning);

        var args = BuildDoctorArgs(input);
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Doctor", string.Join(' ', args)));

        var runner = new OpenClawRunner(processRunner);
        var result = await runner.RunAsync(actionId, correlationId, context, args, events, cancellationToken).ConfigureAwait(false);
        var summary = result.Output as OpenClawCommandSummary
            ?? new OpenClawCommandSummary(
                string.Join(' ', args),
                result.ExitCode ?? -1,
                false,
                Array.Empty<string>(),
                Array.Empty<string>(),
                0,
                0,
                false);

        return new ActionResult(
            result.Success,
            Output: new DoctorSummary(summary, diagnostics.Path, GetJournalPath(context)),
            Error: result.Error,
            ExitCode: result.ExitCode,
            Warnings: warnings);
    }

    private static async Task<ActionResult> RunFix(
        string actionId,
        Guid correlationId,
        ActionContext context,
        FixInput? input,
        IProgress<ActionEvent> events,
        BackupService backupService,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var warnings = new List<WarningItem>();
        var snapshotPath = input?.SnapshotPath ?? BuildPreFixSnapshotPath(context.BackupDirectory);
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Snapshot", snapshotPath));
        snapshotPath = await CreateOpenClawSnapshotAsync(actionId, correlationId, context, events, backupService, processRunner, snapshotPath, cancellationToken).ConfigureAwait(false);

        var diagnostics = await MaybeExportDiagnosticsAsync(context, input?.ExportDiagnostics, input?.DiagnosticsOutputPath, events, actionId, correlationId, cancellationToken).ConfigureAwait(false);
        if (diagnostics.Warning != null) warnings.Add(diagnostics.Warning);

        var args = BuildFixArgs(input);
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Fix", string.Join(' ', args)));

        var runner = new OpenClawRunner(processRunner);
        var result = await runner.RunAsync(actionId, correlationId, context, args, events, cancellationToken).ConfigureAwait(false);
        var summary = result.Output as OpenClawCommandSummary
            ?? new OpenClawCommandSummary(
                string.Join(' ', args),
                result.ExitCode ?? -1,
                false,
                Array.Empty<string>(),
                Array.Empty<string>(),
                0,
                0,
                false);

        if (!result.Success)
        {
            warnings.Add(new WarningItem("rollback-available", "Fix failed after snapshot; rollback is available."));
        }

        return new ActionResult(
            result.Success,
            Output: new FixSummary(summary, snapshotPath, diagnostics.Path, GetJournalPath(context)),
            Error: result.Error,
            ExitCode: result.ExitCode,
            Warnings: warnings);
    }

    private static async Task<ActionResult> RunRecover(
        string actionId,
        Guid correlationId,
        ActionContext context,
        RecoverInput? input,
        IProgress<ActionEvent> events,
        BackupService backupService,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var warnings = new List<WarningItem>();
        var diagnostics = await MaybeExportDiagnosticsAsync(context, input?.ExportDiagnostics, input?.DiagnosticsOutputPath, events, actionId, correlationId, cancellationToken).ConfigureAwait(false);
        if (diagnostics.Warning != null) warnings.Add(diagnostics.Warning);
        var steps = new List<RecoveryStep>();

        string? archive;
        if (!string.IsNullOrWhiteSpace(input?.ArchivePath))
        {
            archive = input.ArchivePath;
        }
        else
        {
            archive = await FindLatestValidBackupAsync(context.BackupDirectory, backupService, input?.Password).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(archive))
        {
            return new ActionResult(
                false,
                Output: new RecoverSummary(null, input?.DestinationPath ?? context.OpenClawHome, input?.Scope, false, null, null, null, diagnostics.Path, steps, "restore-or-clean-install", GetJournalPath(context)),
                Error: "No valid backup found. Provide --snapshot to select the archive to recover.",
                Warnings: warnings);
        }

        if (input?.Preview == true)
        {
            var preview = await backupService.PreviewRestoreAsync(archive, input?.DestinationPath ?? context.OpenClawHome, input?.Password, input?.Scope).ConfigureAwait(false);
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Impact summary", BuildRestoreImpactSummary(preview)));
            var previewSummary = new RestoreSummary(
                archive,
                input?.DestinationPath ?? context.OpenClawHome,
                preview.Scope,
                false,
                preview,
                null,
                input?.SafeReset == true ? input?.ResetMode ?? ResetMode.PreserveBackups : null,
                null,
                GetJournalPath(context));
            steps.Add(new RecoveryStep("restore", "preview", null, "confirm-reset"));
            warnings.Add(new WarningItem("preview-only", "Recovery preview only; no changes applied."));
            warnings.Add(new WarningItem("preview-required", "Run without --preview and confirm to apply recovery."));
            return new ActionResult(
                true,
                Output: new RecoverSummary(archive, previewSummary.DestinationPath, previewSummary.Scope, false, previewSummary, null, null, diagnostics.Path, steps, "confirm-reset", GetJournalPath(context)),
                Warnings: warnings);
        }

        if (input?.SafeReset == true && input?.ConfirmReset != true)
        {
            var preview = await backupService.PreviewRestoreAsync(archive, input?.DestinationPath ?? context.OpenClawHome, input?.Password, input?.Scope).ConfigureAwait(false);
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Impact summary", BuildRestoreImpactSummary(preview)));
            var previewSummary = new RestoreSummary(
                archive,
                input?.DestinationPath ?? context.OpenClawHome,
                preview.Scope,
                false,
                preview,
                null,
                input?.ResetMode ?? ResetMode.PreserveBackups,
                null,
                GetJournalPath(context));
            steps.Add(new RecoveryStep("restore", "blocked", "Confirmation required for reset.", "confirm-reset"));
            warnings.Add(new WarningItem("confirmation-required", "Reset requires confirmation before recovery."));
            warnings.Add(new WarningItem("preview-required", "Review the impact summary and confirm reset to proceed."));
            return new ActionResult(
                false,
                Output: new RecoverSummary(archive, previewSummary.DestinationPath, previewSummary.Scope, false, previewSummary, null, null, diagnostics.Path, steps, "confirm-reset", GetJournalPath(context)),
                Error: "Reset requires confirmation. Pass --confirm-reset to proceed.",
                Warnings: warnings);
        }

        DoctorSummary? doctorSummary = null;
        if (input?.RunDoctor != false)
        {
            var doctorResult = await RunDoctor(actionId, correlationId, context, new DoctorInput(ExportDiagnostics: false), events, processRunner, cancellationToken).ConfigureAwait(false);
            doctorSummary = doctorResult.Output as DoctorSummary;
            if (!doctorResult.Success && !string.IsNullOrWhiteSpace(doctorResult.Error))
            {
                events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Doctor warning", doctorResult.Error));
                warnings.Add(new WarningItem("doctor-failed", doctorResult.Error));
                steps.Add(new RecoveryStep("doctor", "failed", doctorResult.Error, "fix"));
            }
            else if (doctorResult.Success)
            {
                steps.Add(new RecoveryStep("doctor", "success", null, input?.RunFix != false ? "fix" : "restore"));
            }
        }
        else
        {
            steps.Add(new RecoveryStep("doctor", "skipped", null, input?.RunFix != false ? "fix" : "restore"));
        }

        FixSummary? fixSummary = null;
        if (input?.RunFix != false)
        {
            var fixResult = await RunFix(actionId, correlationId, context, new FixInput(input?.Password), events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
            fixSummary = fixResult.Output as FixSummary;
            if (!fixResult.Success && !string.IsNullOrWhiteSpace(fixResult.Error))
            {
                events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Fix warning", fixResult.Error));
                warnings.Add(new WarningItem("fix-failed", fixResult.Error));
                steps.Add(new RecoveryStep("fix", "failed", fixResult.Error, "restore"));
            }
            else if (fixResult.Success)
            {
                steps.Add(new RecoveryStep("fix", "success", null, "restore"));
            }
        }
        else
        {
            steps.Add(new RecoveryStep("fix", "skipped", null, "restore"));
        }

        var restoreInput = new BackupRestoreInput(
            archive,
            input?.DestinationPath,
            input?.Password,
            input?.Scope,
            Preview: false,
            SafeReset: input?.SafeReset ?? true,
            ResetMode: input?.ResetMode ?? ResetMode.PreserveBackups,
            ConfirmReset: input?.ConfirmReset ?? false);

        var restoreResult = await RunRestoreInternal(actionId, correlationId, archive, restoreInput, context, events, backupService, processRunner, cancellationToken).ConfigureAwait(false);
        var restoreSummary = restoreResult.Output as RestoreSummary;
        if (restoreSummary != null)
        {
            steps.Add(new RecoveryStep("restore", restoreResult.Success ? "success" : "failed", restoreResult.Error, restoreResult.Success ? null : "clean-install"));
        }

        if (restoreResult.Warnings != null && restoreResult.Warnings.Count > 0)
        {
            warnings.AddRange(restoreResult.Warnings);
        }

        return new ActionResult(
            restoreResult.Success,
            Output: new RecoverSummary(archive, restoreInput.DestinationPath ?? context.OpenClawHome, restoreInput.Scope, restoreResult.Success && restoreSummary?.Applied == true, restoreSummary, doctorSummary, fixSummary, diagnostics.Path, steps, restoreResult.Success ? null : "clean-install", GetJournalPath(context)),
            Error: restoreResult.Error,
            ExitCode: restoreResult.ExitCode,
            Warnings: warnings);
    }

    private static async Task<ActionResult> RunOpenClawRebuild(
        string actionId,
        Guid correlationId,
        ActionContext context,
        OpenClawRebuildInput? input,
        IProgress<ActionEvent> events,
        BackupService backupService,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var service = new OpenClawRebuildService(
            new OpenClawActionRunner(processRunner),
            backupService,
            processRunner,
            OpenClawLocator.EnumerateCandidates);

        return await service.RebuildAsync(
            actionId,
            correlationId,
            context,
            input ?? new OpenClawRebuildInput(),
            events,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunDiagnosticsExport(
        string actionId,
        Guid correlationId,
        ActionContext context,
        DiagnosticsExportInput? input,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var service = new DiagnosticsBundleService();
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Diagnostics", "Collecting"));
        var summary = await service.CreateBundleAsync(context, input?.OutputPath, cancellationToken).ConfigureAwait(false);
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Diagnostics", summary.BundlePath));
        return new ActionResult(true, Output: summary with { JournalPath = GetJournalPath(context) });
    }

    private static async Task<ActionResult> RunGatewayCommand(
        string actionId,
        Guid correlationId,
        ActionContext context,
        string[] args,
        IProgress<ActionEvent> events,
        BackupService backupService,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var repairService = new GatewayRepairService(
            new OpenClawActionRunner(processRunner),
            backupService,
            OpenClawLocator.EnumerateCandidates);

        return await repairService.RunWithRepairAsync(
            actionId,
            correlationId,
            context,
            args,
            events,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ActionResult> RunGatewayTroubleshoot(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var runner = new OpenClawRunner(processRunner);
        OpenClawCommandSummary? statusSummary = null;
        OpenClawCommandSummary? gatewayStatusSummary = null;
        OpenClawCommandSummary? logsSummary = null;
        OpenClawCommandSummary? doctorSummary = null;
        OpenClawCommandSummary? channelsSummary = null;
        var warnings = new List<WarningItem>();

        var statusResult = await runner.RunAsync(
            "gateway-troubleshoot-status",
            correlationId,
            context,
            new[] { "status" },
            events,
            cancellationToken).ConfigureAwait(false);
        statusSummary = statusResult.Output as OpenClawCommandSummary;
        if (!statusResult.Success && !string.IsNullOrWhiteSpace(statusResult.Error))
        {
            warnings.Add(new WarningItem("status-failed", statusResult.Error));
        }

        var gatewayStatusResult = await runner.RunAsync(
            "gateway-troubleshoot-gateway-status",
            correlationId,
            context,
            new[] { "gateway", "status" },
            events,
            cancellationToken).ConfigureAwait(false);
        gatewayStatusSummary = gatewayStatusResult.Output as OpenClawCommandSummary;
        if (!gatewayStatusResult.Success && !string.IsNullOrWhiteSpace(gatewayStatusResult.Error))
        {
            warnings.Add(new WarningItem("gateway-status-failed", gatewayStatusResult.Error));
        }

        logsSummary = await RunGatewayLogsSampleAsync(
            "gateway-troubleshoot-logs",
            correlationId,
            context,
            events,
            processRunner,
            cancellationToken).ConfigureAwait(false);

        var doctorResult = await runner.RunAsync(
            "gateway-troubleshoot-doctor",
            correlationId,
            context,
            new[] { "doctor", "--non-interactive", "--yes" },
            events,
            cancellationToken).ConfigureAwait(false);
        doctorSummary = doctorResult.Output as OpenClawCommandSummary;
        if (!doctorResult.Success && !string.IsNullOrWhiteSpace(doctorResult.Error))
        {
            warnings.Add(new WarningItem("doctor-failed", doctorResult.Error));
        }

        var channelsResult = await runner.RunAsync(
            "gateway-troubleshoot-channels-probe",
            correlationId,
            context,
            new[] { "channels", "status", "--probe" },
            events,
            cancellationToken).ConfigureAwait(false);
        channelsSummary = channelsResult.Output as OpenClawCommandSummary;
        if (!channelsResult.Success && !string.IsNullOrWhiteSpace(channelsResult.Error))
        {
            warnings.Add(new WarningItem("channels-probe-failed", channelsResult.Error));
        }

        var gatewayModeReason = TryFindGatewayModeUnsetReason(doctorSummary)
            ?? TryFindGatewayModeUnsetReason(gatewayStatusSummary)
            ?? TryFindGatewayModeUnsetReason(statusSummary)
            ?? TryFindGatewayModeUnsetReason(logsSummary);
        if (!string.IsNullOrWhiteSpace(gatewayModeReason))
        {
            warnings.Add(new WarningItem("gateway-mode-unset", gatewayModeReason));
        }

        var gatewayHealthy = IsGatewayHealthy(gatewayStatusSummary);
        var startupReason = ExtractStartupReason(logsSummary, doctorSummary, gatewayStatusSummary, statusSummary);
        var output = new GatewayTroubleshootSummary(
            statusSummary,
            gatewayStatusSummary,
            logsSummary,
            doctorSummary,
            channelsSummary,
            startupReason,
            gatewayHealthy);

        var error = gatewayHealthy ? null : "Gateway not healthy.";
        return new ActionResult(gatewayHealthy, Output: output, Error: error, Warnings: warnings);
    }

    private static async Task<OpenClawCommandSummary?> RunGatewayLogsSampleAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        try
        {
            var (command, spec, commandLine) = OpenClawRunner.BuildRunSpec(context, new[] { "logs", "--follow" });
            spec = spec with { Timeout = TimeSpan.FromSeconds(8) };
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Command", commandLine));
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Executable", spec.FileName));
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Arguments", string.Join(' ', spec.Arguments)));
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "WorkingDir", spec.WorkingDirectory ?? "(null)"));
            var result = await processRunner.RunAsync(actionId, correlationId, spec, events, cancellationToken).ConfigureAwait(false);
            return new OpenClawCommandSummary(
                commandLine,
                result.ExitCode,
                result.TimedOut,
                result.StdOut,
                result.StdErr,
                result.StdOutLineCount,
                result.StdErrLineCount,
                result.OutputTruncated);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsGatewayHealthy(OpenClawCommandSummary? summary)
    {
        if (summary == null)
        {
            return false;
        }

        var lines = summary.StdOut.Concat(summary.StdErr)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        var runtimeRunning = lines.Any(line =>
            line.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
            && line.Contains("running", StringComparison.OrdinalIgnoreCase));
        var rpcOk = lines.Any(line =>
            line.Contains("RPC probe", StringComparison.OrdinalIgnoreCase)
            && line.Contains("ok", StringComparison.OrdinalIgnoreCase));
        return runtimeRunning && rpcOk;
    }

    private static string? BuildGatewayStatusSnapshot(OpenClawCommandSummary? summary)
    {
        if (summary == null)
        {
            return null;
        }

        var lines = summary.StdOut.Concat(summary.StdErr)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        var runtimeLine = lines.FirstOrDefault(line => line.StartsWith("Runtime", StringComparison.OrdinalIgnoreCase));
        var rpcLine = lines.FirstOrDefault(line => line.StartsWith("RPC probe", StringComparison.OrdinalIgnoreCase));
        var serviceLine = lines.FirstOrDefault(line => line.StartsWith("Service", StringComparison.OrdinalIgnoreCase));

        var parts = new[] { runtimeLine, rpcLine, serviceLine }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        return parts.Length == 0 ? null : string.Join(" | ", parts);
    }

    private static int ResolveGatewayPort(OpenClawCommandSummary? summary)
    {
        var envPort = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_PORT");
        if (int.TryParse(envPort, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        if (summary != null)
        {
            var regex = new Regex(@"\\bport=?\\s*(\\d{2,5})\\b", RegexOptions.IgnoreCase);
            foreach (var line in summary.StdOut.Concat(summary.StdErr))
            {
                var match = regex.Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var port) && port > 0)
                {
                    return port;
                }
            }
        }

        return 18789;
    }

    private static async Task<bool> ProbeGatewayHealthAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(1200));
            var response = await Http.GetAsync($"http://127.0.0.1:{port}/healthz", cts.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            return code >= 200 && code < 500;
        }
        catch
        {
            return false;
        }
    }

    internal static string? ExtractStartupReason(
        OpenClawCommandSummary? logsSummary,
        OpenClawCommandSummary? doctorSummary,
        OpenClawCommandSummary? gatewayStatusSummary,
        OpenClawCommandSummary? statusSummary)
    {
        var gatewayModeReason = TryFindGatewayModeUnsetReason(doctorSummary)
            ?? TryFindGatewayModeUnsetReason(gatewayStatusSummary)
            ?? TryFindGatewayModeUnsetReason(statusSummary)
            ?? TryFindGatewayModeUnsetReason(logsSummary);
        if (!string.IsNullOrWhiteSpace(gatewayModeReason))
        {
            return gatewayModeReason;
        }

        var reason = TryGetReasonFrom(logsSummary)
            ?? TryGetReasonFrom(doctorSummary)
            ?? TryGetReasonFrom(gatewayStatusSummary)
            ?? TryGetReasonFrom(statusSummary);

        if (!string.IsNullOrWhiteSpace(reason))
        {
            return TrimReason(reason);
        }

        var fallback = GetLastNonEmptyLine(logsSummary)
            ?? GetLastNonEmptyLine(doctorSummary)
            ?? GetLastNonEmptyLine(gatewayStatusSummary)
            ?? GetLastNonEmptyLine(statusSummary);
        return TrimReason(fallback);
    }

    private static string? TryGetReasonFrom(OpenClawCommandSummary? summary)
    {
        if (summary == null)
        {
            return null;
        }

        string? lastReason = null;
        foreach (var line in summary.StdErr.Concat(summary.StdOut))
        {
            if (IsReasonLine(line))
            {
                lastReason = line.Trim();
            }
        }

        return lastReason;
    }

    private static string? TryFindGatewayModeUnsetReason(OpenClawCommandSummary? summary)
    {
        if (summary == null)
        {
            return null;
        }

        foreach (var line in summary.StdOut.Concat(summary.StdErr))
        {
            if (IsGatewayModeUnsetLine(line))
            {
                return GatewayModeUnsetMessage;
            }
        }

        return null;
    }

    private static bool IsGatewayModeUnsetLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var lower = line.ToLowerInvariant();
        var mentionsMode = lower.Contains("gateway.mode") || lower.Contains("gateway mode");
        var mentionsUnset = lower.Contains("unset") || lower.Contains("not set") || lower.Contains("missing");
        var mentionsBlocked = lower.Contains("blocked") || lower.Contains("will be blocked");
        return mentionsMode && mentionsUnset && mentionsBlocked;
    }

    private static bool IsReasonLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var lower = line.ToLowerInvariant();
        return lower.Contains("error")
            || lower.Contains("fatal")
            || lower.Contains("failed")
            || lower.Contains("refusing")
            || lower.Contains("blocked")
            || lower.Contains("eaddrinuse")
            || lower.Contains("permission")
            || lower.Contains("unauthorized")
            || lower.Contains("unable")
            || lower.Contains("panic")
            || lower.Contains("exception")
            || lower.Contains("not found")
            || lower.Contains("missing")
            || lower.Contains("bind");
    }

    private static string? GetLastNonEmptyLine(OpenClawCommandSummary? summary)
    {
        if (summary == null)
        {
            return null;
        }

        foreach (var line in summary.StdOut.Concat(summary.StdErr).Reverse())
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line.Trim();
            }
        }

        return null;
    }

    private static string? TrimReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        return reason.Length <= 260 ? reason : reason[..260];
    }

    private static async Task<ActionResult> RunOpenClawTerminal(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var repoRoot = OpenClawLocator.ResolveRepoRoot(context);
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return new ActionResult(false, Error: "OpenClaw repo not found. Set OPENCLAW_REPO or OPENCLAW_ENTRY.");
        }

        var headless = IsTerminalHeadless();
        var (fileName, args) = BuildTerminalCommand(repoRoot, headless);
        var spec = new ProcessRunSpec(fileName, args, repoRoot);
        var terminalTimeoutSeconds = Environment.GetEnvironmentVariable("RECLAW_OPENCLAW_TERMINAL_TIMEOUT_SECONDS");
        if (!string.IsNullOrWhiteSpace(terminalTimeoutSeconds)
            && int.TryParse(terminalTimeoutSeconds, out var terminalSeconds)
            && terminalSeconds > 0)
        {
            spec = spec with { Timeout = TimeSpan.FromSeconds(terminalSeconds) };
        }
        var commandLine = string.Join(' ', new[] { fileName }.Concat(args));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Command", $"{commandLine} (cwd: {repoRoot})"));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Executable", spec.FileName));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Arguments", string.Join(' ', spec.Arguments)));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "WorkingDir", spec.WorkingDirectory ?? "(null)"));
        var result = await processRunner.RunAsync(actionId, correlationId, spec, events, cancellationToken).ConfigureAwait(false);

        var summary = new OpenClawCommandSummary(
            commandLine,
            result.ExitCode,
            result.TimedOut,
            result.StdOut,
            result.StdErr,
            result.StdOutLineCount,
            result.StdErrLineCount,
            result.OutputTruncated);

        if (result.TimedOut)
        {
            return new ActionResult(false, Output: summary, Error: "Terminal launch timed out.", ExitCode: result.ExitCode);
        }

        return result.ExitCode == 0
            ? new ActionResult(true, Output: summary, ExitCode: result.ExitCode)
            : new ActionResult(false, Output: summary, Error: $"Terminal launch exited with code {result.ExitCode}", ExitCode: result.ExitCode);
    }

    private static ActionResult RunOpenClawCleanup(
        string actionId,
        Guid correlationId,
        ActionContext context,
        OpenClawCleanupInput? input,
        IProgress<ActionEvent> events)
    {
        var inventory = OpenClawLocator.BuildInventory(context);
        var artifacts = OpenClawLocator.ScanArtifacts(context, inventory);
        var warnings = new List<WarningItem>();

        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Cleanup scan", $"{artifacts.Count} artifacts"));

        var apply = input?.Apply == true;
        if (!apply)
        {
            warnings.Add(new WarningItem("preview-only", "Cleanup preview only; no changes applied."));
            warnings.Add(new WarningItem("preview-required", "Pass --apply and confirm to clean safe items."));
            return new ActionResult(
                true,
                Output: new OpenClawCleanupSummary(artifacts, Array.Empty<string>(), false, GetJournalPath(context)),
                Warnings: warnings);
        }

        if (input?.Confirm != true)
        {
            warnings.Add(new WarningItem("confirmation-required", "Cleanup requires confirmation before deleting files."));
            warnings.Add(new WarningItem("preview-required", "Review the cleanup candidates and confirm to proceed."));
            return new ActionResult(
                false,
                Output: new OpenClawCleanupSummary(artifacts, Array.Empty<string>(), false, GetJournalPath(context)),
                Error: "Cleanup requires confirmation. Pass --confirm to proceed.",
                Warnings: warnings);
        }

        var removed = new List<string>();
        foreach (var artifact in artifacts.Where(a => a.IsSafeToClean))
        {
            try
            {
                if (File.Exists(artifact.Path))
                {
                    File.Delete(artifact.Path);
                    removed.Add(artifact.Path);
                }
                else if (Directory.Exists(artifact.Path))
                {
                    Directory.Delete(artifact.Path, true);
                    removed.Add(artifact.Path);
                }
            }
            catch (Exception ex)
            {
                warnings.Add(new WarningItem("cleanup-failed", $"Failed to remove {artifact.Path}: {ex.Message}"));
            }
        }

        return new ActionResult(
            true,
            Output: new OpenClawCleanupSummary(artifacts, removed, true, GetJournalPath(context)),
            Warnings: warnings);
    }

    private static (string FileName, string[] Args) BuildTerminalCommand(string repoRoot, bool headless)
    {
        if (OperatingSystem.IsWindows())
        {
            if (headless)
            {
                var escaped = EscapePowerShellLiteral(repoRoot);
                return ("powershell.exe", new[]
                {
                    "-NoProfile",
                    "-Command",
                    $"Set-Location -LiteralPath '{escaped}'; Write-Output 'OpenClaw terminal ready.'"
                });
            }
            return ("cmd.exe", new[] { "/c", "start", "\"\"", "cmd.exe", "/k", $"cd /d \"{repoRoot}\"" });
        }

        if (OperatingSystem.IsMacOS())
        {
            if (headless)
            {
                return ("sh", new[] { "-lc", $"cd \"{repoRoot}\" && echo OpenClaw terminal ready." });
            }
            return ("open", new[] { "-a", "Terminal", repoRoot });
        }

        if (headless)
        {
            return ("sh", new[] { "-lc", $"cd \"{repoRoot}\" && echo OpenClaw terminal ready." });
        }

        return ("x-terminal-emulator", new[] { "--working-directory", repoRoot });
    }

    private static bool IsTerminalHeadless()
    {
        var env = Environment.GetEnvironmentVariable("RECLAW_OPENCLAW_TERMINAL_HEADLESS");
        return string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapePowerShellLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static async Task<ActionResult> RunRollback(
        string actionId,
        Guid correlationId,
        ActionContext context,
        RollbackInput? input,
        IProgress<ActionEvent> events,
        BackupService backupService)
    {
        if (string.IsNullOrWhiteSpace(input?.SnapshotPath))
        {
            return new ActionResult(false, Error: "SnapshotPath is required.");
        }

        var dest = input.DestinationPath ?? context.OpenClawHome;
        var password = input.Password;
        var scope = input.Scope;
        var warnings = new List<WarningItem>();

        var preview = await backupService.PreviewRestoreAsync(input.SnapshotPath, dest, password, scope).ConfigureAwait(false);
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Impact summary", BuildRestoreImpactSummary(preview)));

        if (input.Preview)
        {
            warnings.Add(new WarningItem("preview-only", "Rollback preview only; no changes applied."));
            warnings.Add(new WarningItem("preview-required", "Run without --preview and confirm to apply rollback."));
            return new ActionResult(
                true,
                Output: new RollbackSummary(input.SnapshotPath, dest, preview.Scope, false, preview, GetJournalPath(context)),
                Warnings: warnings);
        }

        if (!input.ConfirmRollback)
        {
            warnings.Add(new WarningItem("confirmation-required", "Rollback requires confirmation before restore."));
            warnings.Add(new WarningItem("preview-required", "Review the impact summary and confirm rollback to proceed."));
            return new ActionResult(
                false,
                Output: new RollbackSummary(input.SnapshotPath, dest, preview.Scope, false, preview, GetJournalPath(context)),
                Error: "Rollback requires confirmation. Pass --confirm-rollback to proceed.",
                Warnings: warnings);
        }

        try
        {
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Rolling back", input.SnapshotPath));
            await backupService.RestoreAsync(input.SnapshotPath, dest, password, scope, preview).ConfigureAwait(false);
            return new ActionResult(
                true,
                Output: new RollbackSummary(input.SnapshotPath, dest, preview.Scope, true, preview, GetJournalPath(context)),
                Warnings: warnings);
        }
        catch (Exception ex)
        {
            if (ex is BackupService.RestoreApplyException restoreEx && restoreEx.PartialWrite)
            {
                var detail = restoreEx.OverwroteExisting
                    ? "Rollback failed after overwriting existing files; destination may need manual inspection."
                    : restoreEx.CleanupSucceeded
                        ? "Rollback failed after writing some files; newly created outputs were cleaned up."
                        : "Rollback failed after writing some files; cleanup was incomplete.";
                warnings.Add(new WarningItem("partial-failure", detail));
                if (!restoreEx.CleanupSucceeded)
                {
                    warnings.Add(new WarningItem("cleanup-incomplete", "Rollback cleanup did not fully complete; inspect destination before retrying."));
                }
            }

            return new ActionResult(
                false,
                Output: new RollbackSummary(input.SnapshotPath, dest, preview.Scope, false, preview, GetJournalPath(context)),
                Error: ex.Message,
                Warnings: warnings);
        }
    }

    private static async Task<ActionResult> RunReset(
        string actionId,
        Guid correlationId,
        ActionContext context,
        ResetInput? input,
        IProgress<ActionEvent> events,
        BackupService backupService,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var mode = input?.Mode ?? ResetMode.PreserveBackups;
        var resetPlan = BuildResetPlan(context, mode);
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Reset plan", $"{resetPlan.DeletePaths.Count} paths"));

        var warnings = new List<WarningItem>();
        if (input?.Preview == true)
        {
            warnings.Add(new WarningItem("preview-only", "Reset preview only; no changes applied."));
            warnings.Add(new WarningItem("preview-required", "Run without --preview and confirm to apply reset."));
            return new ActionResult(
                true,
                Output: new ResetSummary(mode, resetPlan, false, GetJournalPath(context)),
                Warnings: warnings);
        }

        if (input?.Confirm != true)
        {
            warnings.Add(new WarningItem("confirmation-required", "Reset requires confirmation before applying changes."));
            warnings.Add(new WarningItem("preview-required", "Review the reset plan and confirm to proceed."));
            return new ActionResult(
                false,
                Output: new ResetSummary(mode, resetPlan, false, GetJournalPath(context)),
                Error: "Reset requires confirmation. Pass --confirm to proceed.",
                Warnings: warnings);
        }

        var snapshotPath = BuildPreResetSnapshotPath(context.BackupDirectory);
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Snapshot", snapshotPath));
        snapshotPath = await CreateOpenClawSnapshotAsync(actionId, correlationId, context, events, backupService, processRunner, snapshotPath, cancellationToken).ConfigureAwait(false);
        warnings.Add(new WarningItem("rollback-available", $"Snapshot created before reset: {snapshotPath}"));

        var resetService = backupService.CreateResetService();
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Resetting", mode.ToString()));
        await resetService.ExecuteAsync(resetPlan).ConfigureAwait(false);
        return new ActionResult(
            true,
            Output: new ResetSummary(mode, resetPlan, true, GetJournalPath(context)),
            Warnings: warnings);
    }

    private static ActionResult RunBackupList(string actionId, Guid correlationId, ActionContext context, IProgress<ActionEvent> events)
    {
        var entries = Directory.Exists(context.BackupDirectory)
            ? Directory.GetFiles(context.BackupDirectory, "*.tar.gz*").OrderByDescending(File.GetLastWriteTimeUtc).ToArray()
            : Array.Empty<string>();

        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Found backups", entries.Length.ToString(CultureInfo.InvariantCulture)));
        return new ActionResult(true, Output: entries);
    }

    private static ActionResult RunBackupPrunePlan(string actionId, Guid correlationId, ActionContext context, BackupPruneInput? input, IProgress<ActionEvent> events)
    {
        var keepLast = input?.KeepLast ?? 5;
        var olderThan = ParseAge(input?.OlderThan ?? "30d");
        var cutoff = DateTimeOffset.UtcNow - olderThan;

        var all = Directory.Exists(context.BackupDirectory)
            ? Directory.GetFiles(context.BackupDirectory, "*.tar.gz*").OrderByDescending(File.GetLastWriteTimeUtc).ToList()
            : new List<string>();

        var keep = all.Take(keepLast).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prune = all.Where(path => !keep.Contains(path) && File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime).ToArray();

        if (input?.DryRun == false)
        {
            var removed = 0;
            foreach (var path in prune)
            {
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch
                {
                    // ignore delete errors
                }
            }

            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Prune applied", $"{removed} removed"));
            return new ActionResult(true, Output: prune);
        }

        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Prune plan", $"{prune.Length} candidates"));
        return new ActionResult(true, Output: prune);
    }

    private static ActionResult RunStatus(string actionId, Guid correlationId, ActionContext context, IProgress<ActionEvent> events)
    {
        var backupCount = Directory.Exists(context.BackupDirectory)
            ? Directory.GetFiles(context.BackupDirectory, "*.tar.gz*").Length
            : 0;

        var summary = new StatusSummary(
            context.OpenClawHome,
            context.ConfigDirectory,
            context.DataDirectory,
            context.BackupDirectory,
            context.LogsDirectory,
            context.TempDirectory,
            context.OpenClawExecutable,
            context.OpenClawEntry,
            backupCount,
            Directory.Exists(context.OpenClawHome),
            Directory.Exists(context.ConfigDirectory),
            Directory.Exists(context.DataDirectory),
            Directory.Exists(context.BackupDirectory),
            GetJournalPath(context));

        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Status", $"backups={backupCount}"));
        return new ActionResult(true, Output: summary);
    }

    private static async Task<ActionResult> RunBackupVerify(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupVerifyInput? input,
        IProgress<ActionEvent> events,
        BackupService backupService,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var target = input?.ArchivePath ?? FindLatestBackup(context.BackupDirectory);
        if (string.IsNullOrWhiteSpace(target))
        {
            return new ActionResult(false, Error: "No backup found to verify.");
        }

        if (IsReClawEncryptedArchive(target))
        {
            var legacySummary = await backupService.VerifySnapshotAsync(target, input?.Password).ConfigureAwait(false);
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Verified", $"{legacySummary.EntryCount} entries"));
            return new ActionResult(true, Output: legacySummary);
        }

        var openClawRunner = new OpenClawRunner(processRunner);
        ActionResult openClawResult;
        try
        {
            openClawResult = await openClawRunner.RunAsync(
                actionId,
                correlationId,
                context,
                new[] { "backup", "verify", target, "--json" },
                events,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            openClawResult = new ActionResult(false, Error: ex.Message);
        }

        if (openClawResult.Success && openClawResult.Output is OpenClawCommandSummary summary)
        {
            try
            {
                var parsed = OpenClawBackupParser.ParseVerify(summary);
                events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Verified", $"{parsed.EntryCount} entries"));
                return new ActionResult(true, Output: parsed);
            }
            catch (Exception ex)
            {
                return new ActionResult(false, Output: summary, Error: ex.Message);
            }
        }

        var fallback = await backupService.VerifySnapshotAsync(target, input?.Password).ConfigureAwait(false);
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Verified", $"{fallback.EntryCount} entries"));
        return new ActionResult(true, Output: fallback);
    }

    private static async Task<ActionResult> RunBackupExport(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupExportInput? input,
        IProgress<ActionEvent> events,
        BackupService backupService,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var scope = input?.Scope ?? "config+creds+sessions";
        var encrypt = input?.Encrypt ?? true;
        var password = input?.Password;
        var warnings = new List<WarningItem>();
        if (encrypt && string.IsNullOrWhiteSpace(password))
        {
            return new ActionResult(false, Error: "Password is required for encrypted export.");
        }
        if (!encrypt)
        {
            warnings.Add(new WarningItem("export-insecure", "Export is unencrypted. Avoid sharing this archive over insecure channels."));
        }

        var outputPath = input?.OutputPath;
        var tempDir = Path.Combine(context.TempDirectory, $"reclaw_export_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var openClawRunner = new OpenClawRunner(processRunner);
        var args = new List<string> { "backup", "create", "--json" };
        if (input?.Verify == true)
        {
            args.Add("--verify");
        }

        if (ScopeIsConfigOnly(scope))
        {
            args.Add("--only-config");
        }
        else if (!ScopeIncludesWorkspace(scope))
        {
            args.Add("--no-include-workspace");
        }

        args.Add("--output");
        args.Add(tempDir);

        ActionResult openClawResult;
        try
        {
            openClawResult = await openClawRunner.RunAsync(
                actionId,
                correlationId,
                context,
                args.ToArray(),
                events,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            openClawResult = new ActionResult(false, Error: ex.Message);
        }

        OpenClawBackupCreateSummary parsed = default!;
        string rawArchive = string.Empty;
        var useLegacy = false;

        if (!openClawResult.Success)
        {
            useLegacy = ShouldFallbackToLegacy(context, openClawResult);
            if (!useLegacy)
            {
                return openClawResult;
            }
        }
        else if (openClawResult.Output is not OpenClawCommandSummary summary)
        {
            useLegacy = ShouldFallbackToLegacy(context, openClawResult);
            if (!useLegacy)
            {
                return openClawResult;
            }
        }
        else
        {
            try
            {
                parsed = OpenClawBackupParser.ParseCreate(summary);
                rawArchive = parsed.ArchivePath;
                useLegacy = false;
            }
            catch (Exception ex)
            {
                useLegacy = ShouldFallbackToLegacy(context, openClawResult);
                if (!useLegacy)
                {
                    return new ActionResult(false, Output: summary, Error: ex.Message);
                }
            }
        }

        if (useLegacy)
        {
            rawArchive = BuildBackupOutputPath(tempDir);
            await backupService.CreateBackupAsync(context.OpenClawHome, rawArchive, null, scope).ConfigureAwait(false);
            if (input?.Verify == true)
            {
                await backupService.VerifySnapshotAsync(rawArchive).ConfigureAwait(false);
            }

            parsed = new OpenClawBackupCreateSummary(
                DateTimeOffset.UtcNow.ToString("O"),
                tempDir,
                rawArchive,
                false,
                IncludeWorkspace: ScopeIncludesWorkspace(scope),
                OnlyConfig: ScopeIsConfigOnly(scope),
                Verified: input?.Verify == true,
                Array.Empty<OpenClawBackupAsset>(),
                Array.Empty<OpenClawBackupSkipped>());
        }
        var encryptedArchive = string.Empty;
        if (encrypt)
        {
            encryptedArchive = ResolveEncryptedOutputPath(outputPath, rawArchive);
            CryptoHelpers.EncryptFileWithPassword(rawArchive, encryptedArchive, password!);
            TryDeleteFile(rawArchive);
        }
        else if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var resolvedOutput = ResolveRawOutputPath(outputPath, rawArchive);
            if (!string.Equals(resolvedOutput, rawArchive, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resolvedOutput) ?? context.BackupDirectory);
                File.Copy(rawArchive, resolvedOutput, overwrite: false);
                rawArchive = resolvedOutput;
            }
        }

        warnings.Add(new WarningItem("backup-sensitive", "Backups may contain credentials and secrets. Encrypt before sharing or uploading."));
        if (ShouldFallbackToLegacy(context, openClawResult))
        {
            warnings.Add(new WarningItem("openclaw-missing", "OpenClaw CLI unavailable; used legacy backup format."));
        }

        var summaryOutput = new BackupExportSummary(
            encrypt ? encryptedArchive : rawArchive,
            scope,
            input?.Verify == true,
            encrypt ? encryptedArchive : null,
            encrypt ? null : rawArchive);

        return new ActionResult(true, Output: summaryOutput, Warnings: warnings);
    }

    private static async Task<ActionResult> RunBackupDiff(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupDiffInput? input,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var left = input?.LeftArchivePath;
        var right = input?.RightArchivePath;

        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            var backups = Directory.Exists(context.BackupDirectory)
                ? Directory.GetFiles(context.BackupDirectory, "*.tar.gz*").OrderByDescending(File.GetLastWriteTimeUtc).ToArray()
                : Array.Empty<string>();

            left ??= backups.ElementAtOrDefault(0);
            right ??= backups.ElementAtOrDefault(1);
        }

        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return new ActionResult(false, Error: "Two backups are required for diff. Specify --left and --right.");
        }

        try
        {
            var service = new BackupDiffService();
            var summary = await service.DiffAsync(left, right, input?.RedactSecrets ?? true, input?.Password, cancellationToken).ConfigureAwait(false);
            var warnings = new List<WarningItem>();
            if (input?.RedactSecrets != false)
            {
                warnings.Add(new WarningItem("redacted", "Diff output is redacted. Use --no-redact to see full values."));
            }
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Diff", $"{summary.AddedAssets.Count} added, {summary.RemovedAssets.Count} removed"));
            return new ActionResult(true, Output: summary, Warnings: warnings);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, Error: ex.Message);
        }
    }

    private static async Task<ActionResult> RunBackupScheduleCreate(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupScheduleInput? input,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        if (input is null)
        {
            return new ActionResult(false, Error: "Schedule input is required.");
        }

        try
        {
            var service = new BackupScheduleService(processRunner);
            var summary = await service.CreateAsync(actionId, correlationId, context, input, events, cancellationToken).ConfigureAwait(false);
            return new ActionResult(true, Output: summary);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, Error: ex.Message);
        }
    }

    private static async Task<ActionResult> RunBackupScheduleList(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        try
        {
            var service = new BackupScheduleService(processRunner);
            var summary = await service.ListAsync(context, cancellationToken).ConfigureAwait(false);
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Schedules", summary.Schedules.Count.ToString(CultureInfo.InvariantCulture)));
            return new ActionResult(true, Output: summary);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, Error: ex.Message);
        }
    }

    private static async Task<ActionResult> RunBackupScheduleRemove(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BackupScheduleRemoveInput? input,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        try
        {
            var service = new BackupScheduleService(processRunner);
            var summary = await service.RemoveAsync(actionId, correlationId, context, input?.ScheduleId, events, cancellationToken).ConfigureAwait(false);
            return new ActionResult(true, Output: summary);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, Error: ex.Message);
        }
    }

    private static async Task<ActionResult> RunGatewayUrl(
        string actionId,
        Guid correlationId,
        ActionContext context,
        GatewayUrlInput? input,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var effectiveContext = ApplyRuntimeOverride(context, input?.RuntimeOverride);
        var env = BuildConfigOverrideEnvironment(input?.ConfigOverride);
        var runner = new OpenClawRunner(processRunner);
        var result = await runner.RunAsync(actionId, correlationId, effectiveContext, new[] { "dashboard", "--no-open" }, events, cancellationToken, env).ConfigureAwait(false);
        var warnings = new List<WarningItem>();
        if (result.Output is OpenClawCommandSummary summary)
        {
            var url = TryExtractUrl(summary);
            if (!string.IsNullOrWhiteSpace(url))
            {
                events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Dashboard URL", url));
            }
            else
            {
                warnings.Add(new WarningItem("dashboard-url-missing", "Dashboard URL not found in output."));
            }
        }
        return new ActionResult(result.Success, Output: result.Output, Error: result.Error, ExitCode: result.ExitCode, Warnings: warnings);
    }

    private static async Task<ActionResult> RunDashboardOpen(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var runner = new OpenClawRunner(processRunner);
        var healthResult = await runner.RunAsync(
            $"{actionId}-health",
            correlationId,
            context,
            new[] { "gateway", "status", "--require-rpc" },
            events,
            cancellationToken).ConfigureAwait(false);
        var healthSummary = healthResult.Output as OpenClawCommandSummary;
        var gatewayHealthy = IsGatewayHealthy(healthSummary);
        if (!gatewayHealthy)
        {
            var preflightWarnings = new List<WarningItem>();
            var snapshot = BuildGatewayStatusSnapshot(healthSummary);
            var lastCheck = DateTimeOffset.Now.ToString("g");
            var message = string.IsNullOrWhiteSpace(snapshot)
                ? $"Gateway not ready for browser. Last check {lastCheck}. Next action: run repair or view logs."
                : $"Gateway not ready for browser. Last check {lastCheck}: {snapshot}. Next action: run repair or view logs.";
            preflightWarnings.Add(new WarningItem("gateway-not-ready", message));
            return new ActionResult(false, Output: healthSummary, Error: "Gateway not ready for browser.", ExitCode: healthResult.ExitCode, Warnings: preflightWarnings);
        }

        var port = ResolveGatewayPort(healthSummary);
        var healthOk = await ProbeGatewayHealthAsync(port, cancellationToken).ConfigureAwait(false);
        if (!healthOk)
        {
            var preflightWarnings = new List<WarningItem>();
            var snapshot = BuildGatewayStatusSnapshot(healthSummary);
            var lastCheck = DateTimeOffset.Now.ToString("g");
            var message = string.IsNullOrWhiteSpace(snapshot)
                ? $"Gateway not ready for browser. Last check {lastCheck}. Health probe failed at http://127.0.0.1:{port}/healthz. Next action: run repair or view logs."
                : $"Gateway not ready for browser. Last check {lastCheck}: {snapshot}. Health probe failed at http://127.0.0.1:{port}/healthz. Next action: run repair or view logs.";
            preflightWarnings.Add(new WarningItem("gateway-not-ready", message));
            preflightWarnings.Add(new WarningItem("gateway-healthz-failed", $"Health probe failed at http://127.0.0.1:{port}/healthz."));
            return new ActionResult(false, Output: healthSummary, Error: "Gateway not ready for browser.", ExitCode: healthResult.ExitCode, Warnings: preflightWarnings);
        }

        var result = await runner.RunAsync(actionId, correlationId, context, new[] { "dashboard", "--no-open" }, events, cancellationToken).ConfigureAwait(false);
        var warnings = new List<WarningItem>();
        if (result.Output is OpenClawCommandSummary summary)
        {
            var url = TryExtractUrl(summary);
            if (!string.IsNullOrWhiteSpace(url))
            {
                events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Opening", url));
                try
                {
                    OpenUrl(url);
                }
                catch (Exception ex)
                {
                    warnings.Add(new WarningItem("dashboard-open-failed", ex.Message));
                }
            }
            else
            {
                warnings.Add(new WarningItem("dashboard-url-missing", "Dashboard URL not found in output."));
            }
        }
        return new ActionResult(result.Success, Output: result.Output, Error: result.Error, ExitCode: result.ExitCode, Warnings: warnings);
    }

    private static Task<ActionResult> RunGatewayTokenShow(
        string actionId,
        Guid correlationId,
        ActionContext context,
        GatewayTokenInput? input,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var reveal = input?.Reveal == true;
        var (token, path) = TryReadGatewayToken(context);
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(new ActionResult(false, Error: "OpenClaw config path not found."));
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(new ActionResult(false, Error: "Gateway token not found."));
        }

        var masked = reveal ? token : MaskToken(token);
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Gateway Token", reveal ? "revealed" : "masked"));
        return Task.FromResult<ActionResult>(new ActionResult(true, Output: new GatewayTokenSummary(masked, reveal, path)));
    }

    private static async Task<ActionResult> RunGatewayTokenGenerate(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var runner = new OpenClawRunner(processRunner);
        var result = await runner.RunAsync(
            actionId,
            correlationId,
            context,
            new[] { "doctor", "--generate-gateway-token", "--non-interactive", "--yes" },
            events,
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    private static async Task<ActionResult> RunBrowserDiagnostics(
        string actionId,
        Guid correlationId,
        ActionContext context,
        BrowserDiagnosticsInput? input,
        IProgress<ActionEvent> events,
        ProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var effectiveContext = ApplyRuntimeOverride(context, input?.RuntimeOverride);
        var env = BuildConfigOverrideEnvironment(input?.ConfigOverride);
        var runner = new OpenClawRunner(processRunner);
        var result = await runner.RunAsync(actionId, correlationId, effectiveContext, new[] { "status", "--deep" }, events, cancellationToken, env).ConfigureAwait(false);
        if (result.Output is not OpenClawCommandSummary summary)
        {
            return new ActionResult(result.Success, Output: result.Output, Error: result.Error, ExitCode: result.ExitCode);
        }

        var warningItems = BuildBrowserWarnings(summary);
        var diagnostics = BuildBrowserDiagnosticsSummary(summary, input, context, warningItems);
        return new ActionResult(result.Success, Output: diagnostics, Error: result.Error, ExitCode: result.ExitCode, Warnings: warningItems);
    }

    private static string BuildBackupOutputPath(string backupDirectory)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"openclaw_backup_{timestamp}.tar.gz";
        return Path.Combine(backupDirectory, fileName);
    }

    private static string BuildRebuildSnapshotPath(string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"openclaw_rebuild_{timestamp}.tar.gz";
        return Path.Combine(backupDirectory, fileName);
    }

    private static string BuildPreRestoreSnapshotPath(string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"openclaw_pre_restore_{timestamp}.tar.gz";
        return Path.Combine(backupDirectory, fileName);
    }

    private static string BuildPreFixSnapshotPath(string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"openclaw_pre_fix_{timestamp}.tar.gz";
        return Path.Combine(backupDirectory, fileName);
    }

    private static string BuildPreResetSnapshotPath(string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"openclaw_pre_reset_{timestamp}.tar.gz";
        return Path.Combine(backupDirectory, fileName);
    }

    private static async Task<string> CreateOpenClawSnapshotAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        BackupService backupService,
        ProcessRunner processRunner,
        string outputPath,
        CancellationToken cancellationToken)
    {
        EnsureOutputDirectory(outputPath);
        if (backupService.IsFaultInjected)
        {
            await backupService.CreateBackupAsync(context.OpenClawHome, outputPath, null, "full").ConfigureAwait(false);
            await backupService.VerifySnapshotAsync(outputPath).ConfigureAwait(false);
            return outputPath;
        }

        var runner = new OpenClawRunner(processRunner);
        var args = new[] { "backup", "create", "--verify", "--json", "--output", outputPath };
        ActionResult result;
        try
        {
            result = await runner.RunAsync(actionId, correlationId, context, args, events, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new ActionResult(false, Error: ex.Message);
        }
        if (!result.Success)
        {
            if (!ShouldFallbackToLegacy(context, result))
            {
                throw new InvalidOperationException(result.Error ?? "OpenClaw backup create failed.");
            }

            await backupService.CreateBackupAsync(context.OpenClawHome, outputPath, null, "full").ConfigureAwait(false);
            await backupService.VerifySnapshotAsync(outputPath).ConfigureAwait(false);
            return outputPath;
        }

        if (result.Output is not OpenClawCommandSummary summary)
        {
            throw new InvalidOperationException(result.Error ?? "OpenClaw backup create failed.");
        }
        try
        {
            var parsed = OpenClawBackupParser.ParseCreate(summary);
            return parsed.ArchivePath;
        }
        catch (Exception ex)
        {
            if (ShouldFallbackToLegacy(context, result))
            {
                await backupService.CreateBackupAsync(context.OpenClawHome, outputPath, null, "full").ConfigureAwait(false);
                await backupService.VerifySnapshotAsync(outputPath).ConfigureAwait(false);
                return outputPath;
            }

            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private static bool ScopeIsConfigOnly(string? scope)
    {
        var tokens = ParseScopeTokens(scope, "full");
        return tokens.SetEquals(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "config" });
    }

    private static bool ScopeIncludesWorkspace(string? scope)
    {
        var tokens = ParseScopeTokens(scope, "full");
        return tokens.Contains("full") || tokens.Contains("workspace");
    }

    private static (string Scope, IReadOnlyList<string> Tokens) BuildRebuildScope(OpenClawRebuildInput? input, List<WarningItem> warnings)
    {
        var tokens = new List<string>();
        if (input?.PreserveConfig != false) tokens.Add("config");
        if (input?.PreserveCredentials != false) tokens.Add("creds");
        if (input?.PreserveSessions != false) tokens.Add("sessions");
        if (input?.PreserveWorkspace != false) tokens.Add("workspace");

        if (tokens.Count == 0)
        {
            warnings.Add(new WarningItem("rebuild-no-preserve", "No preserve options selected; defaulting to config restore."));
            tokens.Add("config");
        }

        var scope = tokens.Count == 4 ? "full" : string.Join('+', tokens);
        return (scope, tokens);
    }

    private static bool IsOpenClawMissing(ActionResult result)
    {
        return result.Error?.IndexOf("openclaw CLI not found", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool ShouldFallbackToLegacy(ActionContext context, ActionResult result)
    {
        if (IsOpenClawMissing(result))
        {
            return true;
        }

        if (!result.Success)
        {
            return true;
        }

        var resolved = OpenClawLocator.ResolveWithSource(context);
        return string.Equals(resolved?.Source, "repo-fallback", StringComparison.OrdinalIgnoreCase);
    }

    private static ActionContext ApplyRuntimeOverride(ActionContext context, string? runtimeOverride)
    {
        if (string.IsNullOrWhiteSpace(runtimeOverride))
        {
            return context;
        }

        if (runtimeOverride.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || runtimeOverride.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            return context with { OpenClawEntry = runtimeOverride };
        }

        return context with { OpenClawExecutable = runtimeOverride };
    }

    private static IDictionary<string, string>? BuildConfigOverrideEnvironment(string? configOverride)
    {
        if (string.IsNullOrWhiteSpace(configOverride))
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OPENCLAW_CONFIG_PATH"] = configOverride
        };
    }

    private static HashSet<string> ParseScopeTokens(string? scope, string fallback)
    {
        var raw = string.IsNullOrWhiteSpace(scope) ? fallback : scope!;
        var tokens = raw
            .Split(new[] { ',', '+', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim().ToLowerInvariant())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (tokens.Count == 0)
        {
            tokens.Add(fallback);
        }

        return tokens;
    }

    private static bool IsReClawEncryptedArchive(string archivePath)
    {
        try
        {
            using var fs = File.OpenRead(archivePath);
            var header = new byte[8];
            var read = fs.Read(header, 0, header.Length);
            if (read != header.Length) return false;
            var magic = Encoding.ASCII.GetBytes("RCLAWENC1");
            return header.SequenceEqual(magic);
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureOutputDirectory(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return;
        try
        {
            if (Directory.Exists(outputPath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = outputPath;
            }

            Directory.CreateDirectory(directory);
        }
        catch
        {
            // best effort
        }
    }

    private static string ResolveEncryptedOutputPath(string? requestedOutput, string rawArchivePath)
    {
        if (string.IsNullOrWhiteSpace(requestedOutput))
        {
            return rawArchivePath + ".enc";
        }

        if (requestedOutput.EndsWith(Path.DirectorySeparatorChar) || requestedOutput.EndsWith(Path.AltDirectorySeparatorChar))
        {
            var name = Path.GetFileName(rawArchivePath) + ".enc";
            return Path.Combine(requestedOutput.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), name);
        }

        if (Directory.Exists(requestedOutput))
        {
            var name = Path.GetFileName(rawArchivePath) + ".enc";
            return Path.Combine(requestedOutput, name);
        }

        return requestedOutput;
    }

    private static string ResolveRawOutputPath(string requestedOutput, string rawArchivePath)
    {
        if (string.IsNullOrWhiteSpace(requestedOutput))
        {
            return rawArchivePath;
        }

        if (requestedOutput.EndsWith(Path.DirectorySeparatorChar) || requestedOutput.EndsWith(Path.AltDirectorySeparatorChar))
        {
            var name = Path.GetFileName(rawArchivePath);
            return Path.Combine(requestedOutput.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), name);
        }

        if (Directory.Exists(requestedOutput))
        {
            var name = Path.GetFileName(rawArchivePath);
            return Path.Combine(requestedOutput, name);
        }

        return requestedOutput;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static string? TryExtractUrl(OpenClawCommandSummary summary)
    {
        var lines = summary.StdOut.Concat(summary.StdErr);
        foreach (var line in lines)
        {
            var index = line.IndexOf("http", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var slice = line.Substring(index).Trim();
                var end = slice.IndexOf(' ');
                return end > 0 ? slice.Substring(0, end) : slice;
            }
        }

        return null;
    }

    private static void OpenUrl(string url)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true });
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
            return;
        }

        Process.Start("xdg-open", url);
    }

    private static (string? Token, string? ConfigPath) TryReadGatewayToken(ActionContext context, string? configOverride = null)
    {
        var configPath = !string.IsNullOrWhiteSpace(configOverride) ? configOverride : ResolveConfigPath(context);
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return (null, configPath);
        }

        try
        {
            var raw = File.ReadAllText(configPath);
            if (!Json5Reader.TryParse(raw, out var doc))
            {
                return (null, configPath);
            }

            using (doc)
            {
                var root = doc!.RootElement;
                var token = TryFindString(root, "gatewayToken", "gateway_token", "authToken");
                if (string.IsNullOrWhiteSpace(token) && root.TryGetProperty("gateway", out var gateway) && gateway.ValueKind == JsonValueKind.Object)
                {
                    token = TryFindString(gateway, "token", "authToken", "gatewayToken");
                }
                if (string.IsNullOrWhiteSpace(token) && root.TryGetProperty("auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
                {
                    token = TryFindString(auth, "gatewayToken", "token");
                }
                return (token, configPath);
            }
        }
        catch
        {
            return (null, configPath);
        }
    }

    private static string? ResolveConfigPath(ActionContext context)
    {
        var explicitPath = Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;

        var configDirPath = Path.Combine(context.ConfigDirectory, "openclaw.json");
        if (File.Exists(configDirPath)) return configDirPath;

        var homePath = Path.Combine(context.OpenClawHome, "openclaw.json");
        if (File.Exists(homePath)) return homePath;

        var nested = Path.Combine(context.OpenClawHome, "config", "openclaw.json");
        return nested;
    }

    private static string? TryFindString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (property.NameEquals(name))
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }
        return null;
    }

    private static string MaskToken(string token)
    {
        if (token.Length <= 8) return "********";
        return $"{token[..4]}…{token[^4..]}";
    }

    private static IReadOnlyList<WarningItem> BuildBrowserWarnings(OpenClawCommandSummary summary)
    {
        var warnings = new List<WarningItem>();
        var lines = summary.StdOut.Concat(summary.StdErr).ToArray();

        var hasLoopback = lines.Any(line =>
            line.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("localhost", StringComparison.OrdinalIgnoreCase));

        var hasWideBind = lines.Any(line =>
            line.Contains("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("http://", StringComparison.OrdinalIgnoreCase) && !line.Contains("127.0.0.1"));

        if (hasWideBind || !hasLoopback)
        {
            warnings.Add(new WarningItem("browser-unsafe-bind", "Gateway appears to be bound to a non-loopback interface. Prefer 127.0.0.1 or a secure tunnel."));
        }

        if (lines.Any(line => line.Contains("allowedOrigins", StringComparison.OrdinalIgnoreCase) && line.Contains("[]", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(new WarningItem("browser-origins", "Allowed origins are empty; browser access may be blocked. Update gateway auth/origin settings."));
        }

        if (lines.Any(line => line.Contains("auth", StringComparison.OrdinalIgnoreCase) && line.Contains("disabled", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(new WarningItem("browser-auth", "Gateway auth appears disabled. Enable authentication before exposing the dashboard."));
        }

        return warnings;
    }

    private static BrowserDiagnosticsSummary BuildBrowserDiagnosticsSummary(
        OpenClawCommandSummary summary,
        BrowserDiagnosticsInput? input,
        ActionContext context,
        IReadOnlyList<WarningItem> warningItems)
    {
        var warnings = warningItems.Select(item => item.Message).ToList();
        var localUrl = TryExtractUrl(summary);
        if (string.IsNullOrWhiteSpace(localUrl) || (!localUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) && !localUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            var port = ExtractGatewayPort(summary) ?? 18789;
            localUrl = $"http://127.0.0.1:{port}";
        }

        var (token, _) = TryReadGatewayToken(context, input?.ConfigOverride);
        var tokenPresent = !string.IsNullOrWhiteSpace(token);

        var dashboardUrl = TryExtractUrl(summary);
        if (string.IsNullOrWhiteSpace(dashboardUrl))
        {
            dashboardUrl = tokenPresent ? $"{localUrl}?token={token}" : localUrl;
        }

        var authRequired = !warningItems.Any(w => w.Code == "browser-auth");
        var allowedOriginsValid = !warningItems.Any(w => w.Code == "browser-origins");
        var secureContextWarning = input?.Mode == BrowserAccessMode.Remote && localUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var hasWideBind = warningItems.Any(w => w.Code == "browser-unsafe-bind");
        var remoteUnsafe = input?.Mode == BrowserAccessMode.Remote && (hasWideBind || !authRequired || !allowedOriginsValid || secureContextWarning);
        var remoteSafe = input?.Mode == BrowserAccessMode.Remote && !remoteUnsafe;

        return new BrowserDiagnosticsSummary(
            localUrl,
            dashboardUrl,
            authRequired,
            tokenPresent,
            allowedOriginsValid,
            secureContextWarning,
            remoteSafe,
            remoteUnsafe,
            warnings,
            summary);
    }

    private static int? ExtractGatewayPort(OpenClawCommandSummary summary)
    {
        foreach (var line in summary.StdOut.Concat(summary.StdErr))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @":(?<port>\d{2,5})");
            if (match.Success && int.TryParse(match.Groups["port"].Value, out var port))
            {
                return port;
            }

            match = System.Text.RegularExpressions.Regex.Match(line, @"port\s+(?<port>\d{2,5})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups["port"].Value, out port))
            {
                return port;
            }
        }

        return null;
    }

    private static string[] BuildDoctorArgs(DoctorInput? input)
    {
        var args = new List<string> { "doctor" };
        if (input?.Repair == true) args.Add("--repair");
        if (input?.Force == true) args.Add("--force");
        if (input?.Deep == true) args.Add("--deep");
        if (input?.GenerateToken == true) args.Add("--generate-gateway-token");
        if (input?.NonInteractive != false) args.Add("--non-interactive");
        if (input?.Yes != false) args.Add("--yes");
        return args.ToArray();
    }

    private static string[] BuildFixArgs(FixInput? input)
    {
        var args = new List<string> { "doctor", "--fix" };
        if (input?.Force == true) args.Add("--force");
        if (input?.NonInteractive != false) args.Add("--non-interactive");
        if (input?.Yes != false) args.Add("--yes");
        return args.ToArray();
    }

    private static async Task<DiagnosticsResult> MaybeExportDiagnosticsAsync(
        ActionContext context,
        bool? exportDiagnostics,
        string? outputPath,
        IProgress<ActionEvent> events,
        string actionId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (exportDiagnostics != true)
        {
            return DiagnosticsResult.Empty;
        }

        var service = new DiagnosticsBundleService();
        try
        {
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Diagnostics", "Collecting"));
            var summary = await service.CreateBundleAsync(context, outputPath, cancellationToken).ConfigureAwait(false);
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Diagnostics", summary.BundlePath));
            return new DiagnosticsResult(summary.BundlePath, null);
        }
        catch (Exception ex)
        {
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Diagnostics warning", ex.Message));
            return new DiagnosticsResult(null, new WarningItem("diagnostics-failed", $"Diagnostics export failed: {ex.Message}"));
        }
    }

    private sealed record DiagnosticsResult(string? Path, WarningItem? Warning)
    {
        public static DiagnosticsResult Empty { get; } = new(null, null);
    }

    private static string BuildRestoreImpactSummary(RestorePreview preview)
    {
        var assetCount = preview.Assets.Count;
        var overwriteCount = preview.OverwritePayloadEntries;
        var total = preview.RestorePayloadEntries;
        return $"scope={preview.Scope}; entries={total}; overwrites={overwriteCount}; targets={assetCount}";
    }

    private static ResetPlan BuildResetPlan(ActionContext context, ResetMode mode)
    {
        var resetService = new ResetService();
        var resetContext = new ResetContext(
            context.OpenClawHome,
            context.ConfigDirectory,
            context.DataDirectory,
            context.BackupDirectory);
        return resetService.BuildPlan(resetContext, mode);
    }

    private static string GetJournalPath(ActionContext context)
    {
        return Path.Combine(context.DataDirectory, "journal.jsonl");
    }

    private static string? FindLatestBackup(string backupDirectory)
    {
        if (!Directory.Exists(backupDirectory)) return null;
        return Directory.GetFiles(backupDirectory, "*.tar.gz*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static async Task<string?> FindLatestValidBackupAsync(string backupDirectory, BackupService backupService, string? password)
    {
        if (!Directory.Exists(backupDirectory)) return null;
        var candidates = Directory.GetFiles(backupDirectory, "*.tar.gz*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();

        foreach (var candidate in candidates)
        {
            try
            {
                await backupService.VerifySnapshotAsync(candidate, password).ConfigureAwait(false);
                return candidate;
            }
            catch
            {
                // skip invalid or mismatched archives
            }
        }

        return null;
    }

    private static TimeSpan ParseAge(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return TimeSpan.FromDays(30);
        value = value.Trim();
        if (value.EndsWith("d", StringComparison.OrdinalIgnoreCase) && double.TryParse(value[..^1], out var days))
        {
            return TimeSpan.FromDays(days);
        }
        if (value.EndsWith("h", StringComparison.OrdinalIgnoreCase) && double.TryParse(value[..^1], out var hours))
        {
            return TimeSpan.FromHours(hours);
        }
        if (double.TryParse(value, out var rawDays))
        {
            return TimeSpan.FromDays(rawDays);
        }
        return TimeSpan.FromDays(30);
    }
}
