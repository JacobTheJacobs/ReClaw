using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Actions;
using ReClaw.Core;

namespace ReClaw.App.Execution;

internal sealed class OpenClawRebuildService
{
    private static readonly System.Net.Http.HttpClient Http = new();
    private readonly IOpenClawActionRunner runner;
    private readonly BackupService backupService;
    private readonly IProcessRunner processRunner;
    private readonly Func<ActionContext, IReadOnlyList<OpenClawCandidate>> candidateProvider;
    private readonly ResetService resetService = new();

    public OpenClawRebuildService(
        IOpenClawActionRunner runner,
        BackupService backupService,
        IProcessRunner processRunner,
        Func<ActionContext, IReadOnlyList<OpenClawCandidate>> candidateProvider)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.candidateProvider = candidateProvider ?? throw new ArgumentNullException(nameof(candidateProvider));
    }

    public async Task<ActionResult> RebuildAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        OpenClawRebuildInput input,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var warnings = new List<WarningItem>();
        if (!input.ConfirmDestructive)
        {
            warnings.Add(new WarningItem("confirmation-required", "Rebuild requires confirmation before proceeding."));
            return new ActionResult(false, Error: "Rebuild requires confirmation. Pass --confirm-destructive to proceed.", Warnings: warnings);
        }

        var steps = new List<RebuildStep>();
        var removedItems = new List<string>();
        var installedItems = new List<string>();
        var verification = new RebuildVerificationSummary(null, null, null, null, null);

        var scopeInfo = BuildRebuildScope(input, warnings);
        var resetMode = input.CleanInstall ? ResetMode.FullLocalReset : ResetMode.PreserveBackups;

        var inventorySummary = await CollectInventoryAsync(actionId, correlationId, context, events, cancellationToken, steps).ConfigureAwait(false);
        var inventory = inventorySummary?.Inventory;
        var detection = inventorySummary?.Detection;

        var configNewerThanRuntime = IsConfigNewerThanRuntime(detection, warnings);
        var runtimePlan = ResolveRuntimePlan(context, inventorySummary?.Inventory);
        if (configNewerThanRuntime)
        {
            runtimePlan = runtimePlan with { Description = $"{runtimePlan.Description} (config newer than runtime)" };
        }

        steps.Add(new RebuildStep("runtime-strategy", "Runtime strategy", "info", null, runtimePlan.Description));

        var backupPath = await CreateVerifiedBackupAsync(
            actionId,
            correlationId,
            context,
            input.Password,
            events,
            cancellationToken,
            steps,
            warnings).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(backupPath))
        {
            return BuildResult(false, "Backup step failed.", backupPath ?? string.Empty, scopeInfo, resetMode, runtimePlan.Description, removedItems, installedItems, steps, verification, inventory, warnings, context);
        }

        steps.Add(new RebuildStep("preserve", "Preserve scopes", "success", null, scopeInfo.Scope));

        var stopResult = await TryRunOpenClawStepAsync(
            "gateway-stop",
            "Gateway stop",
            new[] { "gateway", "stop" },
            actionId,
            correlationId,
            context,
            events,
            cancellationToken).ConfigureAwait(false);
        if (stopResult.Step != null) steps.Add(stopResult.Step);

        var serviceMismatch = inventory?.Services.Any(service => service.IsMismatched || service.IsLegacy) == true;
        var serviceMissing = detection?.GatewayServiceExists == false;
        if (serviceMismatch)
        {
            warnings.Add(new WarningItem("gateway-entrypoint-mismatch", "Gateway service entrypoint mismatch detected; service will be reinstalled."));
        }

        if (!serviceMissing || serviceMismatch)
        {
            var uninstallResult = await TryRunOpenClawStepAsync(
                "gateway-uninstall",
                "Gateway uninstall",
                new[] { "gateway", "uninstall" },
                actionId,
                correlationId,
                context,
                events,
                cancellationToken).ConfigureAwait(false);
            if (uninstallResult.Step != null)
            {
                steps.Add(uninstallResult.Step);
                if (uninstallResult.Step.Status == "success")
                {
                    removedItems.Add("gateway-service");
                }
            }
        }
        else
        {
            steps.Add(new RebuildStep("gateway-uninstall", "Gateway uninstall", "skipped", null, "Gateway service missing."));
        }

        if (!await ExecuteResetAsync(actionId, correlationId, context, resetMode, events, steps, removedItems).ConfigureAwait(false))
        {
            return BuildResult(false, "Reset failed.", backupPath, scopeInfo, resetMode, runtimePlan.Description, removedItems, installedItems, steps, verification, inventory, warnings, context);
        }

        var runtimeResult = await ExecuteRuntimeStrategyAsync(
            runtimePlan,
            input.CleanInstall,
            configNewerThanRuntime,
            actionId,
            correlationId,
            context,
            events,
            cancellationToken,
            steps,
            removedItems,
            installedItems,
            warnings).ConfigureAwait(false);
        if (!runtimeResult)
        {
            return BuildResult(false, "Runtime reinstall failed.", backupPath, scopeInfo, resetMode, runtimePlan.Description, removedItems, installedItems, steps, verification, inventory, warnings, context);
        }

        var doctorResult = await RunOpenClawStepAsync(
            "doctor",
            "Doctor",
            new[] { "doctor", "--non-interactive", "--yes" },
            actionId,
            correlationId,
            context,
            events,
            cancellationToken).ConfigureAwait(false);
        steps.Add(doctorResult.Step);
        if (!doctorResult.Result.Success)
        {
            return BuildResult(false, "Doctor failed.", backupPath, scopeInfo, resetMode, runtimePlan.Description, removedItems, installedItems, steps, verification, inventory, warnings, context);
        }

        var installServiceResult = await RunOpenClawStepAsync(
            "gateway-install",
            "Gateway install",
            new[] { "gateway", "install" },
            actionId,
            correlationId,
            context,
            events,
            cancellationToken).ConfigureAwait(false);
        steps.Add(installServiceResult.Step);
        if (!installServiceResult.Result.Success)
        {
            return BuildResult(false, "Gateway install failed.", backupPath, scopeInfo, resetMode, runtimePlan.Description, removedItems, installedItems, steps, verification, inventory, warnings, context);
        }
        installedItems.Add("gateway-service");

        var restoreResult = await RestorePreservedScopesAsync(
            actionId,
            correlationId,
            backupPath,
            scopeInfo.Scope,
            context,
            input.Password,
            events,
            cancellationToken,
            steps).ConfigureAwait(false);
        if (!restoreResult)
        {
            return BuildResult(false, "Restore failed.", backupPath, scopeInfo, resetMode, runtimePlan.Description, removedItems, installedItems, steps, verification, inventory, warnings, context);
        }

        var modeStep = EnsureGatewayModeLocal(context.OpenClawHome);
        if (modeStep != null)
        {
            steps.Add(modeStep);
        }

        var startResult = await RunOpenClawStepAsync(
            "gateway-start",
            "Gateway start",
            new[] { "gateway", "start" },
            actionId,
            correlationId,
            context,
            events,
            cancellationToken).ConfigureAwait(false);
        steps.Add(startResult.Step);
        if (!startResult.Result.Success)
        {
            warnings.Add(new WarningItem("gateway-start-failed", "Gateway failed to start after rebuild."));
        }

        var stabilization = await StabilizeGatewayAsync(
            actionId,
            correlationId,
            context,
            events,
            cancellationToken,
            steps).ConfigureAwait(false);
        if (stabilization.Diagnostics is not null)
        {
            warnings.AddRange(stabilization.DiagnosticsWarnings);
        }

        var verificationResult = await VerifyAsync(
            actionId,
            correlationId,
            context,
            events,
            cancellationToken,
            steps,
            warnings,
            stabilization.Diagnostics,
            stabilization.LogsSummary).ConfigureAwait(false);
        verification = verificationResult.Verification;
        if (verificationResult.Warnings.Count > 0)
        {
            warnings.AddRange(verificationResult.Warnings);
        }

        var success = verification.GatewayHealthy && verification.LogsAvailable && verification.BrowserReady;
        if (!success && verification.VerificationFailures is { Count: > 0 } failures)
        {
            return BuildResult(false, $"Rebuild verification failed: {string.Join("; ", failures)}", backupPath, scopeInfo, resetMode, runtimePlan.Description, removedItems, installedItems, steps, verification, inventory, warnings, context);
        }

        return BuildResult(success, success ? null : "Rebuild verification failed.", backupPath, scopeInfo, resetMode, runtimePlan.Description, removedItems, installedItems, steps, verification, inventory, warnings, context);
    }
    private async Task<GatewayRepairSummary?> CollectInventoryAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        List<RebuildStep> steps)
    {
        var service = new GatewayRepairService(runner, backupService, candidateProvider);
        var result = await service.RunWithRepairAsync(
            "gateway-status",
            correlationId,
            context,
            new[] { "gateway", "status" },
            events,
            cancellationToken).ConfigureAwait(false);

        var summary = result.Output as GatewayRepairSummary;
        var status = result.Success ? "success" : "failed";
        var command = summary?.FinalCommand?.Command;
        steps.Add(new RebuildStep("inventory", "Inventory", status, command, result.Error ?? summary?.Outcome));
        return summary;
    }

    private async Task<string?> CreateVerifiedBackupAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        string? password,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        List<RebuildStep> steps,
        List<WarningItem> warnings)
    {
        var outputDir = context.BackupDirectory;
        Directory.CreateDirectory(outputDir);
        var fallbackPath = BuildRebuildSnapshotPath(outputDir);
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Backup", outputDir));

        try
        {
            var args = new[] { "backup", "create", "--verify", "--json", "--output", outputDir };
            var result = await runner.RunAsync(actionId, correlationId, context, args, events, cancellationToken).ConfigureAwait(false);
            var summary = result.Output as OpenClawCommandSummary;
            if (result.Success && summary != null)
            {
                var parsed = OpenClawBackupParser.ParseCreate(summary);
                steps.Add(new RebuildStep("backup", "Backup create", "success", summary.Command, parsed.ArchivePath));
                return parsed.ArchivePath;
            }

            warnings.Add(new WarningItem("backup-fallback", "OpenClaw backup create failed; using legacy backup format."));
        }
        catch (Exception ex)
        {
            warnings.Add(new WarningItem("backup-fallback", $"OpenClaw backup create failed; using legacy backup format. ({ex.Message})"));
        }

        try
        {
            await backupService.CreateBackupAsync(context.OpenClawHome, fallbackPath, password, "full").ConfigureAwait(false);
            await backupService.VerifySnapshotAsync(fallbackPath, password).ConfigureAwait(false);
            steps.Add(new RebuildStep("backup", "Backup create", "success", null, fallbackPath));
            return fallbackPath;
        }
        catch (Exception ex)
        {
            steps.Add(new RebuildStep("backup", "Backup create", "failed", null, ex.Message));
            return null;
        }
    }

    private async Task<bool> RestorePreservedScopesAsync(
        string actionId,
        Guid correlationId,
        string backupPath,
        string scope,
        ActionContext context,
        string? password,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        List<RebuildStep> steps)
    {
        try
        {
            var preview = await backupService.PreviewRestoreAsync(backupPath, context.OpenClawHome, password, scope, skipVerify: true).ConfigureAwait(false);
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Restore preview", preview.Scope));
            await backupService.RestoreAsync(backupPath, context.OpenClawHome, password, scope, preview).ConfigureAwait(false);
            steps.Add(new RebuildStep("restore", "Restore preserved scopes", "success", null, scope));
            return true;
        }
        catch (Exception ex)
        {
            steps.Add(new RebuildStep("restore", "Restore preserved scopes", "failed", null, ex.Message));
            return false;
        }
    }

    private async Task<(RebuildVerificationSummary Verification, IReadOnlyList<WarningItem> Warnings)> VerifyAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        List<RebuildStep> steps,
        List<WarningItem> warnings,
        RebuildGatewayDiagnostics? diagnosticsSeed,
        OpenClawCommandSummary? logsOverride)
    {
        var verificationWarnings = new List<string>();
        var verificationFailures = new List<string>();

        var gatewayStatus = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status" }, events, cancellationToken).ConfigureAwait(false);
        var gatewaySummary = gatewayStatus.Output as OpenClawCommandSummary;
        var gatewayState = ParseGatewayStatus(gatewaySummary);
        var gatewayHealthy = gatewaySummary != null && gatewaySummary.ExitCode == 0 && gatewayState.Active;
        steps.Add(new RebuildStep("verify-gateway-status", "Verify gateway status", gatewayHealthy ? "success" : "failed", gatewaySummary?.Command, gatewayStatus.Error));
        if (!gatewayHealthy)
        {
            verificationFailures.Add("Gateway status not healthy.");
        }

        var dashboardStatus = await runner.RunAsync(actionId, correlationId, context, new[] { "dashboard", "--no-open" }, events, cancellationToken).ConfigureAwait(false);
        var dashboardSummary = dashboardStatus.Output as OpenClawCommandSummary;
        steps.Add(new RebuildStep("verify-dashboard", "Verify dashboard URL", dashboardStatus.Success ? "success" : "failed", dashboardSummary?.Command, dashboardStatus.Error));

        var gatewayUrl = dashboardSummary != null
            ? TryExtractUrl(dashboardSummary)
            : null;
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            steps.Add(new RebuildStep("verify-gateway-url", "Verify gateway URL", "failed", null, "Gateway URL not found."));
            warnings.Add(new WarningItem("gateway-url-missing", "Gateway URL not found in dashboard output."));
            verificationWarnings.Add("Gateway URL not found in dashboard output.");
        }
        else
        {
            steps.Add(new RebuildStep("verify-gateway-url", "Verify gateway URL", "success", null, gatewayUrl));
        }

        OpenClawCommandSummary? logsSummary = logsOverride;
        if (logsSummary == null)
        {
            var logsResult = await RunLogsVerificationAsync(actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
            steps.Add(logsResult.Step);
            logsSummary = logsResult.Command;
        }
        else
        {
            steps.Add(BuildLogsStep(logsSummary));
        }

        var logsAvailable = logsSummary != null && (logsSummary.ExitCode == 0 || logsSummary.TimedOut);
        if (!logsAvailable)
        {
            verificationFailures.Add("Gateway logs unavailable.");
        }

        var browserDiagnosticsResult = await RunBrowserDiagnosticsAsync(actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
        steps.Add(browserDiagnosticsResult.Step);
        if (browserDiagnosticsResult.Warnings.Count > 0)
        {
            warnings.AddRange(browserDiagnosticsResult.Warnings);
            verificationWarnings.AddRange(browserDiagnosticsResult.Warnings.Select(warning => warning.Message));
        }

        var browserReady = browserDiagnosticsResult.Diagnostics != null && browserDiagnosticsResult.Step.Status == "success";
        if (!browserReady)
        {
            verificationFailures.Add("Browser diagnostics failed.");
        }

        RebuildGatewayDiagnostics? diagnostics = diagnosticsSeed;
        if (verificationFailures.Count > 0)
        {
            diagnostics = await BuildGatewayDiagnosticsAsync(
                diagnosticsSeed,
                gatewaySummary,
                logsSummary,
                browserDiagnosticsResult.Diagnostics,
                events,
                cancellationToken).ConfigureAwait(false);
        }

        return (
            new RebuildVerificationSummary(
                gatewaySummary,
                dashboardSummary,
                logsSummary,
                browserDiagnosticsResult.Diagnostics,
                gatewayUrl,
                gatewayHealthy,
                logsAvailable,
                browserReady,
                verificationWarnings,
                verificationFailures,
                diagnostics),
            browserDiagnosticsResult.Warnings);
    }

    private async Task<(RebuildStep Step, OpenClawCommandSummary? Command)> RunLogsVerificationAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var (command, spec, commandLine) = OpenClawRunner.BuildRunSpec(context, new[] { "logs", "--follow" });
        spec = spec with { Timeout = TimeSpan.FromSeconds(3) };
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Logs verify", commandLine));
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

        var status = result.ExitCode == 0 || result.TimedOut ? "success" : "failed";
        var detail = result.TimedOut ? "Log follow timed out (expected for short verification)." : null;
        return (new RebuildStep("verify-logs", "Verify logs availability", status, commandLine, detail), summary);
    }

    private async Task<(RebuildStep Step, BrowserDiagnosticsSummary? Diagnostics, IReadOnlyList<WarningItem> Warnings)> RunBrowserDiagnosticsAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(actionId, correlationId, context, new[] { "status", "--deep" }, events, cancellationToken).ConfigureAwait(false);
        if (result.Output is not OpenClawCommandSummary summary)
        {
            var status = result.Success ? "success" : "failed";
            return (new RebuildStep("verify-browser", "Verify browser diagnostics", status, null, result.Error), null, result.Warnings ?? Array.Empty<WarningItem>());
        }

        var warningItems = BuildBrowserWarnings(summary);
        var diagnostics = BuildBrowserDiagnosticsSummary(summary, new BrowserDiagnosticsInput(), context, warningItems);
        var step = new RebuildStep("verify-browser", "Verify browser diagnostics", result.Success ? "success" : "failed", summary.Command, result.Error);
        return (step, diagnostics, warningItems);
    }

    private async Task<GatewayStabilizationResult> StabilizeGatewayAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        List<RebuildStep> steps)
    {
        var diagnosticsWarnings = new List<WarningItem>();
        var lockCleanup = CleanupStaleGatewayLocks(context.OpenClawHome);
        if (lockCleanup.Removed > 0)
        {
            steps.Add(new RebuildStep("gateway-locks", "Gateway lock cleanup", "success", null, $"Removed {lockCleanup.Removed} stale lock(s)."));
        }
        else
        {
            steps.Add(new RebuildStep("gateway-locks", "Gateway lock cleanup", "skipped", null, "No stale gateway lock files detected."));
        }

        var initialWait = await WaitForGatewayReadyAsync(
            actionId,
            correlationId,
            context,
            events,
            cancellationToken,
            "gateway-wait",
            "Gateway readiness").ConfigureAwait(false);
        steps.Add(initialWait.Step);

        if (initialWait.State.Active)
        {
            return new GatewayStabilizationResult(null, diagnosticsWarnings, null);
        }

        diagnosticsWarnings.Add(new WarningItem("gateway-not-running", "Gateway service is installed but not running after rebuild."));

        var logsResult = await RunLogsVerificationAsync(actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);

        if (OperatingSystem.IsWindows() && IsDetachedStartAllowed())
        {
            var detached = TryLaunchDetachedGateway(context, ResolveGatewayPort());
            steps.Add(new RebuildStep(
                "gateway-run-detached",
                "Gateway run detached",
                detached ? "success" : "skipped",
                null,
                detached ? "Detached gateway run launched." : "Detached gateway run disabled or unavailable."));

            if (detached)
            {
                var detachedWait = await WaitForGatewayReadyAsync(
                    actionId,
                    correlationId,
                    context,
                    events,
                    cancellationToken,
                    "gateway-wait-detached",
                    "Gateway readiness (detached)").ConfigureAwait(false);
                steps.Add(detachedWait.Step);
                if (detachedWait.State.Active)
                {
                    return new GatewayStabilizationResult(null, diagnosticsWarnings, logsResult.Command);
                }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var retryCleanup = CleanupStaleGatewayLocks(context.OpenClawHome);
            var retryStatus = retryCleanup.Removed > 0 ? "success" : "skipped";
            var retryDetail = retryCleanup.Removed > 0
                ? $"Removed {retryCleanup.Removed} stale lock(s) before retry."
                : "No stale gateway lock files detected before retry.";
            steps.Add(new RebuildStep("gateway-locks-retry", "Gateway lock cleanup (retry)", retryStatus, null, retryDetail));
        }

        var doctorRetry = await RunOpenClawStepAsync(
            "doctor-retry",
            "Doctor retry",
            new[] { "doctor", "--non-interactive", "--yes" },
            actionId,
            correlationId,
            context,
            events,
            cancellationToken).ConfigureAwait(false);
        steps.Add(doctorRetry.Step);

        var startRetry = await RunOpenClawStepAsync(
            "gateway-start-retry",
            "Gateway start retry",
            new[] { "gateway", "start" },
            actionId,
            correlationId,
            context,
            events,
            cancellationToken).ConfigureAwait(false);
        steps.Add(startRetry.Step);

        var retryWait = await WaitForGatewayReadyAsync(
            actionId,
            correlationId,
            context,
            events,
            cancellationToken,
            "gateway-wait-retry",
            "Gateway readiness retry").ConfigureAwait(false);
        steps.Add(retryWait.Step);

        if (retryWait.State.Active)
        {
            return new GatewayStabilizationResult(null, diagnosticsWarnings, logsResult.Command);
        }

        GatewayWaitResult finalWait = retryWait;

        if (OperatingSystem.IsWindows() && IsDetachedStartAllowed())
        {
            var detachedRetry = TryLaunchDetachedGateway(context, ResolveGatewayPort());
            steps.Add(new RebuildStep(
                "gateway-run-detached-retry",
                "Gateway run detached (retry)",
                detachedRetry ? "success" : "skipped",
                null,
                detachedRetry ? "Detached gateway run launched (retry)." : "Detached gateway run retry unavailable."));

            if (detachedRetry)
            {
                var detachedRetryWait = await WaitForGatewayReadyAsync(
                    actionId,
                    correlationId,
                    context,
                    events,
                    cancellationToken,
                    "gateway-wait-detached-retry",
                    "Gateway readiness (detached retry)").ConfigureAwait(false);
                steps.Add(detachedRetryWait.Step);
                finalWait = detachedRetryWait;
                if (detachedRetryWait.State.Active)
                {
                    return new GatewayStabilizationResult(null, diagnosticsWarnings, logsResult.Command);
                }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var remediationWait = await RunWindowsAutoRemediationAsync(
                actionId,
                correlationId,
                context,
                events,
                cancellationToken,
                steps).ConfigureAwait(false);
            if (remediationWait != null)
            {
                finalWait = remediationWait;
                if (remediationWait.State.Active)
                {
                    return new GatewayStabilizationResult(null, diagnosticsWarnings, logsResult.Command);
                }
            }
        }

        var diagnostics = finalWait.State.Active
            ? null
            : new RebuildGatewayDiagnostics(
                finalWait.State.Summary,
                ExtractServiceEntrypoint(finalWait.State.Command),
                finalWait.State.ServiceExists,
                finalWait.State.Active,
                finalWait.State.Command,
                logsResult.Command,
                null,
                null,
                BuildRemediationSuggestion(finalWait.State, logsResult.Command, null));

        if (!finalWait.State.Active)
        {
            diagnosticsWarnings.Add(new WarningItem("gateway-retry-failed", "Gateway did not become healthy after retrying start."));
        }

        return new GatewayStabilizationResult(diagnostics, diagnosticsWarnings, logsResult.Command);
    }

    private async Task<GatewayWaitResult> WaitForGatewayReadyAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        string stepId,
        string title)
    {
        var (attempts, delay) = GetGatewayWaitPolicy();
        var used = 0;
        var healthy = false;

        for (var i = 0; i < attempts; i++)
        {
            used = i + 1;
            healthy = await ProbeGatewayAsync(ResolveGatewayPort(), cancellationToken).ConfigureAwait(false);
            if (healthy)
            {
                break;
            }

            if (i < attempts - 1)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        var state = await GetGatewayStatusStateAsync(actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
        var detail = healthy
            ? $"Health check ok after {used} check(s); {state.Summary ?? "status unknown"}."
            : $"Health check failed after {used} check(s); {state.Summary ?? "status unknown"}.";
        var status = healthy && state.Active ? "success" : "failed";
        var step = new RebuildStep(stepId, title, status, state.Command?.Command, detail);
        return new GatewayWaitResult(state with { HealthOk = healthy }, step);
    }

    private async Task<GatewayStatusState> GetGatewayStatusStateAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status" }, events, cancellationToken).ConfigureAwait(false);
        var summary = result.Output as OpenClawCommandSummary;
        return ParseGatewayStatus(summary);
    }

    private static GatewayStatusState ParseGatewayStatus(OpenClawCommandSummary? summary)
    {
        if (summary == null)
        {
            return new GatewayStatusState(true, false, "Gateway status unavailable.", null, false);
        }

        var combined = string.Join(' ', summary.StdOut.Concat(summary.StdErr));
        var serviceMissing = combined.Contains("service missing", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("gateway service missing", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("service not installed", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("service unit not found", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("scheduled task (missing)", StringComparison.OrdinalIgnoreCase);
        var inactive = combined.Contains("inactive", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("stopped", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("not running", StringComparison.OrdinalIgnoreCase);
        var active = !inactive && (combined.Contains("active", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("running", StringComparison.OrdinalIgnoreCase));

        var statusSummary = serviceMissing
            ? "gateway service missing"
            : active
                ? "gateway running"
                : "gateway inactive";

        return new GatewayStatusState(!serviceMissing, active, statusSummary, summary, false);
    }

    private static RebuildStep BuildLogsStep(OpenClawCommandSummary logsSummary)
    {
        var status = logsSummary.ExitCode == 0 || logsSummary.TimedOut ? "success" : "failed";
        var detail = logsSummary.TimedOut ? "Log follow timed out (expected for short verification)." : null;
        return new RebuildStep("verify-logs", "Verify logs availability", status, logsSummary.Command, detail);
    }

    private async Task<RebuildGatewayDiagnostics> BuildGatewayDiagnosticsAsync(
        RebuildGatewayDiagnostics? seed,
        OpenClawCommandSummary? gatewayStatus,
        OpenClawCommandSummary? logsStatus,
        BrowserDiagnosticsSummary? browserDiagnostics,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var state = ParseGatewayStatus(gatewayStatus);
        var serviceEntrypoint = seed?.ServiceEntrypoint ?? ExtractServiceEntrypoint(gatewayStatus);
        var serviceTaskStatus = seed?.ServiceTaskStatus ?? await QueryServiceTaskStatusAsync(
            gatewayStatus,
            events,
            cancellationToken).ConfigureAwait(false);

        var gatewaySummary = gatewayStatus ?? seed?.GatewayStatus;
        var logsSummary = logsStatus ?? seed?.LogsStatus;
        var browserSummary = browserDiagnostics ?? seed?.BrowserDiagnostics;
        var remediation = BuildRemediationSuggestion(state, logsSummary, browserSummary);

        return new RebuildGatewayDiagnostics(
            seed?.ServiceStatus ?? state.Summary,
            serviceEntrypoint,
            state.ServiceExists,
            state.Active,
            gatewaySummary,
            logsSummary,
            browserSummary,
            serviceTaskStatus,
            remediation);
    }

    private async Task<CommandOutputSummary?> QueryServiceTaskStatusAsync(
        OpenClawCommandSummary? statusSummary,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        string? command = null;
        List<string>? args = null;

        if (OperatingSystem.IsWindows())
        {
            var taskName = ExtractTaskName(statusSummary);
            if (!string.IsNullOrWhiteSpace(taskName))
            {
                command = "schtasks";
                args = new List<string> { "/Query", "/TN", taskName, "/FO", "LIST", "/V" };
            }
            else if (LooksLikeScheduledTask(statusSummary))
            {
                command = "schtasks";
                args = new List<string> { "/Query", "/TN", "OpenClaw Gateway", "/FO", "LIST", "/V" };
            }
            else
            {
                var serviceName = ExtractServiceName(statusSummary);
                if (!string.IsNullOrWhiteSpace(serviceName))
                {
                    command = "sc.exe";
                    args = new List<string> { "query", serviceName };
                }
                else
                {
                    command = "powershell";
                    args = new List<string>
                    {
                        "-NoProfile",
                        "-Command",
                        "Get-Service | Where-Object { $_.Name -match 'openclaw' -or $_.DisplayName -match 'openclaw' } | Format-List Name,DisplayName,Status,StartType; " +
                        "Get-ScheduledTask | Where-Object { $_.TaskName -match 'openclaw' } | Format-List TaskName,State,Actions"
                    };
                }
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var serviceName = ExtractServiceName(statusSummary) ?? "openclaw-gateway";
            command = "systemctl";
            args = new List<string> { "status", serviceName, "--no-pager" };
        }
        else if (OperatingSystem.IsMacOS())
        {
            command = "launchctl";
            args = new List<string> { "list" };
        }

        if (command == null || args == null)
        {
            return null;
        }

        var spec = new ProcessRunSpec(command, args)
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
        return await RunDiagnosticProcessAsync("rebuild-diagnostics", Guid.NewGuid(), spec, events, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandOutputSummary> RunDiagnosticProcessAsync(
        string actionId,
        Guid correlationId,
        ProcessRunSpec spec,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(actionId, correlationId, spec, events, cancellationToken).ConfigureAwait(false);
        var commandLine = string.Join(' ', new[] { spec.FileName }.Concat(spec.Arguments));
        return new CommandOutputSummary(
            commandLine,
            result.ExitCode,
            result.StdOut,
            result.StdErr);
    }

    private static (int Attempts, TimeSpan Delay) GetGatewayWaitPolicy()
    {
        var attempts = ReadIntEnv("RECLAW_GATEWAY_WAIT_ATTEMPTS", 10);
        var delayMs = ReadIntEnv("RECLAW_GATEWAY_WAIT_DELAY_MS", 1000);
        attempts = Math.Max(1, attempts);
        delayMs = Math.Max(1, delayMs);
        return (attempts, TimeSpan.FromMilliseconds(delayMs));
    }

    private static int ReadIntEnv(string key, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static int ResolveGatewayPort()
    {
        var raw = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_PORT");
        return int.TryParse(raw, out var port) && port > 0 ? port : 18789;
    }

    private static bool IsDetachedStartAllowed()
    {
        var env = Environment.GetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START");
        if (string.IsNullOrWhiteSpace(env)) return true;
        return !string.Equals(env, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(env, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryLaunchDetachedGateway(ActionContext context, int port)
    {
        return GatewayLaunchHelper.TryLaunchDetachedGateway(context, port);
    }

    private async Task<GatewayWaitResult?> RunWindowsAutoRemediationAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        List<RebuildStep> steps)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        steps.Add(await RunWindowsAutostartCleanupAsync(actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false));
        steps.Add(await RunWindowsKillGatewayProcessesAsync(actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false));

        var installResult = await TryRunOpenClawStepAsync(
            "gateway-install-force",
            "Gateway install (force)",
            new[] { "gateway", "install", "--force" },
            actionId,
            correlationId,
            context,
            events,
            cancellationToken).ConfigureAwait(false);
        if (installResult.Step != null)
        {
            steps.Add(installResult.Step);
        }

        var startResult = await TryRunOpenClawStepAsync(
            "gateway-start-force",
            "Gateway start (force)",
            new[] { "gateway", "start" },
            actionId,
            correlationId,
            context,
            events,
            cancellationToken).ConfigureAwait(false);
        if (startResult.Step != null)
        {
            steps.Add(startResult.Step);
        }

        var wait = await WaitForGatewayReadyAsync(
            actionId,
            correlationId,
            context,
            events,
            cancellationToken,
            "gateway-wait-remediation",
            "Gateway readiness (remediation)").ConfigureAwait(false);
        steps.Add(wait.Step);
        return wait;
    }

    private async Task<RebuildStep> RunWindowsAutostartCleanupAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        const string stepId = "gateway-autostart-disable";
        const string title = "Disable gateway autostart";

        if (!OperatingSystem.IsWindows())
        {
            return new RebuildStep(stepId, title, "skipped", null, "Autostart cleanup not required on non-Windows.");
        }

        var notes = new List<string>();
        var taskNames = new[] { "OpenClaw Gateway", "OpenClawGateway", "OpenClawGatewayTask" };

        foreach (var name in taskNames)
        {
            var query = await RunProcessAsync(
                actionId,
                correlationId,
                new ProcessRunSpec("schtasks", new[] { "/Query", "/TN", name }, Timeout: TimeSpan.FromSeconds(5)),
                events,
                cancellationToken).ConfigureAwait(false);
            if (query.ExitCode != 0)
            {
                continue;
            }

            notes.Add($"Removed scheduled task \"{name}\".");
            await RunProcessAsync(
                actionId,
                correlationId,
                new ProcessRunSpec("schtasks", new[] { "/End", "/TN", name, "/F" }, Timeout: TimeSpan.FromSeconds(5)),
                events,
                cancellationToken).ConfigureAwait(false);
            await RunProcessAsync(
                actionId,
                correlationId,
                new ProcessRunSpec("schtasks", new[] { "/Delete", "/TN", name, "/F" }, Timeout: TimeSpan.FromSeconds(5)),
                events,
                cancellationToken).ConfigureAwait(false);
        }

        await RunProcessAsync(
            actionId,
            correlationId,
            new ProcessRunSpec("sc", new[] { "stop", "OpenClawGateway" }, Timeout: TimeSpan.FromSeconds(5)),
            events,
            cancellationToken).ConfigureAwait(false);
        await RunProcessAsync(
            actionId,
            correlationId,
            new ProcessRunSpec("sc", new[] { "delete", "OpenClawGateway" }, Timeout: TimeSpan.FromSeconds(5)),
            events,
            cancellationToken).ConfigureAwait(false);

        var startupShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs",
            "Startup",
            "OpenClaw Gateway.lnk");

        if (!string.IsNullOrWhiteSpace(startupShortcut) && File.Exists(startupShortcut))
        {
            try
            {
                File.Delete(startupShortcut);
                notes.Add("Removed startup shortcut OpenClaw Gateway.lnk.");
            }
            catch (Exception ex)
            {
                notes.Add($"Could not remove startup shortcut: {ex.Message}");
            }
        }

        var detail = notes.Count > 0 ? string.Join(' ', notes) : "No OpenClaw gateway autostart tasks or shortcuts were found.";
        return new RebuildStep(stepId, title, "success", null, detail);
    }

    private async Task<RebuildStep> RunWindowsKillGatewayProcessesAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        const string stepId = "gateway-kill-processes";
        const string title = "Kill gateway processes";

        if (!OperatingSystem.IsWindows())
        {
            return new RebuildStep(stepId, title, "skipped", null, "Process cleanup not required on non-Windows.");
        }

        var powerShell = ResolvePowerShellExecutable();
        var script = string.Join("; ", new[]
        {
            "$pids = @()",
            "$pids += Get-NetTCPConnection -LocalPort 18789 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess",
            "$pids += Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'gateway|openclaw' -or $_.Path -match 'openclaw' -or $_.CommandLine -match 'openclaw.*gateway' } | Select-Object -ExpandProperty Id",
            "$pids | Sort-Object -Unique | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }"
        });

        var psResult = await RunProcessAsync(
            actionId,
            correlationId,
            new ProcessRunSpec(
                powerShell,
                new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script },
                Timeout: TimeSpan.FromSeconds(8)),
            events,
            cancellationToken).ConfigureAwait(false);

        if (psResult.ExitCode != 0 && !psResult.TimedOut)
        {
            await RunProcessAsync(
                actionId,
                correlationId,
                new ProcessRunSpec(
                    "cmd.exe",
                    new[] { "/c", "for /f \"tokens=5\" %p in ('netstat -ano ^| find \"18789\"') do taskkill /PID %p /F" },
                    Timeout: TimeSpan.FromSeconds(8)),
                events,
                cancellationToken).ConfigureAwait(false);
            await RunProcessAsync(
                actionId,
                correlationId,
                new ProcessRunSpec("taskkill", new[] { "/IM", "openclaw.exe", "/F" }, Timeout: TimeSpan.FromSeconds(8)),
                events,
                cancellationToken).ConfigureAwait(false);
            await RunProcessAsync(
                actionId,
                correlationId,
                new ProcessRunSpec("taskkill", new[] { "/IM", "node.exe", "/F" }, Timeout: TimeSpan.FromSeconds(8)),
                events,
                cancellationToken).ConfigureAwait(false);
        }

        return new RebuildStep(stepId, title, "success", null, "Attempted to kill gateway/OpenClaw processes and port 18789 listeners.");
    }

    private async Task<ProcessResult> RunProcessAsync(
        string actionId,
        Guid correlationId,
        ProcessRunSpec spec,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        try
        {
            return await processRunner.RunAsync(actionId, correlationId, spec, events, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new ProcessResult(-1, false, Array.Empty<string>(), Array.Empty<string>(), 0, 0, false);
        }
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
            if (string.Equals(candidate, "powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "powershell.exe";
    }

    private static RebuildStep? EnsureGatewayModeLocal(string openClawHome)
    {
        if (string.IsNullOrWhiteSpace(openClawHome))
        {
            return null;
        }

        var configPath = Path.Combine(openClawHome, "openclaw.json");
        if (!File.Exists(configPath))
        {
            return new RebuildStep("gateway-mode", "Ensure gateway mode", "skipped", null, "OpenClaw config missing; gateway mode unchanged.");
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
            if (node == null)
            {
                return new RebuildStep("gateway-mode", "Ensure gateway mode", "failed", null, "OpenClaw config is not a JSON object.");
            }

            var gateway = node["gateway"] as JsonObject ?? new JsonObject();
            var mode = gateway["mode"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(mode))
            {
                return new RebuildStep("gateway-mode", "Ensure gateway mode", "skipped", null, $"Gateway mode already set to '{mode}'.");
            }

            gateway["mode"] = "local";
            node["gateway"] = gateway;
            var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
            return new RebuildStep("gateway-mode", "Ensure gateway mode", "success", null, "Set gateway.mode=local.");
        }
        catch (Exception ex)
        {
            return new RebuildStep("gateway-mode", "Ensure gateway mode", "failed", null, ex.Message);
        }
    }

    private static (int Scanned, int Removed) CleanupStaleGatewayLocks(string? openClawHome)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "openclaw");
        if (!Directory.Exists(tmp)) return (0, 0);

        var removed = 0;
        var scanned = 0;
        var targetConfig = !string.IsNullOrWhiteSpace(openClawHome)
            ? Path.Combine(openClawHome, "openclaw.json")
            : null;

        foreach (var file in Directory.EnumerateFiles(tmp, "gateway*.lock"))
        {
            scanned++;
            try
            {
                var raw = File.ReadAllText(file);
                var parsed = JsonDocument.Parse(raw);
                using (parsed)
                {
                    var root = parsed.RootElement;
                    if (targetConfig != null && root.TryGetProperty("configPath", out var cfg))
                    {
                        var lockConfig = cfg.GetString();
                        if (!string.IsNullOrWhiteSpace(lockConfig)
                            && !string.Equals(Path.GetFullPath(lockConfig), Path.GetFullPath(targetConfig), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    if (root.TryGetProperty("pid", out var pidElement)
                        && pidElement.TryGetInt32(out var pid)
                        && pid > 0
                        && IsProcessRunning(pid))
                    {
                        continue;
                    }
                }
            }
            catch
            {
                // ignore, treat as stale
            }

            try
            {
                File.Delete(file);
                removed++;
            }
            catch
            {
                // ignore
            }
        }

        return (scanned, removed);
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ProbeGatewayAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(GetGatewayProbeTimeoutMs()));
            var response = await Http.GetAsync($"http://127.0.0.1:{port}/healthz", cts.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            return code >= 200 && code < 500;
        }
        catch
        {
            return false;
        }
    }

    private static int GetGatewayProbeTimeoutMs()
    {
        var raw = Environment.GetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_TIMEOUT_MS");
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 1200;
    }

    private static string? ExtractServiceEntrypoint(OpenClawCommandSummary? summary)
    {
        if (summary == null) return null;
        foreach (var line in summary.StdOut.Concat(summary.StdErr))
        {
            if (line.Contains("entrypoint", StringComparison.OrdinalIgnoreCase)
                || line.Contains("entry point", StringComparison.OrdinalIgnoreCase)
                || line.Contains("exec", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.IndexOf(':');
                if (idx >= 0 && idx + 1 < line.Length)
                {
                    var value = line[(idx + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }

        return null;
    }

    private static string? ExtractServiceName(OpenClawCommandSummary? summary)
    {
        if (summary == null) return null;
        foreach (var line in summary.StdOut.Concat(summary.StdErr))
        {
            if (line.Contains("scheduled task", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = System.Text.RegularExpressions.Regex.Match(line, @"service(?: name)?\s*[:=]\s*(?<name>.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups["name"].Value.Trim();
            }
        }
        return null;
    }

    private static string? ExtractTaskName(OpenClawCommandSummary? summary)
    {
        if (summary == null) return null;
        foreach (var line in summary.StdOut.Concat(summary.StdErr))
        {
            var commandMatch = System.Text.RegularExpressions.Regex.Match(line, @"schtasks\s+/Query\s+/TN\s+\""?([^\""]+)\""?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (commandMatch.Success)
            {
                var value = commandMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }

            var taskNameMatch = System.Text.RegularExpressions.Regex.Match(line, @"TaskName\s*[:=]\s*\\?(?<name>.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (taskNameMatch.Success)
            {
                var value = taskNameMatch.Groups["name"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }

            var match = System.Text.RegularExpressions.Regex.Match(line, @"scheduled task\s*[:=]\s*(?<name>.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups["name"].Value.Trim();
            }
        }
        return null;
    }

    private static bool LooksLikeScheduledTask(OpenClawCommandSummary? summary)
    {
        if (summary == null) return false;
        return summary.StdOut.Concat(summary.StdErr)
            .Any(line => line.Contains("scheduled task", StringComparison.OrdinalIgnoreCase));
    }

    private static string? BuildRemediationSuggestion(
        GatewayStatusState state,
        OpenClawCommandSummary? logsStatus,
        BrowserDiagnosticsSummary? browserDiagnostics)
    {
        if (!state.ServiceExists)
        {
            return "Gateway service missing. Run 'openclaw gateway install' and retry start.";
        }

        if (!state.Active)
        {
            var logHint = logsStatus == null || logsStatus.ExitCode != 0
                ? "Check gateway logs and re-run 'openclaw gateway start'."
                : "Review gateway logs and re-run 'openclaw gateway start'.";
            return $"Gateway service installed but not running. {logHint}";
        }

        if (browserDiagnostics?.RemoteUnsafe == true)
        {
            return "Browser diagnostics indicate unsafe remote access. Prefer HTTPS or a secure tunnel.";
        }

        return null;
    }
    private async Task<(RebuildStep? Step, ActionResult Result)> TryRunOpenClawStepAsync(
        string stepId,
        string title,
        string[] args,
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(actionId, correlationId, context, args, events, cancellationToken).ConfigureAwait(false);
            var command = (result.Output as OpenClawCommandSummary)?.Command;
            var status = result.Success ? "success" : "failed";
            return (new RebuildStep(stepId, title, status, command, result.Error), result);
        }
        catch (Exception ex)
        {
            return (new RebuildStep(stepId, title, "failed", null, ex.Message), new ActionResult(false, Error: ex.Message));
        }
    }

    private async Task<(RebuildStep Step, ActionResult Result)> RunOpenClawStepAsync(
        string stepId,
        string title,
        string[] args,
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(actionId, correlationId, context, args, events, cancellationToken).ConfigureAwait(false);
        var command = (result.Output as OpenClawCommandSummary)?.Command;
        var status = result.Success ? "success" : "failed";
        return (new RebuildStep(stepId, title, status, command, result.Error), result);
    }

    private async Task<bool> ExecuteRuntimeStrategyAsync(
        RuntimePlan strategy,
        bool cleanInstall,
        bool configNewerThanRuntime,
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        List<RebuildStep> steps,
        List<string> removedItems,
        List<string> installedItems,
        List<WarningItem> warnings)
    {
        switch (strategy.Kind)
        {
            case RuntimeStrategyKind.RepoClone:
            {
                if (string.IsNullOrWhiteSpace(strategy.RepoRoot) || string.IsNullOrWhiteSpace(strategy.OriginUrl))
                {
                    steps.Add(new RebuildStep("runtime-clone", "Clone runtime", "failed", null, "Repo root or origin URL missing."));
                    return false;
                }

                var backupRoot = $"{strategy.RepoRoot}.rebuild.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
                try
                {
                    if (Directory.Exists(strategy.RepoRoot))
                    {
                        Directory.Move(strategy.RepoRoot, backupRoot);
                        removedItems.Add(strategy.RepoRoot);
                        steps.Add(new RebuildStep("runtime-remove", "Remove runtime", "success", null, backupRoot));
                    }
                }
                catch (Exception ex)
                {
                    steps.Add(new RebuildStep("runtime-remove", "Remove runtime", "failed", null, ex.Message));
                    return false;
                }

                var cloneResult = await RunProcessStepAsync(
                    "runtime-clone",
                    "Clone runtime",
                    new ProcessRunSpec("git", new[] { "clone", strategy.OriginUrl, strategy.RepoRoot }),
                    actionId,
                    correlationId,
                    events,
                    cancellationToken).ConfigureAwait(false);
                steps.Add(cloneResult.Step);
                if (cloneResult.Step.Status != "success")
                {
                    return false;
                }
                installedItems.Add(strategy.RepoRoot);
                return true;
            }
            case RuntimeStrategyKind.PathUpdate:
            {
                if (cleanInstall)
                {
                    var uninstallResult = await TryRunOpenClawStepAsync(
                        "runtime-uninstall",
                        "Runtime uninstall",
                        new[] { "uninstall" },
                        actionId,
                        correlationId,
                        context,
                        events,
                        cancellationToken).ConfigureAwait(false);
                    if (uninstallResult.Step != null)
                    {
                        steps.Add(uninstallResult.Step);
                        if (uninstallResult.Step.Status == "success")
                        {
                            removedItems.Add("openclaw-runtime");
                        }
                        else
                        {
                            warnings.Add(new WarningItem("runtime-uninstall-failed", "Runtime uninstall failed; continuing with update."));
                        }
                    }
                }

                if (configNewerThanRuntime)
                {
                    warnings.Add(new WarningItem("runtime-update-required", "Config is newer than runtime; updating runtime."));
                }

                var result = await RunOpenClawStepAsync(
                    "runtime-update",
                    "Update runtime",
                    new[] { "update" },
                    actionId,
                    correlationId,
                    context,
                    events,
                    cancellationToken).ConfigureAwait(false);
                steps.Add(result.Step);
                if (!result.Result.Success)
                {
                    return false;
                }
                installedItems.Add("openclaw update");
                return true;
            }
            case RuntimeStrategyKind.MissingInstall:
            {
                var npmCommand = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
                var installResult = await RunProcessStepAsync(
                    "runtime-install",
                    "Install runtime",
                    new ProcessRunSpec(npmCommand, new[] { "install", "-g", "openclaw@latest" }),
                    actionId,
                    correlationId,
                    events,
                    cancellationToken).ConfigureAwait(false);
                steps.Add(installResult.Step);
                if (installResult.Step.Status != "success")
                {
                    return false;
                }
                installedItems.Add("openclaw@latest");
                return true;
            }
            default:
                steps.Add(new RebuildStep("runtime", "Runtime strategy", "failed", null, "Unsupported runtime strategy."));
                return false;
        }
    }

    private async Task<bool> ExecuteResetAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        ResetMode resetMode,
        IProgress<ActionEvent> events,
        List<RebuildStep> steps,
        List<string> removedItems)
    {
        var plan = resetService.BuildPlan(new ResetContext(
            context.OpenClawHome,
            context.ConfigDirectory,
            context.DataDirectory,
            context.BackupDirectory), resetMode);

        if (plan.DeletePaths.Count == 0)
        {
            steps.Add(new RebuildStep("reset", "Local reset", "skipped", null, "No local paths selected for reset."));
            return true;
        }

        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Reset", $"{plan.DeletePaths.Count} paths"));
        try
        {
            await resetService.ExecuteAsync(plan).ConfigureAwait(false);
            steps.Add(new RebuildStep("reset", "Local reset", "success", null, $"{plan.DeletePaths.Count} paths removed"));
            removedItems.AddRange(plan.DeletePaths);
            return true;
        }
        catch (Exception ex)
        {
            steps.Add(new RebuildStep("reset", "Local reset", "failed", null, ex.Message));
            return false;
        }
    }
    private static (string Scope, IReadOnlyList<string> Tokens) BuildRebuildScope(OpenClawRebuildInput input, List<WarningItem> warnings)
    {
        var tokens = new List<string>();
        if (input.PreserveConfig) tokens.Add("config");
        if (input.PreserveCredentials) tokens.Add("creds");
        if (input.PreserveSessions) tokens.Add("sessions");
        if (input.PreserveWorkspace) tokens.Add("workspace");

        if (tokens.Count == 0)
        {
            warnings.Add(new WarningItem("rebuild-no-preserve", "No preserve options selected; defaulting to config restore."));
            tokens.Add("config");
        }

        var scope = tokens.Count == 4 ? "full" : string.Join('+', tokens);
        return (scope, tokens);
    }

    private static string BuildRebuildSnapshotPath(string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"openclaw_rebuild_{timestamp}.tar.gz";
        return Path.Combine(backupDirectory, fileName);
    }

    private static RuntimePlan ResolveRuntimePlan(ActionContext context, OpenClawInventory? inventory)
    {
        if (inventory?.CandidateRuntimes is { Count: > 0 } candidates)
        {
            var preferred = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Kind, "configured", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Kind, "path", StringComparison.OrdinalIgnoreCase));

            if (preferred != null)
            {
                return new RuntimePlan(RuntimeStrategyKind.PathUpdate, $"path-update ({preferred.Kind})", null, null);
            }

            if (inventory.ActiveRuntime is { } activeRuntime && string.Equals(activeRuntime.Kind, "repo-fallback", StringComparison.OrdinalIgnoreCase))
            {
                return new RuntimePlan(RuntimeStrategyKind.MissingInstall, "missing-install (repo-fallback)", null, null);
            }
        }

        var resolved = OpenClawLocator.ResolveWithSource(context);
        if (resolved == null)
        {
            return new RuntimePlan(RuntimeStrategyKind.MissingInstall, "missing-install", null, null);
        }

        var hasEntry = resolved.Command.BaseArgs.Any(arg =>
            arg.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
            arg.EndsWith(".js", StringComparison.OrdinalIgnoreCase));

        if (hasEntry)
        {
            var repoRoot = resolved.Command.WorkingDirectory ?? Path.GetDirectoryName(resolved.Command.BaseArgs.FirstOrDefault() ?? string.Empty);
            var origin = TryReadGitOrigin(repoRoot);
            return new RuntimePlan(RuntimeStrategyKind.RepoClone, $"repo-clone ({resolved.Source})", repoRoot, origin);
        }

        return new RuntimePlan(RuntimeStrategyKind.PathUpdate, $"path-update ({resolved.Source})", null, null);
    }

    private static bool IsConfigNewerThanRuntime(OpenClawDetectionSummary? detection, List<WarningItem> warnings)
    {
        if (detection == null) return false;
        if (!Version.TryParse(detection.ConfigVersion, out var parsedConfig)) return false;
        if (!Version.TryParse(detection.RuntimeVersion, out var parsedRuntime)) return false;
        if (parsedConfig <= parsedRuntime) return false;

        warnings.Add(new WarningItem("config-newer-than-runtime", $"Config version {parsedConfig} is newer than runtime {parsedRuntime}."));
        return true;
    }

    private static string? TryReadGitOrigin(string? repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot)) return null;
        var configPath = Path.Combine(repoRoot, ".git", "config");
        if (!File.Exists(configPath)) return null;

        string? currentRemote = null;
        foreach (var line in File.ReadLines(configPath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[remote", StringComparison.OrdinalIgnoreCase))
            {
                currentRemote = trimmed.Contains("\"origin\"", StringComparison.OrdinalIgnoreCase) ? "origin" : null;
                continue;
            }

            if (currentRemote == "origin" && trimmed.StartsWith("url", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split('=', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    return parts[1].Trim();
                }
            }
        }

        return null;
    }
    private static OpenClawRebuildSummary BuildSummary(
        string backupPath,
        (string Scope, IReadOnlyList<string> Tokens) scopeInfo,
        ResetMode resetMode,
        string runtimeStrategy,
        IReadOnlyList<string> removedItems,
        IReadOnlyList<string> installedItems,
        IReadOnlyList<RebuildStep> steps,
        RebuildVerificationSummary verification,
        OpenClawInventory? inventory,
        string? journalPath)
    {
        return new OpenClawRebuildSummary(
            backupPath,
            scopeInfo.Scope,
            scopeInfo.Tokens,
            resetMode,
            runtimeStrategy,
            removedItems,
            installedItems,
            steps,
            verification,
            inventory,
            journalPath);
    }

    private ActionResult BuildResult(
        bool success,
        string? error,
        string backupPath,
        (string Scope, IReadOnlyList<string> Tokens) scopeInfo,
        ResetMode resetMode,
        string runtimeStrategy,
        IReadOnlyList<string> removedItems,
        IReadOnlyList<string> installedItems,
        IReadOnlyList<RebuildStep> steps,
        RebuildVerificationSummary verification,
        OpenClawInventory? inventory,
        IReadOnlyList<WarningItem> warnings,
        ActionContext context)
    {
        var summary = BuildSummary(
            backupPath,
            scopeInfo,
            resetMode,
            runtimeStrategy,
            NormalizeItems(removedItems),
            NormalizeItems(installedItems),
            steps,
            verification,
            inventory,
            GetJournalPath(context));
        return new ActionResult(success, Output: summary, Error: error, Warnings: warnings);
    }

    private async Task<(RebuildStep Step, ProcessResult Result)> RunProcessStepAsync(
        string stepId,
        string title,
        ProcessRunSpec spec,
        string actionId,
        Guid correlationId,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var commandLine = string.Join(' ', new[] { spec.FileName }.Concat(spec.Arguments));
        var result = await processRunner.RunAsync(actionId, correlationId, spec, events, cancellationToken).ConfigureAwait(false);
        var status = result.ExitCode == 0 ? "success" : "failed";
        var detail = result.ExitCode == 0 ? null : $"Exit {result.ExitCode}";
        return (new RebuildStep(stepId, title, status, commandLine, detail), result);
    }

    private static IReadOnlyList<string> NormalizeItems(IReadOnlyList<string> items)
    {
        return items
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetJournalPath(ActionContext context)
    {
        return Path.Combine(context.DataDirectory, "journal.jsonl");
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

    private static int? ExtractGatewayPort(OpenClawCommandSummary summary)
    {
        foreach (var line in summary.StdOut.Concat(summary.StdErr))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @":(?<port>\d{2,5})");
            if (match.Success && int.TryParse(match.Groups["port"].Value, out var port))
            {
                return port;
            }
        }

        return null;
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

    private sealed record GatewayStatusState(
        bool ServiceExists,
        bool Active,
        string? Summary,
        OpenClawCommandSummary? Command,
        bool HealthOk);

    private sealed record GatewayWaitResult(
        GatewayStatusState State,
        RebuildStep Step);

    private sealed record GatewayStabilizationResult(
        RebuildGatewayDiagnostics? Diagnostics,
        IReadOnlyList<WarningItem> DiagnosticsWarnings,
        OpenClawCommandSummary? LogsSummary);

    private sealed record RuntimePlan(RuntimeStrategyKind Kind, string Description, string? RepoRoot, string? OriginUrl);

    private enum RuntimeStrategyKind
    {
        PathUpdate,
        RepoClone,
        MissingInstall
    }
}
