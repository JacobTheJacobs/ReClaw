using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReClaw.App.Actions;
using ReClaw.App.Execution;
using ReClaw.Core;

namespace ReClaw.Cli;

internal sealed record CliResultEnvelope(
    bool Success,
    string Action,
    string Summary,
    object? Details,
    IReadOnlyList<WarningItem> Warnings,
    IReadOnlyList<string> Artifacts,
    IReadOnlyList<string> Changes,
    string? RollbackPoint,
    int ExitCode,
    OpenClawInventory? Inventory = null,
    IReadOnlyList<GatewayRepairAttempt>? RepairAttempts = null,
    IReadOnlyList<OpenClawServiceInfo>? Services = null,
    IReadOnlyList<OpenClawArtifactInfo>? ArtifactDetails = null,
    IReadOnlyList<OpenClawWarning>? InventoryWarnings = null,
    object? BackupSummary = null,
    BackupDiffSummary? DiffSummary = null,
    BackupScheduleSummary? ScheduleInfo = null,
    GatewayTokenSummary? TokenSummary = null,
    BrowserDiagnosticsSummary? BrowserDiagnostics = null);

internal static class CliResultFormatter
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static int Render(string actionId, ActionResult result, bool json)
    {
        var envelope = Build(actionId, result);
        if (json)
        {
            var payload = JsonSerializer.Serialize(envelope, JsonOptions);
            Console.WriteLine(payload);
        }
        else
        {
            RenderHuman(envelope);
        }

        return envelope.ExitCode;
    }

    internal static CliResultEnvelope Build(string actionId, ActionResult result)
    {
        var summary = BuildSummary(result);
        var details = BuildDetails(result);
        var exitCode = MapExitCode(result);
        var warnings = result.Warnings ?? Array.Empty<WarningItem>();
        var artifacts = Array.Empty<string>();
        var changes = Array.Empty<string>();
        string? rollbackPoint = null;
        OpenClawInventory? inventory = null;
        IReadOnlyList<GatewayRepairAttempt>? attempts = null;
        IReadOnlyList<OpenClawServiceInfo>? services = null;
        IReadOnlyList<OpenClawArtifactInfo>? artifactDetails = null;
        IReadOnlyList<OpenClawWarning>? inventoryWarnings = null;
        object? backupSummary = null;
        BackupDiffSummary? diffSummary = null;
        BackupScheduleSummary? scheduleInfo = null;
        GatewayTokenSummary? tokenSummary = null;
        BrowserDiagnosticsSummary? browserDiagnostics = null;

        if (result.Output is RestoreSummary restore)
        {
            if (!string.IsNullOrWhiteSpace(restore.SnapshotPath))
            {
                artifacts = new[] { restore.SnapshotPath };
                rollbackPoint = restore.SnapshotPath;
            }

            changes = restore.Preview.Assets
                .Where(asset => asset.Exists)
                .Select(asset => asset.ArchivePath)
                .ToArray();
        }
        else if (result.Output is FixSummary fix)
        {
            if (!string.IsNullOrWhiteSpace(fix.SnapshotPath))
            {
                artifacts = new[] { fix.SnapshotPath };
                rollbackPoint = fix.SnapshotPath;
            }
        }
        else if (result.Output is DiagnosticsBundleSummary bundle)
        {
            artifacts = new[] { bundle.BundlePath };
            rollbackPoint = bundle.BundlePath;
        }
        else if (result.Output is RecoverSummary recover && recover.Restore is { } recoverRestore)
        {
            if (!string.IsNullOrWhiteSpace(recoverRestore.SnapshotPath))
            {
                artifacts = new[] { recoverRestore.SnapshotPath };
                rollbackPoint = recoverRestore.SnapshotPath;
            }

            changes = recoverRestore.Preview.Assets
                .Where(asset => asset.Exists)
                .Select(asset => asset.ArchivePath)
                .ToArray();
        }
        else if (result.Output is RollbackSummary rollback)
        {
            changes = rollback.Preview.Assets
                .Where(asset => asset.Exists)
                .Select(asset => asset.ArchivePath)
                .ToArray();
        }
        else if (result.Output is ResetSummary reset)
        {
            changes = reset.Plan.DeletePaths.ToArray();
        }
        else if (result.Output is GatewayRepairSummary gateway && !string.IsNullOrWhiteSpace(gateway.SnapshotPath))
        {
            artifacts = new[] { gateway.SnapshotPath };
            rollbackPoint = gateway.SnapshotPath;
        }
        else if (result.Output is OpenClawRebuildSummary rebuild && !string.IsNullOrWhiteSpace(rebuild.BackupPath))
        {
            artifacts = new[] { rebuild.BackupPath };
            rollbackPoint = rebuild.BackupPath;
        }
        if (result.Output is GatewayRepairSummary repair)
        {
            inventory = repair.Inventory;
            attempts = repair.Attempts;
            services = repair.Inventory?.Services;
            artifactDetails = repair.Inventory?.Artifacts;
            inventoryWarnings = repair.Inventory?.Warnings;
        }
        else if (result.Output is OpenClawRebuildSummary rebuildSummary)
        {
            inventory = rebuildSummary.Inventory;
            services = rebuildSummary.Inventory?.Services;
            artifactDetails = rebuildSummary.Inventory?.Artifacts;
            inventoryWarnings = rebuildSummary.Inventory?.Warnings;
        }
        else if (result.Output is OpenClawBackupCreateSummary createSummary)
        {
            backupSummary = createSummary;
        }
        else if (result.Output is OpenClawBackupVerifySummary verifySummary)
        {
            backupSummary = verifySummary;
        }
        else if (result.Output is BackupExportSummary exportSummary)
        {
            backupSummary = exportSummary;
        }
        else if (result.Output is BackupDiffSummary diff)
        {
            diffSummary = diff;
        }
        else if (result.Output is BackupScheduleSummary schedule)
        {
            scheduleInfo = schedule;
        }
        else if (result.Output is GatewayTokenSummary token)
        {
            tokenSummary = token;
        }
        else if (result.Output is BrowserDiagnosticsSummary diagnostics)
        {
            browserDiagnostics = diagnostics;
        }

        if (result.Output is IDiagnosticsBundleCarrier carrier && !string.IsNullOrWhiteSpace(carrier.DiagnosticsBundlePath))
        {
            artifacts = artifacts.Length == 0
                ? new[] { carrier.DiagnosticsBundlePath! }
                : artifacts.Concat(new[] { carrier.DiagnosticsBundlePath! }).Distinct().ToArray();
        }

        return new CliResultEnvelope(
            result.Success,
            actionId,
            summary,
            details,
            warnings,
            artifacts,
            changes,
            rollbackPoint,
            exitCode,
            inventory,
            attempts,
            services,
            artifactDetails,
            inventoryWarnings,
            backupSummary,
            diffSummary,
            scheduleInfo,
            tokenSummary,
            browserDiagnostics);
    }

    private static void RenderHuman(CliResultEnvelope envelope)
    {
        if (!envelope.Success)
        {
            Console.Error.WriteLine(envelope.Summary);
            RenderFailureDetails(envelope);
            RenderWarnings(envelope);
            return;
        }

        switch (envelope.Details)
        {
            case RestoreSummary restore:
                RenderRestoreSummary(restore);
                break;
            case FixSummary fix:
                RenderFixSummary(fix);
                break;
            case DoctorSummary doctor:
                RenderDoctorSummary(doctor);
                break;
            case RecoverSummary recover:
                RenderRecoverSummary(recover);
                break;
            case RollbackSummary rollback:
                RenderRollbackSummary(rollback);
                break;
            case ResetSummary reset:
                RenderResetSummary(reset);
                break;
            case StatusSummary status:
                RenderStatusSummary(status);
                break;
            case DiagnosticsBundleSummary bundle:
                Console.WriteLine($"Diagnostics bundle: {bundle.BundlePath}");
                RenderJournalFooter(bundle);
                break;
            case GatewayRepairSummary repair:
                RenderGatewayRepairSummary(repair);
                break;
            case OpenClawCleanupSummary cleanup:
                Console.WriteLine(cleanup.Applied ? "Cleanup applied." : "Cleanup preview.");
                foreach (var artifact in cleanup.Candidates)
                {
                    Console.WriteLine($"{artifact.Kind}: {artifact.Path} {(artifact.IsSafeToClean ? "(safe)" : "(review)") }");
                }
                RenderJournalFooter(cleanup);
                break;
            case OpenClawRebuildSummary rebuild:
                RenderOpenClawRebuildSummary(rebuild);
                break;
            case OpenClawBackupCreateSummary created:
                Console.WriteLine($"Archive: {created.ArchivePath}");
                Console.WriteLine($"Assets: {created.Assets.Count} (verified: {created.Verified})");
                break;
            case OpenClawBackupVerifySummary verified:
                Console.WriteLine($"Archive: {verified.ArchivePath}");
                Console.WriteLine($"Assets: {verified.AssetCount} Entries: {verified.EntryCount}");
                break;
            case BackupExportSummary export:
                Console.WriteLine($"Exported: {export.ArchivePath}");
                if (export.Verified) Console.WriteLine("Verified: yes");
                if (!string.IsNullOrWhiteSpace(export.EncryptedArchivePath))
                {
                    Console.WriteLine($"Encrypted archive: {export.EncryptedArchivePath}");
                }
                break;
            case BackupDiffSummary diff:
                Console.WriteLine($"Added: {diff.AddedAssets.Count}, Removed: {diff.RemovedAssets.Count}, Changed: {diff.ChangedAssets.Count}");
                if (diff.RedactedNote is { Length: > 0 })
                {
                    Console.WriteLine(diff.RedactedNote);
                }
                break;
            case BackupScheduleSummary schedule:
                Console.WriteLine(schedule.Applied ? "Schedule updated." : "Schedules:");
                foreach (var entry in schedule.Schedules)
                {
                    Console.WriteLine($"{entry.Id}: {entry.Mode} {entry.Kind} {entry.Expression}");
                }
                break;
            case GatewayTokenSummary token:
                Console.WriteLine(token.Revealed ? "Gateway token (revealed):" : "Gateway token (masked):");
                Console.WriteLine(token.TokenMasked ?? "(missing)");
                if (!string.IsNullOrWhiteSpace(token.SourcePath))
                {
                    Console.WriteLine($"Config: {token.SourcePath}");
                }
                break;
            case BrowserDiagnosticsSummary diagnostics:
                Console.WriteLine($"Local URL: {diagnostics.LocalUrl ?? "(unknown)"}");
                Console.WriteLine($"Dashboard URL: {diagnostics.DashboardUrl ?? "(unknown)"}");
                Console.WriteLine($"Auth required: {diagnostics.AuthRequired}");
                Console.WriteLine($"Token present: {diagnostics.TokenPresent}");
                Console.WriteLine($"Allowed origins valid: {diagnostics.AllowedOriginsValid}");
                if (diagnostics.SecureContextWarning)
                {
                    Console.WriteLine("Warning: insecure browser context detected for remote mode.");
                }
                if (diagnostics.RemoteUnsafe)
                {
                    Console.WriteLine("Remote access appears unsafe. Prefer HTTPS or a secure tunnel.");
                }
                break;
            case string[] values:
                foreach (var entry in values)
                {
                    Console.WriteLine(entry);
                }
                break;
            case string value:
                Console.WriteLine(value);
                break;
            default:
                Console.WriteLine(envelope.Summary);
                break;
        }

        RenderWarnings(envelope);
    }

    private static string BuildSummary(ActionResult result)
    {
        if (!result.Success)
        {
            return result.Error ?? "Action failed.";
        }

        return result.Output switch
        {
            BackupVerificationSummary summary =>
                $"Backup OK: {summary.ArchivePath} (entries: {summary.EntryCount}, assets: {summary.AssetCount}, payload: {summary.PayloadEntryCount})",
            OpenClawBackupCreateSummary summary =>
                summary.Verified
                    ? $"OpenClaw backup created + verified: {summary.ArchivePath}"
                    : $"OpenClaw backup created: {summary.ArchivePath}",
            OpenClawBackupVerifySummary summary =>
                $"OpenClaw backup verified: {summary.ArchivePath} (entries: {summary.EntryCount}, assets: {summary.AssetCount})",
            BackupExportSummary export =>
                export.Verified
                    ? $"Backup exported and verified: {export.ArchivePath}"
                    : $"Backup exported: {export.ArchivePath}",
            BackupDiffSummary diff =>
                $"Backup diff: {diff.AddedAssets.Count} added, {diff.RemovedAssets.Count} removed, {diff.ChangedAssets.Count} changed",
            BackupScheduleSummary schedule =>
                schedule.Applied ? "Backup schedule updated." : $"Backup schedules: {schedule.Schedules.Count}",
            GatewayTokenSummary token =>
                token.Revealed ? "Gateway token revealed." : "Gateway token masked.",
            BrowserDiagnosticsSummary =>
                "Browser diagnostics completed.",
            RestoreSummary restore =>
                BuildRestoreSummary(restore),
            DoctorSummary =>
                "Doctor completed.",
            FixSummary =>
                "Fix completed.",
            RecoverSummary recover =>
                recover.Applied ? "Recovery completed." : "Recovery preview.",
            RollbackSummary rollback =>
                rollback.Applied ? "Rollback completed." : "Rollback preview.",
            ResetSummary reset =>
                reset.Applied ? "Reset completed." : "Reset preview.",
            StatusSummary status =>
                $"Status: backups={status.BackupCount}",
            DiagnosticsBundleSummary bundle =>
                $"Diagnostics bundle: {bundle.BundlePath}",
            OpenClawCommandSummary command =>
                command.ExitCode == 0
                    ? "OpenClaw command completed."
                    : $"OpenClaw command failed (exit {command.ExitCode}).",
            GatewayRepairSummary repair =>
                repair.Outcome switch
                {
                    "fixed" => "Gateway repaired.",
                    "fixed-with-warnings" => "Gateway repaired with warnings.",
                    "started-with-warnings" => "Gateway started with warnings.",
                    "confirmation-needed" => "Gateway repair needs confirmation.",
                    _ => "Gateway repair finished."
                },
            OpenClawCleanupSummary cleanup =>
                cleanup.Applied ? "Cleanup applied." : "Cleanup preview.",
            OpenClawRebuildSummary =>
                "OpenClaw rebuild completed.",
            string value => value,
            _ => "Completed successfully."
        };
    }

    private static object? BuildDetails(ActionResult result)
    {
        if (result.Success)
        {
            return result.Output;
        }

        if (result.Output != null)
        {
            return new FailureDetails(result.Error ?? "Action failed.", result.Output);
        }

        return new FailureDetails(result.Error ?? "Action failed.", null);
    }

    private static int MapExitCode(ActionResult result)
    {
        if (result.ExitCode is { } explicitCode && explicitCode != 0)
        {
            return explicitCode;
        }

        if (result.Success)
        {
            return CliExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(result.Error) && IsValidationError(result.Error))
        {
            return CliExitCodes.InvalidUsage;
        }

        return CliExitCodes.ActionFailed;
    }

    private static bool IsValidationError(string error)
    {
        if (error.StartsWith("Missing input", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (error.StartsWith("Invalid input", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return error.Contains("required", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRestoreSummary(RestoreSummary restore)
    {
        var preview = restore.Preview;
        var verb = restore.Applied ? "Restore completed" : "Restore preview";
        var summary = $"{verb}: {preview.RestorePayloadEntries} entries ({preview.OverwritePayloadEntries} overwrite) across {preview.Assets.Count} targets (scope: {preview.Scope})";
        if (!string.IsNullOrWhiteSpace(restore.SnapshotPath))
        {
            summary += $" | snapshot: {restore.SnapshotPath}";
        }
        return summary;
    }

    private static void RenderRestoreSummary(RestoreSummary restore)
    {
        var preview = restore.Preview;
        Console.WriteLine(BuildRestoreSummary(restore));

        var overwriteTargets = preview.Assets
            .Where(asset => asset.Exists)
            .Select(asset => asset.ArchivePath)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (overwriteTargets.Count > 0)
        {
            var display = overwriteTargets.Count > 10
                ? string.Join(", ", overwriteTargets.Take(10)) + $" (+{overwriteTargets.Count - 10} more)"
                : string.Join(", ", overwriteTargets);
            Console.WriteLine($"Will overwrite: {display}");
        }
        else
        {
            Console.WriteLine("No existing entries will be overwritten.");
        }

        if (restore.ResetMode != null)
        {
            Console.WriteLine($"Safe reset mode: {restore.ResetMode}");
        }

        if (restore.Applied && !string.IsNullOrWhiteSpace(restore.SnapshotPath))
        {
            Console.WriteLine($"Rollback snapshot: {restore.SnapshotPath}");
        }

        RenderJournalFooter(restore);
    }

    private static void RenderDoctorSummary(DoctorSummary doctor)
    {
        Console.WriteLine("Doctor completed.");
        RenderCommandFooter(doctor.Command);
        RenderDiagnosticsFooter(doctor.DiagnosticsBundlePath);
        RenderJournalFooter(doctor);
    }

    private static void RenderFixSummary(FixSummary fix)
    {
        Console.WriteLine("Fix completed.");
        if (!string.IsNullOrWhiteSpace(fix.SnapshotPath))
        {
            Console.WriteLine($"Snapshot: {fix.SnapshotPath}");
        }
        RenderCommandFooter(fix.Command);
        RenderDiagnosticsFooter(fix.DiagnosticsBundlePath);
        RenderJournalFooter(fix);
    }

    private static void RenderRecoverSummary(RecoverSummary recover)
    {
        if (recover.Restore != null)
        {
            RenderRestoreSummary(recover.Restore);
        }
        else
        {
            Console.WriteLine(recover.Applied ? "Recovery completed." : "Recovery preview.");
        }

        if (recover.Doctor != null)
        {
            Console.WriteLine("Doctor step completed.");
        }

        if (recover.Fix != null)
        {
            Console.WriteLine("Fix step completed.");
        }

        if (recover.Steps.Count > 0)
        {
            foreach (var step in recover.Steps)
            {
                var detail = string.IsNullOrWhiteSpace(step.Error) ? string.Empty : $" ({step.Error})";
                Console.WriteLine($"Step {step.Step}: {step.Status}{detail}");
            }
        }

        if (!string.IsNullOrWhiteSpace(recover.NextEscalation))
        {
            Console.WriteLine($"Next escalation: {recover.NextEscalation}");
        }

        RenderDiagnosticsFooter(recover.DiagnosticsBundlePath);
        RenderJournalFooter(recover);
    }

    private static void RenderRollbackSummary(RollbackSummary rollback)
    {
        var preview = rollback.Preview;
        var verb = rollback.Applied ? "Rollback completed" : "Rollback preview";
        Console.WriteLine($"{verb}: {preview.RestorePayloadEntries} entries ({preview.OverwritePayloadEntries} overwrite) across {preview.Assets.Count} targets (scope: {preview.Scope})");
        Console.WriteLine($"Snapshot: {rollback.SnapshotPath}");
        RenderJournalFooter(rollback);
    }

    private static void RenderResetSummary(ResetSummary reset)
    {
        var verb = reset.Applied ? "Reset completed" : "Reset preview";
        Console.WriteLine($"{verb}: {reset.Plan.DeletePaths.Count} paths queued for deletion (mode: {reset.Mode}).");
        if (reset.Plan.PreservePaths.Count > 0)
        {
            var display = reset.Plan.PreservePaths.Count > 6
                ? string.Join(", ", reset.Plan.PreservePaths.Take(6)) + $" (+{reset.Plan.PreservePaths.Count - 6} more)"
                : string.Join(", ", reset.Plan.PreservePaths);
            Console.WriteLine($"Preserve: {display}");
        }
        RenderJournalFooter(reset);
    }

    private static void RenderStatusSummary(StatusSummary status)
    {
        Console.WriteLine($"OpenClaw home: {status.OpenClawHome} (exists: {status.OpenClawHomeExists})");
        Console.WriteLine($"Config: {status.ConfigDirectory} (exists: {status.ConfigDirectoryExists})");
        Console.WriteLine($"Data: {status.DataDirectory} (exists: {status.DataDirectoryExists})");
        Console.WriteLine($"Backups: {status.BackupDirectory} (exists: {status.BackupDirectoryExists}, count: {status.BackupCount})");
        Console.WriteLine($"Logs: {status.LogsDirectory}");
        Console.WriteLine($"Temp: {status.TempDirectory}");
        if (!string.IsNullOrWhiteSpace(status.OpenClawExecutable))
        {
            Console.WriteLine($"OpenClaw exe: {status.OpenClawExecutable}");
        }
        RenderJournalFooter(status);
    }

    private static void RenderGatewayRepairSummary(GatewayRepairSummary repair)
    {
        Console.WriteLine($"Gateway action: {repair.Action}");
        Console.WriteLine($"Outcome: {repair.Outcome}");
        var detection = repair.Detection;
        Console.WriteLine($"Runtime: {detection.RuntimeVersion ?? "(unknown)"}");
        Console.WriteLine($"Config: {detection.ConfigPath ?? "(missing)"} (version: {detection.ConfigVersion ?? "(unknown)"})");
        Console.WriteLine($"Gateway service exists: {detection.GatewayServiceExists}");
        Console.WriteLine($"Gateway active: {detection.GatewayActive}");
        if (repair.Inventory?.ActiveRuntime is { } runtime)
        {
            Console.WriteLine($"Active runtime: {runtime.ExecutablePath} ({runtime.Kind}, {runtime.Version ?? "unknown"})");
        }
        if (repair.Inventory?.Config is { } config)
        {
            Console.WriteLine($"Config path: {config.ConfigPath} (exists: {config.Exists})");
            if (!string.IsNullOrWhiteSpace(config.LogPath))
            {
                Console.WriteLine($"Log path: {config.LogPath}");
            }
        }
        if (repair.Inventory?.Services is { Count: > 0 } services)
        {
            foreach (var service in services)
            {
                var mismatch = service.IsMismatched ? " (mismatched)" : string.Empty;
                Console.WriteLine($"Service: {service.Name} [{service.PlatformKind}] active={service.IsActive} exists={service.Exists}{mismatch}");
            }
        }
        if (!string.IsNullOrWhiteSpace(repair.SelectedRuntime))
        {
            Console.WriteLine($"Selected runtime: {repair.SelectedRuntime}");
        }
        if (!string.IsNullOrWhiteSpace(repair.SnapshotPath))
        {
            Console.WriteLine($"Snapshot: {repair.SnapshotPath}");
        }
        if (repair.Steps.Count > 0)
        {
            foreach (var step in repair.Steps)
            {
                var detail = string.IsNullOrWhiteSpace(step.Detail) ? string.Empty : $" ({step.Detail})";
                Console.WriteLine($"Step {step.Step}: {step.Status}{detail}");
            }
        }
        if (repair.Attempts.Count > 0)
        {
            foreach (var attempt in repair.Attempts)
            {
                Console.WriteLine($"Attempt {attempt.StepId}: {(attempt.Succeeded ? "ok" : "fail")} - {attempt.Summary}");
            }
        }
        if (repair.SuggestedActions.Count > 0)
        {
            Console.WriteLine("Suggested actions:");
            foreach (var suggestion in repair.SuggestedActions)
            {
                Console.WriteLine($"- {suggestion}");
            }
        }
        if (repair.Notes.Count > 0)
        {
            Console.WriteLine("Notes:");
            foreach (var note in repair.Notes)
            {
                Console.WriteLine($"- {note}");
            }
        }
        if (repair.FinalCommand != null)
        {
            RenderCommandFooter(repair.FinalCommand);
        }
        RenderJournalFooter(repair);
    }

    private static void RenderOpenClawRebuildSummary(OpenClawRebuildSummary rebuild)
    {
        var preserve = rebuild.PreserveScopes.Count == 0 ? "(none)" : string.Join(", ", rebuild.PreserveScopes);
        Console.WriteLine($"Backup: {rebuild.BackupPath}");
        Console.WriteLine($"Preserve: {preserve}");
        Console.WriteLine($"Runtime strategy: {rebuild.RuntimeStrategy}");
        Console.WriteLine("Destructive confirmation: required (confirmed)");

        if (rebuild.RemovedItems.Count > 0)
        {
            Console.WriteLine($"Removed: {string.Join(", ", rebuild.RemovedItems)}");
        }
        if (rebuild.InstalledItems.Count > 0)
        {
            Console.WriteLine($"Installed: {string.Join(", ", rebuild.InstalledItems)}");
        }

        var gatewayUrl = rebuild.Verification.GatewayUrl ?? rebuild.Verification.BrowserDiagnostics?.LocalUrl;
        if (!string.IsNullOrWhiteSpace(gatewayUrl))
        {
            Console.WriteLine($"Gateway URL: {gatewayUrl}");
        }
        if (!string.IsNullOrWhiteSpace(rebuild.Verification.BrowserDiagnostics?.DashboardUrl))
        {
            Console.WriteLine($"Dashboard URL: {rebuild.Verification.BrowserDiagnostics!.DashboardUrl}");
        }

        if (rebuild.Verification.GatewayStatus != null)
        {
            Console.WriteLine($"Gateway status: exit {rebuild.Verification.GatewayStatus.ExitCode}");
        }
        if (rebuild.Verification.DashboardStatus != null)
        {
            Console.WriteLine($"Dashboard status: exit {rebuild.Verification.DashboardStatus.ExitCode}");
        }
        if (rebuild.Verification.LogsStatus != null)
        {
            var logs = rebuild.Verification.LogsStatus;
            var detail = logs.TimedOut ? "timed out (expected)" : $"exit {logs.ExitCode}";
            Console.WriteLine($"Logs: {detail}");
        }
        Console.WriteLine($"Gateway healthy: {rebuild.Verification.GatewayHealthy}");
        Console.WriteLine($"Logs available: {rebuild.Verification.LogsAvailable}");
        Console.WriteLine($"Browser ready: {rebuild.Verification.BrowserReady}");

        if (rebuild.Verification.VerificationFailures is { Count: > 0 } failures)
        {
            foreach (var failure in failures)
            {
                Console.WriteLine($"Verification failure: {failure}");
            }
        }

        if (rebuild.Verification.VerificationWarnings is { Count: > 0 } warnings)
        {
            foreach (var warning in warnings)
            {
                Console.WriteLine($"Verification warning: {warning}");
            }
        }

        if (rebuild.Verification.GatewayDiagnostics is { } diagnostics)
        {
            Console.WriteLine($"Gateway service exists: {diagnostics.ServiceExists}");
            Console.WriteLine($"Gateway service active: {diagnostics.ServiceActive}");
            if (!string.IsNullOrWhiteSpace(diagnostics.ServiceEntrypoint))
            {
                Console.WriteLine($"Service entrypoint: {diagnostics.ServiceEntrypoint}");
            }
            if (!string.IsNullOrWhiteSpace(diagnostics.ServiceStatus))
            {
                Console.WriteLine($"Service status: {diagnostics.ServiceStatus}");
            }
            if (diagnostics.ServiceTaskStatus != null)
            {
                Console.WriteLine($"Service/task command: {diagnostics.ServiceTaskStatus.Command} (exit {diagnostics.ServiceTaskStatus.ExitCode})");
            }
            if (!string.IsNullOrWhiteSpace(diagnostics.Remediation))
            {
                Console.WriteLine($"Remediation: {diagnostics.Remediation}");
            }
        }

        if (rebuild.Steps.Count > 0)
        {
            foreach (var step in rebuild.Steps)
            {
                var detail = string.IsNullOrWhiteSpace(step.Detail) ? string.Empty : $" ({step.Detail})";
                Console.WriteLine($"Step {step.StepId}: {step.Status}{detail}");
            }
        }

        RenderJournalFooter(rebuild);
    }

    private static void RenderCommandFooter(OpenClawCommandSummary command)
    {
        if (command.ExitCode != 0)
        {
            Console.WriteLine($"OpenClaw exit code: {command.ExitCode}");
        }
        if (command.OutputTruncated)
        {
            Console.WriteLine("Output truncated; use --json for full capture.");
        }
    }

    private static void RenderDiagnosticsFooter(string? bundlePath)
    {
        if (!string.IsNullOrWhiteSpace(bundlePath))
        {
            Console.WriteLine($"Diagnostics bundle: {bundlePath}");
        }
    }

    private static void RenderJournalFooter(object? output)
    {
        if (output is IJournalCarrier carrier && !string.IsNullOrWhiteSpace(carrier.JournalPath))
        {
            Console.WriteLine($"Journal: {carrier.JournalPath}");
        }
    }

    private static void RenderFailureDetails(CliResultEnvelope envelope)
    {
        var details = envelope.Details is FailureDetails failure
            ? failure.Output
            : envelope.Details;

        switch (details)
        {
            case RestoreSummary restore:
                if (!string.IsNullOrWhiteSpace(restore.SnapshotPath))
                {
                    Console.Error.WriteLine($"Rollback snapshot: {restore.SnapshotPath}");
                }
                RenderJournalFooter(restore);
                break;
            case FixSummary fix:
                if (!string.IsNullOrWhiteSpace(fix.SnapshotPath))
                {
                    Console.Error.WriteLine($"Snapshot: {fix.SnapshotPath}");
                }
                RenderDiagnosticsFooter(fix.DiagnosticsBundlePath);
                RenderJournalFooter(fix);
                break;
            case RecoverSummary recover:
                if (recover.Restore?.SnapshotPath is { Length: > 0 } snapshot)
                {
                    Console.Error.WriteLine($"Rollback snapshot: {snapshot}");
                }
                RenderDiagnosticsFooter(recover.DiagnosticsBundlePath);
                RenderJournalFooter(recover);
                break;
            case RollbackSummary rollback:
                Console.Error.WriteLine($"Snapshot: {rollback.SnapshotPath}");
                RenderJournalFooter(rollback);
                break;
            case ResetSummary reset:
                RenderResetSummary(reset);
                break;
            case GatewayRepairSummary repair:
                RenderGatewayRepairSummary(repair);
                break;
            case OpenClawRebuildSummary rebuild:
                RenderOpenClawRebuildSummary(rebuild);
                break;
        }
    }

    private static void RenderWarnings(CliResultEnvelope envelope)
    {
        if (envelope.Warnings.Count == 0) return;
        foreach (var warning in envelope.Warnings)
        {
            Console.WriteLine($"Warning [{warning.Code}]: {warning.Message}");
        }
    }
}

internal sealed record FailureDetails(string Error, object? Output);

internal static class CliExitCodes
{
    public const int Success = 0;
    public const int ActionFailed = 1;
    public const int InvalidUsage = 2;
}
