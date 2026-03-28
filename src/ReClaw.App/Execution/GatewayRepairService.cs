using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Actions;
using ReClaw.Core;

namespace ReClaw.App.Execution;

internal sealed class GatewayRepairService
{
    private static readonly Regex VersionRegex = new(@"(\d+\.\d+\.\d+)", RegexOptions.Compiled);
    private const string GatewayModeUnsetMessage = "gateway.mode is unset; gateway start is blocked. Fix: openclaw config set gateway.mode local (or run openclaw configure).";
    private const string InteractivePromptMessage = "Interactive prompt detected; automatic repair cannot continue. Use Open Terminal or re-run with --non-interactive --yes.";
    private static readonly HttpClient Http = new();
    private static readonly object VersionCacheLock = new();
    private static readonly Dictionary<string, VersionCacheEntry> VersionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOpenClawActionRunner runner;
    private readonly BackupService backupService;
    private readonly Func<ActionContext, IReadOnlyList<OpenClawCandidate>> candidateProvider;

    public GatewayRepairService(
        IOpenClawActionRunner runner,
        BackupService backupService,
        Func<ActionContext, IReadOnlyList<OpenClawCandidate>> candidateProvider)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        this.candidateProvider = candidateProvider ?? throw new ArgumentNullException(nameof(candidateProvider));
    }

    public async Task<ActionResult> RunWithRepairAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        string[] args,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var warnings = new List<WarningItem>();
        var suggestions = new List<string>();
        var notes = new List<string>();
        var steps = new List<GatewayRepairStep>();
        var attempts = new List<GatewayRepairAttempt>();
        var workingContext = context;
        string? snapshotPath = null;
        string? selectedRuntime = null;

        var (detection, statusResult, candidates) = await DetectAsync(workingContext, correlationId, events, cancellationToken).ConfigureAwait(false);
        ReportDetection(actionId, correlationId, detection, events);
        attempts.Add(BuildAttempt("inventory", "Inventory", true, false, $"Candidates: {detection.Candidates.Count}", null));
        attempts.Add(BuildAttempt(
            "gateway-status",
            "Gateway status",
            statusResult.Command?.ExitCode == 0,
            false,
            statusResult.Command == null ? "No status output." : $"Exit {statusResult.Command.ExitCode}",
            statusResult.Command?.Command));
        var hasConfig = Version.TryParse(detection.ConfigVersion, out var parsedConfig);
        var hasRuntime = Version.TryParse(detection.RuntimeVersion, out var parsedRuntime);
        var configNewerThanRuntime = hasConfig && hasRuntime && parsedConfig > parsedRuntime;
        if (configNewerThanRuntime)
        {
            warnings.Add(new WarningItem("config-newer-than-runtime", $"Config version {parsedConfig} is newer than runtime {parsedRuntime}."));
            AddSuggestion(suggestions, $"Update/choose a runtime >= {parsedConfig} and rerun gateway start.");
            AddNote(notes, $"Config {parsedConfig} is newer than runtime {parsedRuntime}; select a newer OpenClaw runtime.");
        }
        attempts.Add(BuildAttempt(
            "preflight-version-check",
            "Preflight version check",
            !configNewerThanRuntime,
            false,
            configNewerThanRuntime ? $"Config {parsedConfig} newer than runtime {parsedRuntime}." : "Runtime and config version compatible.",
            null));

        if (IsDiagnosticOnly(actionId))
        {
            if (!detection.GatewayServiceExists)
            {
                warnings.Add(new WarningItem("gateway-service-missing", "Gateway service is missing."));
            }
            return await RunDiagnosticOnlyAsync(
                actionId,
                correlationId,
                workingContext,
                args,
                events,
                cancellationToken,
                detection,
                statusResult,
                warnings,
                suggestions,
                notes,
                attempts,
                candidates).ConfigureAwait(false);
        }

        if (!detection.GatewayServiceExists)
        {
            warnings.Add(new WarningItem("gateway-service-missing", "Gateway service is missing; attempting repair."));
            AddSuggestion(suggestions, "openclaw gateway install");
        }

        var tokenResult = EnsureGatewayToken(workingContext.OpenClawHome);
        if (tokenResult.Created)
        {
            warnings.Add(new WarningItem("gateway-token-created", "Gateway auth token was missing and has been generated."));
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Gateway Token", "Generated missing gateway token."));
        }

        var cleaned = CleanupStaleGatewayLocks(workingContext.OpenClawHome);
        if (cleaned.Removed > 0)
        {
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Gateway Locks", $"Removed {cleaned.Removed} stale lock(s)."));
        }

        var modeUnsetDetected = IsGatewayModeUnset(statusResult.Command);
        if (ShouldAutoFix(actionId, statusResult, modeUnsetDetected, configNewerThanRuntime))
        {
            return await RunGatewayAutoFixLadderAsync(
                actionId,
                correlationId,
                workingContext,
                events,
                cancellationToken,
                detection,
                statusResult,
                steps,
                attempts,
                warnings,
                suggestions,
                notes,
                candidates,
                snapshotPath,
                selectedRuntime,
                modeUnsetDetected).ConfigureAwait(false);
        }

        var initialAttempt = await TryGatewayActionAsync(actionId, correlationId, workingContext, args, events, cancellationToken).ConfigureAwait(false);
        if (initialAttempt.Success)
        {
            var initialCommand = initialAttempt.Output as OpenClawCommandSummary;
            CollectGuidance(initialCommand, suggestions, notes, warnings);
            return BuildFinalResult(actionId, detection, steps, attempts, initialAttempt, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates);
        }

        var initialSummary = initialAttempt.Output as OpenClawCommandSummary;
        steps.Add(new GatewayRepairStep("initial", "failed", initialAttempt.Error, initialSummary));
        attempts.Add(BuildAttempt("initial", "Initial command", false, false, initialAttempt.Error ?? "Command failed.", initialSummary?.Command));
        CollectGuidance(initialSummary, suggestions, notes, warnings);
        if (IsGatewayModeUnset(initialSummary))
        {
            return await RunGatewayAutoFixLadderAsync(
                actionId,
                correlationId,
                workingContext,
                events,
                cancellationToken,
                detection,
                statusResult,
                steps,
                attempts,
                warnings,
                suggestions,
                notes,
                candidates,
                snapshotPath,
                selectedRuntime,
                modeUnsetDetected: true).ConfigureAwait(false);
        }

        if (actionId == "gateway-logs" && !detection.GatewayActive)
        {
            warnings.Add(new WarningItem("gateway-inactive", "Gateway is not active; attempting repair before log follow."));
        }

        var doctorResult = await RunStepAsync("doctor", new[] { "doctor", "--non-interactive", "--yes" }, actionId, correlationId, workingContext, events, cancellationToken).ConfigureAwait(false);
        steps.Add(doctorResult.Step);
        attempts.Add(BuildAttempt("doctor", "Doctor", doctorResult.Step.Status == "success", true, doctorResult.Step.Detail ?? "Doctor executed.", doctorResult.Command?.Command));
        CollectGuidance(doctorResult.Command, suggestions, notes, warnings);
        if (IsInteractivePrompt(doctorResult.Command))
        {
            var blocked = new ActionResult(false, Output: doctorResult.Command, Error: InteractivePromptMessage);
            return BuildFinalResult(actionId, detection, steps, attempts, blocked, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates, outcome: "failed", success: false);
        }
        if (IsGatewayModeUnset(doctorResult.Command))
        {
            return await RunGatewayAutoFixLadderAsync(
                actionId,
                correlationId,
                workingContext,
                events,
                cancellationToken,
                detection,
                statusResult,
                steps,
                attempts,
                warnings,
                suggestions,
                notes,
                candidates,
                snapshotPath,
                selectedRuntime,
                modeUnsetDetected: true).ConfigureAwait(false);
        }
        if (doctorResult.Command != null)
        {
            var afterDoctor = await TryGatewayActionAsync(actionId, correlationId, workingContext, args, events, cancellationToken).ConfigureAwait(false);
            if (afterDoctor.Success)
            {
                return BuildFinalResult(actionId, detection, steps, attempts, afterDoctor, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates, outcome: "fixed");
            }
        }

        var startup = await TryGatewayStartupAsync(actionId, correlationId, workingContext, events, cancellationToken).ConfigureAwait(false);
        if (startup.Started)
        {
            if (startup.HealthCheckFailed)
            {
                warnings.Add(new WarningItem("gateway-health-check-failed", "Gateway started but health check did not confirm readiness."));
            }
            var startResult = startup.CommandResult ?? new ActionResult(true);
            attempts.Add(BuildAttempt("gateway-start", "Gateway start", true, true, "Gateway started.", startResult.Output is OpenClawCommandSummary summary ? summary.Command : null));
            return BuildFinalResult(actionId, detection, steps, attempts, startResult, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates, outcome: startup.HealthCheckFailed ? "started-with-warnings" : "fixed");
        }

        var snapshotCreated = false;
        if (Directory.Exists(workingContext.OpenClawHome) && !IsSnapshotSkipRequested())
        {
            snapshotPath = BuildGatewayRepairSnapshotPath(workingContext.BackupDirectory);
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Snapshot", snapshotPath));
            await backupService.CreateBackupAsync(workingContext.OpenClawHome, snapshotPath, password: null, scope: "full").ConfigureAwait(false);
            warnings.Add(new WarningItem("rollback-available", $"Gateway repair snapshot created: {snapshotPath}"));
            snapshotCreated = true;
            attempts.Add(BuildAttempt("snapshot", "Snapshot", true, true, $"Snapshot created at {snapshotPath}.", snapshotPath));
        }

        if (snapshotCreated)
        {
            var fixResult = await RunStepAsync("doctor-fix", new[] { "doctor", "--fix", "--non-interactive", "--yes" }, actionId, correlationId, workingContext, events, cancellationToken).ConfigureAwait(false);
            steps.Add(fixResult.Step);
            attempts.Add(BuildAttempt("doctor-fix", "Doctor fix", fixResult.Step.Status == "success", true, fixResult.Step.Detail ?? "Doctor fix executed.", fixResult.Command?.Command));
            CollectGuidance(fixResult.Command, suggestions, notes, warnings);
            if (IsInteractivePrompt(fixResult.Command))
            {
                var blocked = new ActionResult(false, Output: fixResult.Command, Error: InteractivePromptMessage);
                return BuildFinalResult(actionId, detection, steps, attempts, blocked, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates, outcome: "failed", success: false);
            }
            if (fixResult.Command != null)
            {
                var afterFix = await TryGatewayActionAsync(actionId, correlationId, workingContext, args, events, cancellationToken).ConfigureAwait(false);
                if (afterFix.Success)
                {
                    return BuildFinalResult(actionId, detection, steps, attempts, afterFix, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates, outcome: "fixed-with-warnings");
                }
            }
        }
        else
        {
            steps.Add(new GatewayRepairStep("doctor-fix", "skipped", "Snapshot unavailable; fix skipped.", null));
            attempts.Add(BuildAttempt("doctor-fix", "Doctor fix", false, false, "Snapshot unavailable; fix skipped.", null));
            attempts.Add(BuildAttempt("snapshot", "Snapshot", false, false, "Snapshot unavailable; fix skipped.", null));
        }

        if (!detection.GatewayServiceExists)
        {
            var installResult = await RunStepAsync("gateway-install", new[] { "gateway", "install" }, actionId, correlationId, workingContext, events, cancellationToken).ConfigureAwait(false);
            steps.Add(installResult.Step);
            attempts.Add(BuildAttempt("gateway-install", "Gateway install", installResult.Step.Status == "success", true, installResult.Step.Detail ?? "Gateway install executed.", installResult.Command?.Command));
            CollectGuidance(installResult.Command, suggestions, notes, warnings);
            if (installResult.Command != null)
            {
                var installStartup = await TryGatewayStartupAsync(actionId, correlationId, workingContext, events, cancellationToken).ConfigureAwait(false);
                if (installStartup.Started)
                {
                    var startResult = installStartup.CommandResult ?? new ActionResult(true);
                    attempts.Add(BuildAttempt("gateway-start", "Gateway start", true, true, "Gateway started after install.", startResult.Output is OpenClawCommandSummary summary ? summary.Command : null));
                    return BuildFinalResult(actionId, detection, steps, attempts, startResult, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates, outcome: "fixed");
                }
                var afterInstallAttemptFallback = await TryGatewayActionAsync(actionId, correlationId, workingContext, args, events, cancellationToken).ConfigureAwait(false);
                if (afterInstallAttemptFallback.Success)
                {
                    return BuildFinalResult(actionId, detection, steps, attempts, afterInstallAttemptFallback, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates, outcome: "fixed");
                }
            }
        }

        var selection = await TrySelectCompatibleRuntimeAsync(actionId, workingContext, correlationId, events, cancellationToken).ConfigureAwait(false);
        if (selection.UpdatedContext != null)
        {
            workingContext = selection.UpdatedContext;
            selectedRuntime = selection.SelectedRuntime;
            steps.Add(selection.Step);
            attempts.Add(BuildAttempt("select-runtime", "Select compatible runtime", selection.Step.Status == "success", true, selection.Step.Detail ?? "Runtime selection applied.", selection.Step.Command?.Command));
            var selectionStartup = await TryGatewayStartupAsync(actionId, correlationId, workingContext, events, cancellationToken).ConfigureAwait(false);
            if (selectionStartup.Started)
            {
                if (selectionStartup.HealthCheckFailed)
                {
                    warnings.Add(new WarningItem("gateway-health-check-failed", "Gateway started but health check did not confirm readiness."));
                }
                var startResult = selectionStartup.CommandResult ?? new ActionResult(true);
                attempts.Add(BuildAttempt("gateway-start", "Gateway start", true, true, "Gateway started after runtime selection.", startResult.Output is OpenClawCommandSummary summary ? summary.Command : null));
                return BuildFinalResult(actionId, detection, steps, attempts, startResult, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates, outcome: selectionStartup.HealthCheckFailed ? "started-with-warnings" : "fixed");
            }
            var afterSelectionFallback = await TryGatewayActionAsync(actionId, correlationId, workingContext, args, events, cancellationToken).ConfigureAwait(false);
            if (afterSelectionFallback.Success)
            {
                return BuildFinalResult(actionId, detection, steps, attempts, afterSelectionFallback, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates, outcome: "fixed");
            }
        }

        if (string.IsNullOrWhiteSpace(detection.ConfigVersion))
        {
            var setupResult = await RunStepAsync("setup", new[] { "setup" }, actionId, correlationId, workingContext, events, cancellationToken).ConfigureAwait(false);
            steps.Add(setupResult.Step);
            attempts.Add(BuildAttempt("setup", "Setup", setupResult.Step.Status == "success", true, setupResult.Step.Detail ?? "Setup executed.", setupResult.Command?.Command));
            CollectGuidance(setupResult.Command, suggestions, notes, warnings);
            if (setupResult.Command != null)
            {
                var setupStartup = await TryGatewayStartupAsync(actionId, correlationId, workingContext, events, cancellationToken).ConfigureAwait(false);
                if (setupStartup.Started)
                {
                    if (setupStartup.HealthCheckFailed)
                    {
                        warnings.Add(new WarningItem("gateway-health-check-failed", "Gateway started but health check did not confirm readiness."));
                    }
                    var startResult = setupStartup.CommandResult ?? new ActionResult(true);
                    attempts.Add(BuildAttempt("gateway-start", "Gateway start", true, true, "Gateway started after setup.", startResult.Output is OpenClawCommandSummary summary ? summary.Command : null));
                    return BuildFinalResult(actionId, detection, steps, attempts, startResult, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates, outcome: setupStartup.HealthCheckFailed ? "started-with-warnings" : "fixed");
                }
                var afterSetupFallback = await TryGatewayActionAsync(actionId, correlationId, workingContext, args, events, cancellationToken).ConfigureAwait(false);
                if (afterSetupFallback.Success)
                {
                    return BuildFinalResult(actionId, detection, steps, attempts, afterSetupFallback, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates, outcome: "fixed");
                }
            }
        }

        if (configNewerThanRuntime && string.IsNullOrWhiteSpace(selectedRuntime))
        {
            warnings.Add(new WarningItem("confirmation-required", "Automatic repair exhausted; destructive recovery requires confirmation."));
            warnings.Add(new WarningItem("destructive-repair", "Reinstall/reclone or restore from backup requires explicit confirmation."));
            return BuildFinalResult(actionId, detection, steps, attempts, initialAttempt, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates, outcome: "confirmation-needed", success: false);
        }

        warnings.Add(new WarningItem("unable-to-repair", "Automatic repair did not resolve the gateway issue."));
        return BuildFinalResult(actionId, detection, steps, attempts, initialAttempt, warnings, snapshotPath, selectedRuntime, suggestions, notes, workingContext, statusResult.Command, candidates, outcome: "unable-to-repair", success: false);
    }

    private async Task<(GatewayRepairStep Step, OpenClawCommandSummary? Command)> RunStepAsync(
        string stepName,
        string[] args,
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Repair", stepName));
        var result = await runner.RunAsync(actionId, correlationId, context, args, events, cancellationToken).ConfigureAwait(false);
        var commandSummary = result.Output as OpenClawCommandSummary;
        var status = result.Success ? "success" : "failed";
        var detail = result.Success ? null : result.Error;
        return (new GatewayRepairStep(stepName, status, detail, commandSummary), commandSummary);
    }

    private static bool IsDiagnosticOnly(string actionId)
    {
        return actionId == "gateway-status"
            || actionId == "gateway-stop"
            || actionId == "gateway-logs";
    }

    private static bool ShouldAutoFix(
        string actionId,
        GatewayStatusResult status,
        bool modeUnsetDetected,
        bool configNewerThanRuntime)
    {
        if (IsDiagnosticOnly(actionId) || actionId == "gateway-stop")
        {
            return false;
        }

        if (modeUnsetDetected)
        {
            return true;
        }

        if (status.Command == null)
        {
            return false;
        }

        if (!status.GatewayActive || !status.GatewayServiceExists)
        {
            return true;
        }

        return configNewerThanRuntime && !status.GatewayActive;
    }

    private async Task<ActionResult> RunDiagnosticOnlyAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        string[] args,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        OpenClawDetectionSummary detection,
        GatewayStatusResult status,
        List<WarningItem> warnings,
        List<string> suggestions,
        List<string> notes,
        List<GatewayRepairAttempt> attempts,
        IReadOnlyList<OpenClawCandidate> candidates)
    {
        var steps = new List<GatewayRepairStep>();
        var statusCommand = status.Command;
        CollectGuidance(statusCommand, suggestions, notes, warnings);

        if (statusCommand != null && statusCommand.ExitCode != 0)
        {
            warnings.Add(new WarningItem("gateway-status-exit", $"Gateway status returned exit code {statusCommand.ExitCode}."));
        }

        if (actionId == "gateway-status")
        {
            var healthy = IsGatewayHealthy(statusCommand);
            if (!healthy)
            {
                warnings.Add(new WarningItem("gateway-inactive", "Gateway is not active."));
            }

            var result = statusCommand != null
                ? new ActionResult(healthy, Output: statusCommand, Error: healthy ? null : "Gateway not healthy.", ExitCode: statusCommand.ExitCode)
                : new ActionResult(healthy, Error: "Gateway status unavailable.");

            return BuildFinalResult(actionId, detection, steps, attempts, result, warnings, snapshotPath: null, selectedRuntime: null, suggestions, notes, context, statusCommand, candidates, outcome: "status", success: healthy);
        }

        if (actionId == "gateway-logs")
        {
            if (!detection.GatewayActive)
            {
                warnings.Add(new WarningItem("gateway-inactive", "Gateway inactive; start it before tailing logs."));
                var inactive = statusCommand != null
                    ? new ActionResult(false, Output: statusCommand, Error: "Gateway inactive; cannot tail logs.", ExitCode: statusCommand.ExitCode)
                    : new ActionResult(false, Error: "Gateway inactive; cannot tail logs.");
                return BuildFinalResult(actionId, detection, steps, attempts, inactive, warnings, snapshotPath: null, selectedRuntime: null, suggestions, notes, context, statusCommand, candidates, outcome: "inactive", success: false);
            }

            var logsResult = await runner.RunAsync(actionId, correlationId, context, args, events, cancellationToken).ConfigureAwait(false);
            var logsSummary = logsResult.Output as OpenClawCommandSummary;
            CollectGuidance(logsSummary, suggestions, notes, warnings);
            if (!logsResult.Success && logsSummary?.TimedOut == true)
            {
                warnings.Add(new WarningItem("logs-timeout", "Log tail timed out; increase timeout to keep following logs."));
                var timeoutResult = new ActionResult(true, Output: logsSummary, ExitCode: logsResult.ExitCode);
                return BuildFinalResult(actionId, detection, steps, attempts, timeoutResult, warnings, snapshotPath: null, selectedRuntime: null, suggestions, notes, context, statusCommand, candidates, outcome: "logs");
            }

            return BuildFinalResult(actionId, detection, steps, attempts, logsResult, warnings, snapshotPath: null, selectedRuntime: null, suggestions, notes, context, statusCommand, candidates, outcome: logsResult.Success ? "logs" : "failed", success: logsResult.Success);
        }

        if (!detection.GatewayServiceExists || !detection.GatewayActive)
        {
            warnings.Add(new WarningItem("gateway-inactive", "Gateway is already stopped or missing."));
            var noOp = statusCommand != null
                ? new ActionResult(true, Output: statusCommand, ExitCode: statusCommand.ExitCode)
                : new ActionResult(true);
            return BuildFinalResult(actionId, detection, steps, attempts, noOp, warnings, snapshotPath: null, selectedRuntime: null, suggestions, notes, context, statusCommand, candidates, outcome: "stopped");
        }

        var stopResult = await runner.RunAsync(actionId, correlationId, context, args, events, cancellationToken).ConfigureAwait(false);
        if (!stopResult.Success)
        {
            warnings.Add(new WarningItem("gateway-stop-failed", $"Gateway stop failed (exit {stopResult.ExitCode})."));
        }
        return BuildFinalResult(actionId, detection, steps, attempts, stopResult, warnings, snapshotPath: null, selectedRuntime: null, suggestions, notes, context, statusCommand, candidates, outcome: stopResult.Success ? "stopped" : "failed", success: stopResult.Success);
    }

    private async Task<ActionResult> TryGatewayActionAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        string[] args,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        if (actionId == "gateway-logs")
        {
            var status = await GetGatewayStatusAsync(actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
            if (!status.GatewayActive)
            {
                return new ActionResult(false, Error: "Gateway is not healthy. Attempting repair before requesting logs.");
            }
        }

        return await runner.RunAsync(actionId, correlationId, context, args, events, cancellationToken).ConfigureAwait(false);
    }

    private static ActionResult BuildFinalResult(
        string actionId,
        OpenClawDetectionSummary detection,
        IReadOnlyList<GatewayRepairStep> steps,
        IReadOnlyList<GatewayRepairAttempt> attempts,
        ActionResult finalAction,
        IReadOnlyList<WarningItem> warnings,
        string? snapshotPath,
        string? selectedRuntime,
        IReadOnlyList<string> suggestions,
        IReadOnlyList<string> notes,
        ActionContext context,
        OpenClawCommandSummary? statusSummary,
        IReadOnlyList<OpenClawCandidate> candidates,
        string outcome = "fixed",
        bool success = true)
    {
        var inventoryWarnings = warnings
            .Select(warning => new OpenClawWarning(warning.Code, warning.Message, null))
            .ToList();
        var services = BuildServiceInfo(detection, statusSummary, notes);
        var gatewayInfo = BuildGatewayInfo(detection, statusSummary);
        var inventory = OpenClawLocator.BuildInventory(
            context,
            gatewayInfo,
            services,
            Array.Empty<OpenClawArtifactInfo>(),
            inventoryWarnings,
            detection.RuntimeVersion,
            detection.ConfigVersion,
            OpenClawLocator.Resolve(context),
            candidates);
        var artifacts = OpenClawLocator.ScanArtifacts(context, inventory);
        inventory = inventory with { Artifacts = artifacts };

        var summary = new GatewayRepairSummary(
            actionId,
            outcome,
            detection,
            steps,
            attempts,
            finalAction.Output as OpenClawCommandSummary,
            snapshotPath,
            selectedRuntime,
            suggestions,
            notes,
            inventory);

        return new ActionResult(success, Output: summary, Error: success ? null : finalAction.Error, ExitCode: finalAction.ExitCode, Warnings: warnings);
    }

    private async Task<(OpenClawDetectionSummary Detection, GatewayStatusResult Status, IReadOnlyList<OpenClawCandidate> Candidates)> DetectAsync(
        ActionContext context,
        Guid correlationId,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var candidates = candidateProvider(context);
        var selection = OpenClawLocator.Resolve(context);
        var configPath = ResolveConfigPath(context.OpenClawHome);
        var configVersion = ReadConfigVersion(configPath);
        var candidateSummaries = new List<OpenClawCandidateSummary>();
        string? runtimeVersion = null;
        string? configVersionHint = null;
        string? workingDir = selection?.WorkingDirectory;
        if (selection != null)
        {
            var signals = await GetVersionSignalsAsync(context, correlationId, events, cancellationToken).ConfigureAwait(false);
            runtimeVersion = signals.RuntimeVersion;
            configVersionHint = signals.ConfigVersionHint;
        }

        foreach (var candidate in candidates)
        {
            var candidateContext = ApplySelection(context, candidate.Command);
            var signals = await GetVersionSignalsAsync(candidateContext, correlationId, events, cancellationToken).ConfigureAwait(false);
            var version = signals.RuntimeVersion;
            candidateSummaries.Add(new OpenClawCandidateSummary(
                candidate.Source,
                candidate.Command.FileName,
                candidate.Command.WorkingDirectory,
                version));
        }

        var status = await GetGatewayStatusAsync("gateway-status", correlationId, context, events, cancellationToken).ConfigureAwait(false);
        if (status.Command is { } statusCommand)
        {
            var combined = statusCommand.StdOut.Concat(statusCommand.StdErr).ToArray();
            runtimeVersion ??= ExtractRuntimeVersion(combined);
            configVersionHint ??= ExtractConfigVersionHint(combined);
        }

        var cacheKey = BuildVersionCacheKey(selection?.FileName, workingDir, configPath);
        if (!string.IsNullOrWhiteSpace(cacheKey))
        {
            UpdateVersionCache(cacheKey, runtimeVersion, configVersion ?? configVersionHint);
            if (runtimeVersion == null || configVersion == null)
            {
                var cached = GetCachedVersion(cacheKey);
                runtimeVersion ??= cached.RuntimeVersion;
                configVersion ??= cached.ConfigVersion;
            }
        }

        var detection = new OpenClawDetectionSummary(
            selection?.FileName,
            workingDir,
            configPath,
            runtimeVersion,
            configVersion ?? configVersionHint,
            status.GatewayServiceExists,
            status.GatewayActive,
            candidateSummaries);
        return (detection, status, candidates);
    }

    private static void ReportDetection(
        string actionId,
        Guid correlationId,
        OpenClawDetectionSummary detection,
        IProgress<ActionEvent> events)
    {
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Detect", $"Executable: {detection.ExecutablePath ?? "(none)"}"));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Detect", $"Config: {detection.ConfigPath ?? "(missing)"}"));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Detect", $"Runtime version: {detection.RuntimeVersion ?? "(unknown)"}"));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Detect", $"Config version: {detection.ConfigVersion ?? "(unknown)"}"));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Detect", $"Gateway service exists: {detection.GatewayServiceExists}"));
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Detect", $"Gateway active: {detection.GatewayActive}"));
    }

    private async Task<(ActionContext? UpdatedContext, string? SelectedRuntime, GatewayRepairStep Step)> TrySelectCompatibleRuntimeAsync(
        string actionId,
        ActionContext context,
        Guid correlationId,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var candidates = candidateProvider(context);
        var configPath = ResolveConfigPath(context.OpenClawHome);
        var configVersion = ReadConfigVersion(configPath);
        if (!Version.TryParse(configVersion, out var desired))
        {
            return (null, null, new GatewayRepairStep("select-runtime", "skipped", "No config version to match.", null));
        }

        OpenClawCandidate? bestCandidate = null;
        Version? bestVersion = null;
        foreach (var candidate in candidates)
        {
            var candidateContext = ApplySelection(context, candidate.Command);
            var signals = await GetVersionSignalsAsync(candidateContext, correlationId, events, cancellationToken).ConfigureAwait(false);
            var versionText = signals.RuntimeVersion;
            if (!Version.TryParse(versionText, out var version))
            {
                continue;
            }

            if (version < desired)
            {
                continue;
            }

            if (bestVersion == null || version > bestVersion)
            {
                bestVersion = version;
                bestCandidate = candidate;
            }
        }

        if (bestCandidate == null)
        {
            return (null, null, new GatewayRepairStep("select-runtime", "failed", "No compatible runtime found.", null));
        }

        var updatedContext = ApplySelection(context, bestCandidate.Command);
        var detail = $"{bestCandidate.Source} ({bestCandidate.Command.FileName})";
        events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Runtime", $"Selected {detail}"));
        return (updatedContext, detail, new GatewayRepairStep("select-runtime", "success", detail, null));
    }

    private async Task<VersionSignals> GetVersionSignalsAsync(
        ActionContext context,
        Guid correlationId,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("openclaw-version", correlationId, context, new[] { "--version" }, events, cancellationToken).ConfigureAwait(false);
        var summary = result.Output as OpenClawCommandSummary;
        if (summary == null)
        {
            return new VersionSignals(null, null);
        }

        var combined = summary.StdOut.Concat(summary.StdErr).ToArray();
        return new VersionSignals(
            ExtractRuntimeVersion(combined),
            ExtractConfigVersionHint(combined));
    }

    private static string? ExtractRuntimeVersion(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var currentMatch = Regex.Match(line, @"current version is (\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
            if (currentMatch.Success)
            {
                return currentMatch.Groups[1].Value;
            }

            var match = Regex.Match(line, @"openclaw\s+v?(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static string? ExtractConfigVersionHint(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"written by a newer OpenClaw\s*\((\d+\.\d+\.\d+)\)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static string? ResolveConfigPath(string openClawHome)
    {
        var direct = Path.Combine(openClawHome, "openclaw.json");
        if (File.Exists(direct)) return direct;

        var nested = Path.Combine(openClawHome, "config", "openclaw.json");
        if (File.Exists(nested)) return nested;

        return File.Exists(direct) ? direct : null;
    }

    private static string? ReadConfigVersion(string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(configPath);
            using var doc = JsonDocument.Parse(stream);
            if (TryFindVersionProperty(doc.RootElement, out var version))
            {
                return version;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool TryFindVersionProperty(JsonElement element, out string? version)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("version")
                    || property.NameEquals("configVersion")
                    || property.NameEquals("schemaVersion")
                    || property.NameEquals("openclawVersion"))
                {
                    version = property.Value.GetString();
                    return !string.IsNullOrWhiteSpace(version);
                }
            }
        }

        version = null;
        return false;
    }

    private static ActionContext ApplySelection(ActionContext context, OpenClawCommand command)
    {
        if (command.BaseArgs.Length == 0)
        {
            return context with { OpenClawExecutable = command.FileName, OpenClawEntry = null };
        }

        var entry = command.BaseArgs.FirstOrDefault();
        return context with { OpenClawExecutable = null, OpenClawEntry = entry };
    }

    private static string BuildGatewayRepairSnapshotPath(string backupDirectory)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(backupDirectory, $"gateway-repair-{stamp}.claw");
    }

    private async Task<GatewayStatusResult> GetGatewayStatusAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status" }, events, cancellationToken).ConfigureAwait(false);
        var summary = result.Output as OpenClawCommandSummary;
        if (summary == null)
        {
            return new GatewayStatusResult(GatewayServiceExists: true, GatewayActive: false, Command: null);
        }

        var parsed = ParseGatewayStatus(summary);
        var active = parsed.RuntimeRunning && parsed.RpcOk;
        var serviceMissing = parsed.ServiceMissing;

        return new GatewayStatusResult(GatewayServiceExists: !serviceMissing, GatewayActive: active, Command: summary);
    }

    private static string? BuildVersionCacheKey(string? executable, string? workingDir, string? configPath)
    {
        if (!string.IsNullOrWhiteSpace(configPath)) return $"cfg::{configPath}";
        if (!string.IsNullOrWhiteSpace(executable)) return $"exe::{executable}::{workingDir}";
        return null;
    }

    private static void UpdateVersionCache(string key, string? runtimeVersion, string? configVersion)
    {
        if (string.IsNullOrWhiteSpace(runtimeVersion) && string.IsNullOrWhiteSpace(configVersion)) return;
        lock (VersionCacheLock)
        {
            if (VersionCache.TryGetValue(key, out var existing))
            {
                runtimeVersion ??= existing.RuntimeVersion;
                configVersion ??= existing.ConfigVersion;
            }
            VersionCache[key] = new VersionCacheEntry(runtimeVersion, configVersion);
        }
    }

    private static VersionCacheEntry GetCachedVersion(string key)
    {
        lock (VersionCacheLock)
        {
            return VersionCache.TryGetValue(key, out var entry)
                ? entry
                : new VersionCacheEntry(null, null);
        }
    }

    private async Task<GatewayStartupResult> TryGatewayStartupAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var port = ResolveGatewayPort();
        var launched = false;
        ActionResult? startResult = null;

        if (OperatingSystem.IsWindows() && IsDetachedStartAllowed())
        {
            launched = TryLaunchDetachedGateway(context, port);
            if (launched)
            {
                events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Gateway", $"Detached run launched on port {port}."));
            }
        }

        if (!launched)
        {
            startResult = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "start" }, events, cancellationToken).ConfigureAwait(false);
            if (!startResult.Success)
            {
                return new GatewayStartupResult(false, startResult, HealthCheckFailed: false, DetachedLaunch: false);
            }
        }

        if (!IsHealthCheckEnabled())
        {
            return new GatewayStartupResult(true, startResult, HealthCheckFailed: false, DetachedLaunch: launched);
        }

        var healthy = await WaitForGatewayReadyAsync(port, cancellationToken).ConfigureAwait(false);
        if (!healthy)
        {
            events.Report(new StatusChanged(actionId, correlationId, DateTimeOffset.UtcNow, "Gateway", "Health check failed after startup."));
        }

        if (launched)
        {
            return new GatewayStartupResult(true, startResult, HealthCheckFailed: !healthy, DetachedLaunch: true);
        }

        return new GatewayStartupResult(healthy, startResult, HealthCheckFailed: !healthy, DetachedLaunch: false);
    }

    private static int ResolveGatewayPort()
    {
        var raw = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_PORT");
        return int.TryParse(raw, out var port) && port > 0 ? port : 18789;
    }

    private static bool TryLaunchDetachedGateway(ActionContext context, int port)
    {
        return GatewayLaunchHelper.TryLaunchDetachedGateway(context, port);
    }

    private static async Task<bool> WaitForGatewayReadyAsync(int port, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ProbeGatewayAsync(port, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<bool> ProbeGatewayAsync(int port, CancellationToken cancellationToken)
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

    private static (int Scanned, int Removed) CleanupStaleGatewayLocks(string? openClawHome)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "openclaw");
        if (!Directory.Exists(tmp)) return (0, 0);

        var scanned = 0;
        var removed = 0;
        var targetConfig = string.IsNullOrWhiteSpace(openClawHome)
            ? string.Empty
            : Path.Combine(openClawHome, "openclaw.json").ToLowerInvariant();

        foreach (var file in Directory.EnumerateFiles(tmp, "gateway*.lock"))
        {
            scanned++;
            try
            {
                var raw = File.ReadAllText(file);
                var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var configPath = root.TryGetProperty("configPath", out var cfgProp) ? cfgProp.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrWhiteSpace(targetConfig) && !string.IsNullOrWhiteSpace(configPath)
                    && !string.Equals(configPath, targetConfig, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pid = root.TryGetProperty("pid", out var pidProp) ? pidProp.GetInt32() : -1;
                if (pid > 0 && IsProcessRunning(pid))
                {
                    continue;
                }

                File.Delete(file);
                removed++;
            }
            catch
            {
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
        }

        return (scanned, removed);
    }

    private static int KillStaleGatewayProcesses(int port, string? openClawHome)
    {
        var killed = 0;

        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), "openclaw");
            if (Directory.Exists(tmp))
            {
                foreach (var file in Directory.EnumerateFiles(tmp, "gateway*.lock"))
                {
                    try
                    {
                        var raw = File.ReadAllText(file);
                        using var doc = JsonDocument.Parse(raw);
                        var root = doc.RootElement;
                        var configPath = root.TryGetProperty("configPath", out var cfgProp) ? cfgProp.GetString() ?? string.Empty : string.Empty;
                        if (!string.IsNullOrWhiteSpace(openClawHome)
                            && !string.IsNullOrWhiteSpace(configPath)
                            && !configPath.Contains(openClawHome, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (root.TryGetProperty("pid", out var pidProp) && pidProp.TryGetInt32(out var pid))
                        {
                            if (TryKillProcess(pid))
                            {
                                killed++;
                            }
                        }
                    }
                    catch
                    {
                        // ignore lock parsing errors
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        if (OperatingSystem.IsWindows())
        {
            killed += KillProcessByPort(port);
        }

        return killed;
    }

    private static bool TryKillProcess(int pid)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int KillProcessByPort(int port)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p tcp",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                return 0;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1500);

            var killed = 0;
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!trimmed.Contains($":{port}", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5)
                {
                    continue;
                }
                if (int.TryParse(parts[^1], out var pid))
                {
                    if (TryKillProcess(pid))
                    {
                        killed++;
                    }
                }
            }

            return killed;
        }
        catch
        {
            return 0;
        }
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

    private static (bool Created, string? Token) EnsureGatewayToken(string openClawHome)
    {
        var configPath = ResolveConfigPath(openClawHome);
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return (false, null);
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
            if (node == null) return (false, null);

            var gateway = node["gateway"] as JsonObject ?? new JsonObject();
            node["gateway"] = gateway;

            var auth = gateway["auth"] as JsonObject ?? new JsonObject();
            gateway["auth"] = auth;

            var token = auth["token"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(token))
            {
                return (false, token);
            }

            var newToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            auth["token"] = newToken;
            File.WriteAllText(configPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (true, newToken);
        }
        catch
        {
            return (false, null);
        }
    }

    private static bool IsSnapshotSkipRequested()
    {
        var env = Environment.GetEnvironmentVariable("RECLAW_GATEWAY_REPAIR_SKIP_SNAPSHOT");
        return string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHealthCheckEnabled()
    {
        var env = Environment.GetEnvironmentVariable("RECLAW_GATEWAY_HEALTH_CHECK");
        if (string.IsNullOrWhiteSpace(env)) return true;
        return !string.Equals(env, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(env, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDetachedStartAllowed()
    {
        var env = Environment.GetEnvironmentVariable("RECLAW_GATEWAY_DETACHED_START");
        if (string.IsNullOrWhiteSpace(env)) return true;
        return !string.Equals(env, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(env, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStabilityCheckEnabled()
    {
        var env = Environment.GetEnvironmentVariable("RECLAW_GATEWAY_STABILITY_CHECK");
        if (string.IsNullOrWhiteSpace(env)) return true;
        return !string.Equals(env, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(env, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static GatewayRepairAttempt BuildAttempt(
        string stepId,
        string title,
        bool succeeded,
        bool mutatedState,
        string summary,
        string? commandLine)
    {
        return new GatewayRepairAttempt(stepId, title, succeeded, mutatedState, summary, commandLine);
    }

    private static IReadOnlyList<OpenClawServiceInfo> BuildServiceInfo(
        OpenClawDetectionSummary detection,
        OpenClawCommandSummary? statusSummary,
        IReadOnlyList<string> notes)
    {
        var platformKind = OperatingSystem.IsWindows() ? "schtasks"
            : OperatingSystem.IsMacOS() ? "launchd"
            : "systemd";
        var entrypoint = TryParseEntrypointMismatch(statusSummary);
        if (!entrypoint.IsMismatch)
        {
            var note = notes.FirstOrDefault(n => n.Contains("entrypoint mismatch", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(note))
            {
                entrypoint = (null, true);
            }
        }
        var isMismatched = entrypoint.IsMismatch;
        var service = new OpenClawServiceInfo(
            platformKind,
            "openclaw-gateway",
            entrypoint.Path,
            detection.GatewayServiceExists,
            detection.GatewayActive,
            IsLegacy: false,
            IsMismatched: isMismatched);
        return new[] { service };
    }

    private static OpenClawGatewayInfo BuildGatewayInfo(
        OpenClawDetectionSummary detection,
        OpenClawCommandSummary? statusSummary)
    {
        var parsed = ParseGatewayStatus(statusSummary);
        var summary = statusSummary != null
            ? statusSummary.StdOut.FirstOrDefault() ?? statusSummary.StdErr.FirstOrDefault()
            : null;
        return new OpenClawGatewayInfo(
            parsed.RuntimeRunning,
            parsed.RpcOk,
            parsed.RuntimeRunning && parsed.RpcOk,
            summary,
            parsed.RpcOk ? "RPC probe: ok" : "RPC probe: failed");
    }

    private static (string? Path, bool IsMismatch) TryParseEntrypointMismatch(OpenClawCommandSummary? summary)
    {
        if (summary == null) return (null, false);
        foreach (var line in summary.StdOut.Concat(summary.StdErr))
        {
            if (!line.Contains("entrypoint", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = Regex.Match(line, @"\((.+?)\s*->\s*(.+?)\)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var oldPath = match.Groups[1].Value.Trim();
                var newPath = match.Groups[2].Value.Trim();
                return (!string.IsNullOrWhiteSpace(newPath) ? newPath : oldPath, true);
            }
        }

        return (null, false);
    }

    private sealed record VersionSignals(string? RuntimeVersion, string? ConfigVersionHint);

    private sealed record GatewayStatusResult(bool GatewayServiceExists, bool GatewayActive, OpenClawCommandSummary? Command);

    private sealed record GatewayPollResult(bool Healthy, OpenClawCommandSummary? Summary, GatewayRepairStep Step, bool ServiceNotRunning, int Attempts);
    private sealed record GatewayStabilityResult(bool Healthy, OpenClawCommandSummary? Summary, GatewayRepairStep Step);

    private sealed record GatewayStatusSnapshot(bool RuntimeRunning, bool RpcOk, bool ServiceMissing);

    private sealed record GatewayStartupResult(bool Started, ActionResult? CommandResult, bool HealthCheckFailed, bool DetachedLaunch);

    private sealed record VersionCacheEntry(string? RuntimeVersion, string? ConfigVersion);

    private static void CollectGuidance(OpenClawCommandSummary? summary, List<string> suggestions, List<string> notes, List<WarningItem> warnings)
    {
        if (summary == null) return;
        var lines = summary.StdOut.Concat(summary.StdErr).ToArray();
        foreach (var line in lines)
        {
            if (IsInteractivePromptLine(line))
            {
                AddWarning(warnings, "interactive-prompt", InteractivePromptMessage);
                AddSuggestion(suggestions, "openclaw doctor --non-interactive --yes");
                AddSuggestion(suggestions, "Use Open Terminal for interactive prompts");
                AddNote(notes, InteractivePromptMessage);
            }
            if (IsGatewayModeUnsetLine(line))
            {
                AddWarning(warnings, "gateway-mode-unset", GatewayModeUnsetMessage);
                AddSuggestion(suggestions, "openclaw config set gateway.mode local");
                AddSuggestion(suggestions, "openclaw configure");
                AddNote(notes, GatewayModeUnsetMessage);
            }
            if (line.Contains("Gateway service entrypoint does not match", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(line, @"\((.+?)\s*->\s*(.+?)\)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var oldPath = match.Groups[1].Value.Trim();
                    var newPath = match.Groups[2].Value.Trim();
                    var mismatch = $"Gateway service entrypoint mismatch: {oldPath} -> {newPath}. Prefer runtime at {newPath} or reinstall the service.";
                    AddNote(notes, mismatch);
                    AddWarning(warnings, "gateway-entrypoint-mismatch", mismatch);
                    AddSuggestion(suggestions, "openclaw gateway install");
                }
                else
                {
                    AddNote(notes, "Gateway service entrypoint mismatch detected.");
                    AddWarning(warnings, "gateway-entrypoint-mismatch", "Gateway service entrypoint mismatch detected.");
                }
            }
            if (line.Contains("Service not installed. Run: openclaw gateway install", StringComparison.OrdinalIgnoreCase))
            {
                AddSuggestion(suggestions, "openclaw gateway install");
            }
            if (line.StartsWith("Start with:", StringComparison.OrdinalIgnoreCase))
            {
                var suggestion = line.Replace("Start with:", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(suggestion))
                {
                    AddSuggestion(suggestions, suggestion);
                }
            }
            if (line.Contains("Troubles: run openclaw status", StringComparison.OrdinalIgnoreCase))
            {
                AddSuggestion(suggestions, "openclaw status");
            }
            if (line.Contains("Run \"openclaw doctor --fix\"", StringComparison.OrdinalIgnoreCase))
            {
                AddSuggestion(suggestions, "openclaw doctor --fix");
            }
            if (line.Contains("Gateway auth is off", StringComparison.OrdinalIgnoreCase))
            {
                AddSuggestion(suggestions, "openclaw doctor --fix");
                AddNote(notes, "Gateway auth token missing; generate token before dashboard use.");
            }
            if (line.Contains("Troubleshooting:", StringComparison.OrdinalIgnoreCase))
            {
                AddNote(notes, line.Trim());
            }
        }
    }

    private async Task<ActionResult> RunGatewayAutoFixLadderAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        OpenClawDetectionSummary detection,
        GatewayStatusResult statusResult,
        List<GatewayRepairStep> steps,
        List<GatewayRepairAttempt> attempts,
        List<WarningItem> warnings,
        List<string> suggestions,
        List<string> notes,
        IReadOnlyList<OpenClawCandidate> candidates,
        string? snapshotPath,
        string? selectedRuntime,
        bool modeUnsetDetected)
    {
        var modeFixApplied = false;
        var modeUnsetSeen = modeUnsetDetected;
        var serviceNotRunning = IsServiceLoadedButNotRunning(statusResult.Command);
        if (serviceNotRunning)
        {
            var diagnostics = await CaptureServiceNotRunningDiagnosticsAsync(actionId, correlationId, context, events, cancellationToken, steps, attempts, suggestions, notes, warnings).ConfigureAwait(false);
            modeUnsetSeen = modeUnsetSeen
                || IsGatewayModeUnset(diagnostics.Doctor)
                || IsGatewayModeUnset(diagnostics.Deep)
                || IsGatewayModeUnset(diagnostics.Logs);

            var startupReason = InternalActionDispatcher.ExtractStartupReason(
                diagnostics.Logs,
                diagnostics.Doctor,
                diagnostics.Deep,
                statusResult.Command);
            if (!string.IsNullOrWhiteSpace(startupReason))
            {
                warnings.Add(new WarningItem("gateway-startup-reason", startupReason));
                AddNote(notes, $"Startup reason: {startupReason}");
            }
        }

        if (modeUnsetSeen)
        {
            warnings.Add(new WarningItem("gateway-mode-unset", GatewayModeUnsetMessage));

            var modeFix = await RunStepAsync("gateway-mode-local", new[] { "config", "set", "gateway.mode", "local" }, actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
            steps.Add(modeFix.Step);
            attempts.Add(BuildAttempt("gateway-mode-local", "Set gateway.mode", modeFix.Step.Status == "success", true, modeFix.Step.Detail ?? "Gateway mode set to local.", modeFix.Command?.Command));
            CollectGuidance(modeFix.Command, suggestions, notes, warnings);
            modeFixApplied = modeFix.Step.Status == "success";
            if (!modeFixApplied)
            {
                warnings.Add(new WarningItem("gateway-mode-set-failed", "Failed to set gateway.mode to local; repair cannot continue."));
                AddSuggestion(suggestions, "openclaw config set gateway.mode local");
                var blocked = new ActionResult(false, Output: modeFix.Command, Error: "Gateway mode is unset; config update failed.");
                return BuildFinalResult(actionId, detection, steps, attempts, blocked, warnings, snapshotPath, selectedRuntime, suggestions, notes, context, statusResult.Command, candidates, outcome: "failed", success: false);
            }
        }

        var doctor = await RunStepAsync("doctor", new[] { "doctor", "--non-interactive", "--yes" }, actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
        steps.Add(doctor.Step);
        attempts.Add(BuildAttempt("doctor", "Doctor", doctor.Step.Status == "success", true, doctor.Step.Detail ?? "Doctor executed.", doctor.Command?.Command));
        CollectGuidance(doctor.Command, suggestions, notes, warnings);
        if (IsInteractivePrompt(doctor.Command))
        {
            var blocked = new ActionResult(false, Output: doctor.Command, Error: InteractivePromptMessage);
            return BuildFinalResult(actionId, detection, steps, attempts, blocked, warnings, snapshotPath, selectedRuntime, suggestions, notes, context, statusResult.Command, candidates, outcome: "failed", success: false);
        }
        var doctorCommand = doctor.Command;

        if (!modeFixApplied && IsGatewayModeUnset(doctorCommand))
        {
            warnings.Add(new WarningItem("gateway-mode-unset", GatewayModeUnsetMessage));
            var modeFix = await RunStepAsync("gateway-mode-local", new[] { "config", "set", "gateway.mode", "local" }, actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
            steps.Add(modeFix.Step);
            attempts.Add(BuildAttempt("gateway-mode-local", "Set gateway.mode", modeFix.Step.Status == "success", true, modeFix.Step.Detail ?? "Gateway mode set to local.", modeFix.Command?.Command));
            CollectGuidance(modeFix.Command, suggestions, notes, warnings);
            modeFixApplied = modeFix.Step.Status == "success";
            if (!modeFixApplied)
            {
                warnings.Add(new WarningItem("gateway-mode-set-failed", "Failed to set gateway.mode to local; repair cannot continue."));
                AddSuggestion(suggestions, "openclaw config set gateway.mode local");
                var blocked = new ActionResult(false, Output: modeFix.Command, Error: "Gateway mode is unset; config update failed.");
                return BuildFinalResult(actionId, detection, steps, attempts, blocked, warnings, snapshotPath, selectedRuntime, suggestions, notes, context, statusResult.Command, candidates, outcome: "failed", success: false);
            }

            var doctorRecheck = await RunStepAsync("doctor-recheck", new[] { "doctor", "--non-interactive", "--yes" }, actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
            steps.Add(doctorRecheck.Step);
            attempts.Add(BuildAttempt("doctor-recheck", "Doctor recheck", doctorRecheck.Step.Status == "success", true, doctorRecheck.Step.Detail ?? "Doctor rechecked.", doctorRecheck.Command?.Command));
            CollectGuidance(doctorRecheck.Command, suggestions, notes, warnings);
            if (IsInteractivePrompt(doctorRecheck.Command))
            {
                var blocked = new ActionResult(false, Output: doctorRecheck.Command, Error: InteractivePromptMessage);
                return BuildFinalResult(actionId, detection, steps, attempts, blocked, warnings, snapshotPath, selectedRuntime, suggestions, notes, context, statusResult.Command, candidates, outcome: "failed", success: false);
            }
            doctorCommand = doctorRecheck.Command;
        }

        if (IsSessionStoreMissing(doctorCommand))
        {
            warnings.Add(new WarningItem("session-store-missing", "Session store dir missing; attempting to recreate."));
            var sessionFix = EnsureSessionStoreDir(context.OpenClawHome);
            steps.Add(new GatewayRepairStep("session-store", sessionFix.Success ? "success" : "failed", sessionFix.Success ? null : sessionFix.Error, null));
            attempts.Add(BuildAttempt("session-store", "Session store dir", sessionFix.Success, true, sessionFix.Success ? $"Recreated {sessionFix.Path}." : $"Failed to recreate {sessionFix.Path}.", null));
            if (sessionFix.Success)
            {
                AddNote(notes, $"Session store dir recreated: {sessionFix.Path}");
            }
            else
            {
                warnings.Add(new WarningItem("session-store-missing", $"Session store dir missing and could not be recreated: {sessionFix.Path}."));
                AddSuggestion(suggestions, $"Restore {sessionFix.Path} from backup or rerun rebuild with preserve options.");
                var blocked = new ActionResult(false, Output: doctorCommand, Error: "Session store directory missing; repair blocked.");
                return BuildFinalResult(actionId, detection, steps, attempts, blocked, warnings, snapshotPath, selectedRuntime, suggestions, notes, context, statusResult.Command, candidates, outcome: "failed", success: false);
            }

            var doctorRecheck = await RunStepAsync("doctor-recheck", new[] { "doctor", "--non-interactive", "--yes" }, actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
            steps.Add(doctorRecheck.Step);
            attempts.Add(BuildAttempt("doctor-recheck", "Doctor recheck", doctorRecheck.Step.Status == "success", true, doctorRecheck.Step.Detail ?? "Doctor rechecked.", doctorRecheck.Command?.Command));
            CollectGuidance(doctorRecheck.Command, suggestions, notes, warnings);
            if (IsInteractivePrompt(doctorRecheck.Command))
            {
                var blocked = new ActionResult(false, Output: doctorRecheck.Command, Error: InteractivePromptMessage);
                return BuildFinalResult(actionId, detection, steps, attempts, blocked, warnings, snapshotPath, selectedRuntime, suggestions, notes, context, statusResult.Command, candidates, outcome: "failed", success: false);
            }
            doctorCommand = doctorRecheck.Command;
        }

        var uninstall = await RunStepAsync("gateway-uninstall", new[] { "gateway", "uninstall" }, actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
        steps.Add(uninstall.Step);
        attempts.Add(BuildAttempt("gateway-uninstall", "Gateway uninstall", uninstall.Step.Status == "success", true, uninstall.Step.Detail ?? "Gateway uninstall executed.", uninstall.Command?.Command));
        CollectGuidance(uninstall.Command, suggestions, notes, warnings);

        var install = await RunStepAsync("gateway-install-force", new[] { "gateway", "install", "--force" }, actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
        steps.Add(install.Step);
        attempts.Add(BuildAttempt("gateway-install-force", "Gateway install (force)", install.Step.Status == "success", true, install.Step.Detail ?? "Gateway install (force) executed.", install.Command?.Command));
        CollectGuidance(install.Command, suggestions, notes, warnings);

        var start = await RunStepAsync("gateway-start", new[] { "gateway", "start" }, actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
        steps.Add(start.Step);
        attempts.Add(BuildAttempt("gateway-start", "Gateway start", start.Step.Status == "success", true, start.Step.Detail ?? "Gateway start executed.", start.Command?.Command));
        CollectGuidance(start.Command, suggestions, notes, warnings);

        var poll = await PollGatewayStatusAsync(actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
        steps.Add(poll.Step);
        attempts.Add(BuildAttempt("gateway-status-poll", "Gateway status poll", poll.Step.Status == "success", false, poll.Step.Detail ?? "Gateway status poll complete.", poll.Summary?.Command));

        var healthy = poll.Healthy;
        var statusSummary = poll.Summary ?? statusResult.Command;
        var unstableAfterStart = false;
        if (healthy && IsStabilityCheckEnabled())
        {
            var stability = await ConfirmGatewayStabilityAsync(actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
            steps.Add(stability.Step);
            attempts.Add(BuildAttempt("gateway-stability", "Gateway stability check", stability.Step.Status == "success", false, stability.Step.Detail ?? "Gateway stability check complete.", stability.Summary?.Command));
            if (!stability.Healthy)
            {
                unstableAfterStart = true;
                healthy = false;
                statusSummary = stability.Summary ?? statusSummary;
            }
        }

        if (!healthy)
        {
            var port = ResolveGatewayPort();

            if ((poll.ServiceNotRunning || unstableAfterStart) && OperatingSystem.IsWindows() && IsDetachedStartAllowed())
            {
                var cleaned = CleanupStaleGatewayLocks(context.OpenClawHome);
                steps.Add(new GatewayRepairStep("gateway-lock-cleanup", "success", $"Removed {cleaned.Removed} stale lock(s).", null));
                attempts.Add(BuildAttempt("gateway-lock-cleanup", "Cleanup stale locks", true, false, $"Removed {cleaned.Removed} stale lock(s).", null));

                var killed = KillStaleGatewayProcesses(port, context.OpenClawHome);
                if (killed > 0)
                {
                    steps.Add(new GatewayRepairStep("gateway-kill-stale", "success", $"Killed {killed} stale gateway process(es).", null));
                    attempts.Add(BuildAttempt("gateway-kill-stale", "Kill stale processes", true, false, $"Killed {killed} stale gateway process(es).", null));
                }

                var launched = TryLaunchDetachedGateway(context, port);
                var detail = launched ? $"Detached gateway launched on port {port}." : "Detached gateway launch failed.";
                steps.Add(new GatewayRepairStep("gateway-detached", launched ? "success" : "failed", launched ? null : "Detached gateway launch failed.", null));
                attempts.Add(BuildAttempt("gateway-detached", "Detached gateway run", launched, true, detail, null));

                var detachedHealthy = launched && (!IsHealthCheckEnabled() || await WaitForGatewayReadyAsync(port, cancellationToken).ConfigureAwait(false));
                if (detachedHealthy)
                {
                    warnings.Add(new WarningItem("gateway-detached-fallback", "Gateway running via detached fallback; service path unhealthy."));
                    var refresh = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status", "--require-rpc" }, events, cancellationToken).ConfigureAwait(false);
                    statusSummary = refresh.Output as OpenClawCommandSummary ?? statusSummary;
                    healthy = true;
                }
            }

            if (!healthy)
            {
                var foreground = await RunStepAsync("gateway-foreground", new[] { "gateway", "--port", port.ToString(), "--verbose" }, actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
                steps.Add(foreground.Step);
                attempts.Add(BuildAttempt("gateway-foreground", "Gateway foreground run", foreground.Step.Status == "success", true, foreground.Step.Detail ?? $"Gateway foreground run on port {port}.", foreground.Command?.Command));
                CollectGuidance(foreground.Command, suggestions, notes, warnings);
                AppendDiagnosticsNote(notes, $"openclaw gateway --port {port} --verbose", foreground.Command);

                if (OperatingSystem.IsWindows() && IsDetachedStartAllowed())
                {
                    var launched = TryLaunchDetachedGateway(context, port);
                    var detail = launched ? $"Detached gateway launched on port {port}." : "Detached gateway launch failed.";
                    steps.Add(new GatewayRepairStep("gateway-detached", launched ? "success" : "failed", launched ? null : "Detached gateway launch failed.", null));
                    attempts.Add(BuildAttempt("gateway-detached", "Detached gateway run", launched, true, detail, null));
                }

                var fallbackPoll = await PollGatewayStatusAsync(actionId, correlationId, context, events, cancellationToken).ConfigureAwait(false);
                steps.Add(fallbackPoll.Step);
                attempts.Add(BuildAttempt("gateway-status-poll-fallback", "Gateway status poll (fallback)", fallbackPoll.Step.Status == "success", false, fallbackPoll.Step.Detail ?? "Gateway status poll complete.", fallbackPoll.Summary?.Command));

                healthy = fallbackPoll.Healthy;
                statusSummary = fallbackPoll.Summary ?? statusSummary;
            }
        }

        if (!healthy)
        {
            warnings.Add(new WarningItem("gateway-unhealthy", "Gateway status did not become healthy after repair."));
            await AppendDiagnosticsAsync(actionId, correlationId, context, events, cancellationToken, notes).ConfigureAwait(false);
        }

        var parsedStatus = ParseGatewayStatus(statusSummary);
        var updatedDetection = detection with
        {
            GatewayActive = healthy,
            GatewayServiceExists = !parsedStatus.ServiceMissing
        };

        var outcome = healthy
            ? steps.Any(step => step.Status == "failed") ? "fixed-with-warnings" : "fixed"
            : start.Step.Status == "success"
                ? "partial"
                : "failed";

        var finalAction = statusSummary != null
            ? new ActionResult(healthy, Output: statusSummary, Error: healthy ? null : "Gateway not healthy.", ExitCode: statusSummary.ExitCode)
            : new ActionResult(healthy, Error: healthy ? null : "Gateway not healthy.");

        return BuildFinalResult(
            actionId,
            updatedDetection,
            steps,
            attempts,
            finalAction,
            warnings,
            snapshotPath,
            selectedRuntime,
            suggestions,
            notes,
            context,
            statusSummary,
            candidates,
            outcome: outcome,
            success: healthy);
    }

    private async Task AppendDiagnosticsAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        List<string> notes)
    {
        var status = await runner.RunAsync(actionId, correlationId, context, new[] { "status" }, events, cancellationToken).ConfigureAwait(false);
        AppendDiagnosticsNote(notes, "openclaw status", status.Output as OpenClawCommandSummary);

        var gatewayStatus = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status" }, events, cancellationToken).ConfigureAwait(false);
        AppendDiagnosticsNote(notes, "openclaw gateway status", gatewayStatus.Output as OpenClawCommandSummary);

        var gatewayStatusDeep = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status", "--deep", "--json" }, events, cancellationToken).ConfigureAwait(false);
        AppendDiagnosticsNote(notes, "openclaw gateway status --deep --json", gatewayStatusDeep.Output as OpenClawCommandSummary);

        var logs = await runner.RunAsync("gateway-logs", correlationId, context, new[] { "logs", "--follow" }, events, cancellationToken).ConfigureAwait(false);
        AppendDiagnosticsNote(notes, "openclaw logs --follow", logs.Output as OpenClawCommandSummary);

        var doctor = await runner.RunAsync(actionId, correlationId, context, new[] { "doctor", "--non-interactive", "--yes" }, events, cancellationToken).ConfigureAwait(false);
        AppendDiagnosticsNote(notes, "openclaw doctor", doctor.Output as OpenClawCommandSummary);
    }

    private static void AppendDiagnosticsNote(List<string> notes, string title, OpenClawCommandSummary? summary)
    {
        if (summary == null)
        {
            AddNote(notes, $"{title} (no output)");
            return;
        }

        var lines = summary.StdOut.Concat(summary.StdErr)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(8)
            .ToArray();
        var output = lines.Length == 0 ? "(no output)" : string.Join(Environment.NewLine, lines);
        var meta = summary.TimedOut ? "timed out" : $"exit {summary.ExitCode}";
        AddNote(notes, $"{title} ({meta}){Environment.NewLine}{output}");
    }

    private async Task<GatewayPollResult> PollGatewayStatusAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var (attempts, delay) = GetGatewayWaitPolicy();
        var serviceNotRunningCutoff = ReadIntEnv("RECLAW_GATEWAY_SERVICE_NOT_RUNNING_CUTOFF", 3);
        OpenClawCommandSummary? lastSummary = null;
        var serviceNotRunningCount = 0;
        for (var i = 0; i < attempts; i++)
        {
            var result = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status", "--require-rpc" }, events, cancellationToken).ConfigureAwait(false);
            lastSummary = result.Output as OpenClawCommandSummary;
            if (IsGatewayHealthy(lastSummary))
            {
                return new GatewayPollResult(true, lastSummary, new GatewayRepairStep("gateway-status-poll", "success", $"Gateway healthy after {i + 1} checks.", lastSummary), false, i + 1);
            }

            if (IsServiceLoadedButNotRunning(lastSummary))
            {
                serviceNotRunningCount++;
                if (serviceNotRunningCount >= serviceNotRunningCutoff)
                {
                    var pollDetail = $"Service loaded but not running after {serviceNotRunningCount} checks. Stopping status poll early.";
                    return new GatewayPollResult(false, lastSummary, new GatewayRepairStep("gateway-status-poll", "failed", pollDetail, lastSummary), true, i + 1);
                }
            }

            if (i + 1 < attempts)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        var detail = "Gateway status did not become healthy before timeout.";
        return new GatewayPollResult(false, lastSummary, new GatewayRepairStep("gateway-status-poll", "failed", detail, lastSummary), false, attempts);
    }

    private async Task<GatewayStabilityResult> ConfirmGatewayStabilityAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var (attempts, delay) = GetGatewayStabilityPolicy();
        OpenClawCommandSummary? lastSummary = null;
        for (var i = 0; i < attempts; i++)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            var result = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status", "--require-rpc" }, events, cancellationToken).ConfigureAwait(false);
            lastSummary = result.Output as OpenClawCommandSummary;
            if (!IsGatewayHealthy(lastSummary))
            {
                var detail = $"Gateway became unhealthy after {i + 1} stability check(s).";
                return new GatewayStabilityResult(false, lastSummary, new GatewayRepairStep("gateway-stability-check", "failed", detail, lastSummary));
            }
        }

        var okDetail = attempts == 1
            ? "Gateway remained healthy after stability check."
            : $"Gateway remained healthy for {attempts} stability checks.";
        return new GatewayStabilityResult(true, lastSummary, new GatewayRepairStep("gateway-stability-check", "success", okDetail, lastSummary));
    }

    private static (int Attempts, TimeSpan Delay) GetGatewayWaitPolicy()
    {
        var attempts = ReadIntEnv("RECLAW_GATEWAY_WAIT_ATTEMPTS", 10);
        var delayMs = ReadIntEnv("RECLAW_GATEWAY_WAIT_DELAY_MS", 1000);
        attempts = Math.Max(1, attempts);
        delayMs = Math.Max(1, delayMs);
        return (attempts, TimeSpan.FromMilliseconds(delayMs));
    }

    private static (int Attempts, TimeSpan Delay) GetGatewayStabilityPolicy()
    {
        var attempts = ReadIntEnv("RECLAW_GATEWAY_STABILITY_ATTEMPTS", 3);
        var delayMs = ReadIntEnv("RECLAW_GATEWAY_STABILITY_DELAY_MS", 2000);
        attempts = Math.Max(1, attempts);
        delayMs = Math.Max(1, delayMs);
        return (attempts, TimeSpan.FromMilliseconds(delayMs));
    }

    private static int ReadIntEnv(string key, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static bool IsGatewayModeUnset(OpenClawCommandSummary? summary)
    {
        if (summary == null) return false;
        return summary.StdOut.Concat(summary.StdErr).Any(IsGatewayModeUnsetLine);
    }

    private static bool IsInteractivePrompt(OpenClawCommandSummary? summary)
    {
        if (summary == null) return false;
        return summary.StdOut.Concat(summary.StdErr).Any(IsInteractivePromptLine);
    }

    private static bool IsServiceLoadedButNotRunning(OpenClawCommandSummary? summary)
    {
        if (summary == null)
        {
            return false;
        }

        var lines = summary.StdOut.Concat(summary.StdErr)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Any(line => line.Contains("Service is loaded but not running", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var parsed = ParseGatewayStatus(summary);
        var rpcFailed = lines.Any(line => line.Contains("RPC probe", StringComparison.OrdinalIgnoreCase)
            && line.Contains("failed", StringComparison.OrdinalIgnoreCase));
        return !parsed.ServiceMissing && !parsed.RuntimeRunning && rpcFailed;
    }

    private async Task<(OpenClawCommandSummary? Deep, OpenClawCommandSummary? Logs, OpenClawCommandSummary? Doctor)> CaptureServiceNotRunningDiagnosticsAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken,
        List<GatewayRepairStep> steps,
        List<GatewayRepairAttempt> attempts,
        List<string> suggestions,
        List<string> notes,
        List<WarningItem> warnings)
    {
        var deepResult = await runner.RunAsync(actionId, correlationId, context, new[] { "gateway", "status", "--deep" }, events, cancellationToken).ConfigureAwait(false);
        var deepSummary = deepResult.Output as OpenClawCommandSummary;
        steps.Add(new GatewayRepairStep("gateway-status-deep", deepResult.Success ? "success" : "failed", deepResult.Error, deepSummary));
        attempts.Add(BuildAttempt("gateway-status-deep", "Gateway status --deep", deepResult.Success, false, deepResult.Success ? "Gateway status --deep executed." : deepResult.Error ?? "Gateway status --deep failed.", deepSummary?.Command));
        CollectGuidance(deepSummary, suggestions, notes, warnings);

        var logsResult = await runner.RunAsync("gateway-logs", correlationId, context, new[] { "logs", "--follow" }, events, cancellationToken).ConfigureAwait(false);
        var logsSummary = logsResult.Output as OpenClawCommandSummary;
        steps.Add(new GatewayRepairStep("gateway-logs", logsResult.Success ? "success" : "failed", logsResult.Error, logsSummary));
        attempts.Add(BuildAttempt("gateway-logs", "Gateway logs --follow", logsResult.Success, false, logsResult.Success ? "Gateway logs sample captured." : logsResult.Error ?? "Gateway logs failed.", logsSummary?.Command));
        CollectGuidance(logsSummary, suggestions, notes, warnings);

        var doctorResult = await runner.RunAsync(actionId, correlationId, context, new[] { "doctor", "--non-interactive", "--yes" }, events, cancellationToken).ConfigureAwait(false);
        var doctorSummary = doctorResult.Output as OpenClawCommandSummary;
        steps.Add(new GatewayRepairStep("gateway-doctor", doctorResult.Success ? "success" : "failed", doctorResult.Error, doctorSummary));
        attempts.Add(BuildAttempt("gateway-doctor", "Gateway doctor", doctorResult.Success, false, doctorResult.Success ? "Doctor executed." : doctorResult.Error ?? "Doctor failed.", doctorSummary?.Command));
        CollectGuidance(doctorSummary, suggestions, notes, warnings);

        return (deepSummary, logsSummary, doctorSummary);
    }

    private static bool IsSessionStoreMissing(OpenClawCommandSummary? summary)
    {
        if (summary == null) return false;
        return summary.StdOut.Concat(summary.StdErr)
            .Any(line => line.Contains("Session store dir missing", StringComparison.OrdinalIgnoreCase));
    }

    private static (bool Success, string Path, string? Error) EnsureSessionStoreDir(string openClawHome)
    {
        if (string.IsNullOrWhiteSpace(openClawHome))
        {
            return (false, "~\\.openclaw\\agents\\main\\sessions", "OpenClaw home is not set.");
        }

        var sessionsPath = Path.Combine(openClawHome, "agents", "main", "sessions");
        try
        {
            Directory.CreateDirectory(sessionsPath);
            return (true, sessionsPath, null);
        }
        catch (Exception ex)
        {
            return (false, sessionsPath, ex.Message);
        }
    }

    private static bool IsGatewayHealthy(OpenClawCommandSummary? summary)
    {
        if (summary == null) return false;
        var parsed = ParseGatewayStatus(summary);
        return parsed.RuntimeRunning && parsed.RpcOk;
    }

    private static GatewayStatusSnapshot ParseGatewayStatus(OpenClawCommandSummary? summary)
    {
        if (summary == null)
        {
            return new GatewayStatusSnapshot(false, false, false);
        }

        var lines = summary.StdOut.Concat(summary.StdErr)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        var serviceMissing = lines.Any(line =>
            line.Contains("service missing", StringComparison.OrdinalIgnoreCase)
            || line.Contains("gateway service missing", StringComparison.OrdinalIgnoreCase)
            || line.Contains("service not installed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("service unit not found", StringComparison.OrdinalIgnoreCase)
            || line.Contains("scheduled task (missing)", StringComparison.OrdinalIgnoreCase));

        var runtimeRunning = lines.Any(line =>
            line.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
            && line.Contains("running", StringComparison.OrdinalIgnoreCase));

        var rpcOk = lines.Any(line =>
            line.Contains("RPC probe", StringComparison.OrdinalIgnoreCase)
            && line.Contains("ok", StringComparison.OrdinalIgnoreCase));

        return new GatewayStatusSnapshot(runtimeRunning, rpcOk, serviceMissing);
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
        var mentionsBlocked = lower.Contains("blocked") || lower.Contains("will be blocked") || lower.Contains("start blocked");
        return mentionsMode && mentionsUnset && mentionsBlocked;
    }

    private static bool IsInteractivePromptLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (line.Contains("Start gateway service now", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (line.Contains("Yes / No", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void AddSuggestion(List<string> suggestions, string value)
    {
        if (suggestions.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        suggestions.Add(value);
    }

    private static void AddNote(List<string> notes, string value)
    {
        if (notes.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        notes.Add(value);
    }

    private static void AddWarning(List<WarningItem> warnings, string code, string message)
    {
        if (warnings.Any(warning => string.Equals(warning.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        warnings.Add(new WarningItem(code, message));
    }
}
