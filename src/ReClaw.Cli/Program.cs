using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Actions;
using ReClaw.App.Execution;
using ReClaw.App.Platform;
using ReClaw.App.Schemas;
using ReClaw.Cli;
using ReClaw.Core;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("ReClaw CLI (preview)");
        var (registry, validators, executor) = DefaultActionRegistry.Create();
        var context = CreateContext();

        var actionList = new Command("action-list", "List available actions");
        actionList.SetAction(_ =>
        {
            foreach (var descriptor in registry.Descriptors)
            {
                Console.WriteLine($"{descriptor.Id}\t{descriptor.Label}");
            }
        });

        var actionSchema = new Command("action-schema", "Export action schema(s)");
        var actionIdOption = new Option<string?>("--id") { Description = "Action id to export" };
        actionSchema.Add(actionIdOption);
        actionSchema.SetAction(result =>
        {
            var actionId = result.GetValue(actionIdOption);
            if (!string.IsNullOrWhiteSpace(actionId))
            {
                var descriptor = registry.GetDescriptor(actionId);
                var schema = ActionSchemaExporter.Export(descriptor);
                Console.WriteLine(schema.InputSchema.ToJson());
                Console.WriteLine(schema.OutputSchema.ToJson());
                return;
            }

            var doc = ActionSchemaExporter.ExportAll(registry.Descriptors);
            foreach (var schema in doc.Actions)
            {
                Console.WriteLine($"## {schema.ActionId}");
                Console.WriteLine(schema.InputSchema.ToJson());
                Console.WriteLine(schema.OutputSchema.ToJson());
            }
        });

        var backup = new Command("backup", "Backup operations");

        var sourceOption = new Option<string?>("--source", new[] { "-s" }) { Description = "Source directory to back up" };
        var outOption = new Option<string?>("--out", new[] { "-o" }) { Description = "Output archive path" };
        var passwordOption = new Option<string?>("--password", new[] { "-p" }) { Description = "Optional password for encryption" };
        var verifyOption = new Option<bool>("--verify") { Description = "Verify after backup" };
        var scopeOption = new Option<string?>("--scope") { Description = "Backup scope" };
        var jsonOption = new Option<bool>("--json") { Description = "Output JSON" };

        var backupCreate = new Command("create", "Create a backup");
        backupCreate.Add(sourceOption);
        backupCreate.Add(outOption);
        backupCreate.Add(passwordOption);
        backupCreate.Add(verifyOption);
        backupCreate.Add(scopeOption);
        backupCreate.Add(jsonOption);
        backupCreate.SetAction(async result =>
        {
            var input = new BackupCreateInput(
                result.GetValue(sourceOption),
                result.GetValue(outOption),
                ResolvePassword(result.GetValue(passwordOption)),
                result.GetValue(verifyOption),
                result.GetValue(scopeOption));
            return await RunActionAsync(executor, "backup-create", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var snapshotOption = new Option<string?>("--snapshot", new[] { "-s" }) { Description = "Snapshot/archive path" };
        var backupVerify = new Command("verify", "Verify a backup");
        backupVerify.Add(snapshotOption);
        backupVerify.Add(passwordOption);
        backupVerify.Add(jsonOption);
        backupVerify.SetAction(async result =>
        {
            var input = new BackupVerifyInput(
                result.GetValue(snapshotOption),
                ResolvePassword(result.GetValue(passwordOption)));
            return await RunActionAsync(executor, "backup-verify", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var backupList = new Command("list", "List backups");
        backupList.SetAction(async _ =>
        {
            return await RunActionAsync(executor, "reclaw-backup-list", new BackupListInput(), context, json: false).ConfigureAwait(false);
        });

        var keepLastOption = new Option<int>("--keep-last") { Description = "Keep last N backups" };
        var olderThanOption = new Option<string?>("--older-than") { Description = "Prune backups older than (e.g. 30d)" };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Plan only" };
        var applyOption = new Option<bool>("--apply") { Description = "Apply prune now" };
        var backupPrune = new Command("prune", "Plan or prune backups");
        backupPrune.Add(keepLastOption);
        backupPrune.Add(olderThanOption);
        backupPrune.Add(dryRunOption);
        backupPrune.Add(applyOption);
        backupPrune.SetAction(async result =>
        {
            var keepLast = result.GetResult(keepLastOption) is null ? 5 : result.GetValue(keepLastOption);
            var dryRun = result.GetResult(dryRunOption) is null ? true : result.GetValue(dryRunOption);
            if (result.GetValue(applyOption))
            {
                dryRun = false;
            }
            var input = new BackupPruneInput(
                keepLast,
                result.GetValue(olderThanOption) ?? "30d",
                dryRun);
            return await RunActionAsync(executor, "backup-prune", input, context, json: false).ConfigureAwait(false);
        });

        var backupExport = new Command("export", "Export scoped backup");
        var exportScopeOption = new Option<string?>("--scope") { Description = "Scope (config+creds+sessions)" };
        var exportVerifyOption = new Option<bool>("--verify") { Description = "Verify after export" };
        var exportNoEncryptOption = new Option<bool>("--no-encrypt") { Description = "Export without encryption" };
        backupExport.Add(exportScopeOption);
        backupExport.Add(exportVerifyOption);
        backupExport.Add(outOption);
        backupExport.Add(passwordOption);
        backupExport.Add(exportNoEncryptOption);
        backupExport.Add(jsonOption);
        backupExport.SetAction(async result =>
        {
            var input = new BackupExportInput(
                result.GetValue(exportScopeOption) ?? "config+creds+sessions",
                result.GetValue(exportVerifyOption),
                result.GetValue(outOption),
                ResolvePassword(result.GetValue(passwordOption)),
                Encrypt: !result.GetValue(exportNoEncryptOption));
            return await RunActionAsync(executor, "backup-export", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var diffLeftOption = new Option<string?>("--left") { Description = "Left backup archive" };
        var diffRightOption = new Option<string?>("--right") { Description = "Right backup archive" };
        var diffNoRedactOption = new Option<bool>("--no-redact") { Description = "Disable redaction in diff output" };
        var backupDiff = new Command("diff", "Compare two backups");
        backupDiff.Add(diffLeftOption);
        backupDiff.Add(diffRightOption);
        backupDiff.Add(passwordOption);
        backupDiff.Add(diffNoRedactOption);
        backupDiff.Add(jsonOption);
        backupDiff.SetAction(async result =>
        {
            var input = new BackupDiffInput(
                result.GetValue(diffLeftOption),
                result.GetValue(diffRightOption),
                RedactSecrets: !result.GetValue(diffNoRedactOption),
                Password: ResolvePassword(result.GetValue(passwordOption)));
            return await RunActionAsync(executor, "backup-diff", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var schedule = new Command("schedule", "Schedule backups");
        var scheduleModeOption = new Option<string?>("--mode") { Description = "gateway|os" };
        var scheduleKindOption = new Option<string?>("--kind") { Description = "daily|weekly|monthly|cron" };
        var scheduleExpressionOption = new Option<string?>("--expression") { Description = "Cron expression" };
        var scheduleAtOption = new Option<string?>("--at") { Description = "Time (HH:mm)" };
        var scheduleDayOption = new Option<string?>("--day-of-week") { Description = "Weekday (Mon..Sun)" };
        var scheduleDayOfMonthOption = new Option<int?>("--day-of-month") { Description = "Day of month (1-28)" };
        var scheduleKeepLastOption = new Option<int>("--keep-last") { Description = "Retention: keep last N" };
        var scheduleOlderThanOption = new Option<string>("--older-than") { Description = "Retention: older than (e.g. 30d)" };
        var scheduleVerifyOption = new Option<bool>("--verify-after") { Description = "Verify after backup" };
        var scheduleIncludeWorkspaceOption = new Option<bool>("--include-workspace") { Description = "Include workspace in backup" };
        var scheduleOnlyConfigOption = new Option<bool>("--only-config") { Description = "Only backup config" };

        var scheduleCreate = new Command("create", "Create a backup schedule");
        scheduleCreate.Add(scheduleModeOption);
        scheduleCreate.Add(scheduleKindOption);
        scheduleCreate.Add(scheduleExpressionOption);
        scheduleCreate.Add(scheduleAtOption);
        scheduleCreate.Add(scheduleDayOption);
        scheduleCreate.Add(scheduleDayOfMonthOption);
        scheduleCreate.Add(scheduleKeepLastOption);
        scheduleCreate.Add(scheduleOlderThanOption);
        scheduleCreate.Add(scheduleVerifyOption);
        scheduleCreate.Add(scheduleIncludeWorkspaceOption);
        scheduleCreate.Add(scheduleOnlyConfigOption);
        scheduleCreate.Add(jsonOption);
        scheduleCreate.SetAction(async result =>
        {
            if (!TryParseScheduleMode(result.GetValue(scheduleModeOption), out var mode, out var modeError))
            {
                var errorResult = new ActionResult(false, Error: modeError, ExitCode: CliExitCodes.InvalidUsage);
                return CliResultFormatter.Render("backup-schedule-create", errorResult, result.GetValue(jsonOption));
            }

            if (!TryParseScheduleKind(result.GetValue(scheduleKindOption), out var kind, out var kindError))
            {
                var errorResult = new ActionResult(false, Error: kindError, ExitCode: CliExitCodes.InvalidUsage);
                return CliResultFormatter.Render("backup-schedule-create", errorResult, result.GetValue(jsonOption));
            }

            var includeWorkspace = result.GetResult(scheduleIncludeWorkspaceOption) is null
                ? true
                : result.GetValue(scheduleIncludeWorkspaceOption);
            var onlyConfig = result.GetValue(scheduleOnlyConfigOption);
            if (onlyConfig)
            {
                includeWorkspace = false;
            }

            var keepLast = result.GetResult(scheduleKeepLastOption) is null ? 7 : result.GetValue(scheduleKeepLastOption);
            var olderThan = result.GetValue(scheduleOlderThanOption) ?? "30d";
            var verifyAfter = result.GetResult(scheduleVerifyOption) is null ? true : result.GetValue(scheduleVerifyOption);

            var input = new BackupScheduleInput(
                mode,
                kind,
                result.GetValue(scheduleExpressionOption),
                result.GetValue(scheduleAtOption),
                result.GetValue(scheduleDayOption),
                result.GetValue(scheduleDayOfMonthOption),
                keepLast,
                olderThan,
                verifyAfter,
                includeWorkspace,
                onlyConfig);

            return await RunActionAsync(executor, "backup-schedule-create", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var scheduleList = new Command("list", "List backup schedules");
        scheduleList.Add(jsonOption);
        scheduleList.SetAction(async result =>
        {
            return await RunActionAsync(executor, "backup-schedule-list", new BackupScheduleListInput(), context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var scheduleRemove = new Command("remove", "Remove a backup schedule");
        var scheduleIdOption = new Option<string?>("--id") { Description = "Schedule id" };
        scheduleRemove.Add(scheduleIdOption);
        scheduleRemove.Add(jsonOption);
        scheduleRemove.SetAction(async result =>
        {
            var input = new BackupScheduleRemoveInput(result.GetValue(scheduleIdOption));
            return await RunActionAsync(executor, "backup-schedule-remove", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        schedule.Add(scheduleCreate);
        schedule.Add(scheduleList);
        schedule.Add(scheduleRemove);

        var destOption = new Option<string?>("--dest", new[] { "-d" }) { Description = "Destination path" };
        var restoreScopeOption = new Option<string?>("--scope") { Description = "Restore scope" };
        var previewOption = new Option<bool>("--preview") { Description = "Preview restore impact without applying changes" };
        var safeResetOption = new Option<bool>("--safe-reset") { Description = "Run safe reset before restore" };
        var skipVerifyOption = new Option<bool>("--skip-verify") { Description = "Skip backup verify before restore" };
        var resetModeOption = new Option<string?>("--reset-mode") { Description = "Reset mode (preserve-cli, preserve-config, preserve-backups, full)" };
        var confirmResetOption = new Option<bool>("--confirm-reset") { Description = "Confirm reset before restore" };
        var backupRestore = new Command("restore", "Restore a backup");
        backupRestore.Add(snapshotOption);
        backupRestore.Add(destOption);
        backupRestore.Add(passwordOption);
        backupRestore.Add(restoreScopeOption);
        backupRestore.Add(previewOption);
        backupRestore.Add(safeResetOption);
        backupRestore.Add(skipVerifyOption);
        backupRestore.Add(resetModeOption);
        backupRestore.Add(confirmResetOption);
        backupRestore.Add(jsonOption);
        backupRestore.SetAction(async result =>
        {
            if (!TryParseResetMode(result.GetValue(resetModeOption), out var resetMode, out var resetError))
            {
                var errorResult = new ActionResult(false, Error: resetError, ExitCode: CliExitCodes.InvalidUsage);
                return CliResultFormatter.Render("backup-restore", errorResult, result.GetValue(jsonOption));
            }

            var input = new BackupRestoreInput(
                result.GetValue(snapshotOption),
                result.GetValue(destOption),
                ResolvePassword(result.GetValue(passwordOption)),
                result.GetValue(restoreScopeOption),
                result.GetValue(previewOption),
                result.GetValue(safeResetOption),
                resetMode,
                result.GetValue(confirmResetOption),
                VerifyFirst: !result.GetValue(skipVerifyOption));
            return await RunActionAsync(executor, "backup-restore", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        backup.Add(backupCreate);
        backup.Add(backupVerify);
        backup.Add(backupList);
        backup.Add(backupPrune);
        backup.Add(backupExport);
        backup.Add(backupRestore);
        backup.Add(backupDiff);
        backup.Add(schedule);

        var confirmRollbackOption = new Option<bool>("--confirm-rollback") { Description = "Confirm rollback before applying changes" };
        var rollback = new Command("rollback", "Rollback from snapshot archive");
        rollback.Add(snapshotOption);
        rollback.Add(destOption);
        rollback.Add(passwordOption);
        rollback.Add(restoreScopeOption);
        rollback.Add(previewOption);
        rollback.Add(confirmRollbackOption);
        rollback.Add(jsonOption);
        rollback.SetAction(async result =>
        {
            var snapshot = result.GetValue(snapshotOption);
            if (string.IsNullOrWhiteSpace(snapshot))
            {
                var errorResult = new ActionResult(false, Error: "Snapshot path is required.", ExitCode: CliExitCodes.InvalidUsage);
                return CliResultFormatter.Render("rollback", errorResult, result.GetValue(jsonOption));
            }

            var input = new RollbackInput(
                snapshot,
                result.GetValue(destOption),
                ResolvePassword(result.GetValue(passwordOption)),
                result.GetValue(restoreScopeOption),
                result.GetValue(previewOption),
                result.GetValue(confirmRollbackOption));
            return await RunActionAsync(executor, "rollback", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var resetPreviewOption = new Option<bool>("--preview") { Description = "Preview reset plan without applying changes" };
        var resetConfirmOption = new Option<bool>("--confirm") { Description = "Confirm reset before applying changes" };
        var reset = new Command("reset", "Reset local data using safe plan");
        reset.Add(resetModeOption);
        reset.Add(resetPreviewOption);
        reset.Add(resetConfirmOption);
        reset.Add(jsonOption);
        reset.SetAction(async result =>
        {
            if (!TryParseResetMode(result.GetValue(resetModeOption), out var resetMode, out var resetError))
            {
                var errorResult = new ActionResult(false, Error: resetError, ExitCode: CliExitCodes.InvalidUsage);
                return CliResultFormatter.Render("reset", errorResult, result.GetValue(jsonOption));
            }

            var input = new ResetInput(
                resetMode,
                result.GetValue(resetPreviewOption),
                result.GetValue(resetConfirmOption));
            return await RunActionAsync(executor, "reset", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var doctor = new Command("doctor", "Run OpenClaw doctor");
        var doctorRepairOption = new Option<bool>("--repair") { Description = "Run doctor --repair" };
        var doctorForceOption = new Option<bool>("--force") { Description = "Run doctor --force" };
        var doctorDeepOption = new Option<bool>("--deep") { Description = "Run doctor --deep" };
        var doctorTokenOption = new Option<bool>("--generate-token") { Description = "Run doctor --generate-gateway-token" };
        var doctorInteractiveOption = new Option<bool>("--interactive") { Description = "Allow interactive prompts" };
        var doctorNoYesOption = new Option<bool>("--no-yes") { Description = "Do not auto-confirm prompts" };
        var doctorDiagnosticsOption = new Option<bool>("--diagnostics") { Description = "Export diagnostics bundle" };
        var diagnosticsOutOption = new Option<string?>("--diagnostics-out") { Description = "Diagnostics bundle output path" };
        doctor.Add(doctorRepairOption);
        doctor.Add(doctorForceOption);
        doctor.Add(doctorDeepOption);
        doctor.Add(doctorTokenOption);
        doctor.Add(doctorInteractiveOption);
        doctor.Add(doctorNoYesOption);
        doctor.Add(doctorDiagnosticsOption);
        doctor.Add(diagnosticsOutOption);
        doctor.Add(jsonOption);
        doctor.SetAction(async result =>
        {
            var input = new DoctorInput(
                Repair: result.GetValue(doctorRepairOption),
                Force: result.GetValue(doctorForceOption),
                Deep: result.GetValue(doctorDeepOption),
                NonInteractive: !result.GetValue(doctorInteractiveOption),
                Yes: !result.GetValue(doctorNoYesOption),
                GenerateToken: result.GetValue(doctorTokenOption),
                ExportDiagnostics: result.GetValue(doctorDiagnosticsOption),
                DiagnosticsOutputPath: result.GetValue(diagnosticsOutOption));
            return await RunActionAsync(executor, "doctor", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var fix = new Command("fix", "Run OpenClaw doctor --fix with snapshot safety");
        var fixForceOption = new Option<bool>("--force") { Description = "Run doctor --fix --force" };
        var fixInteractiveOption = new Option<bool>("--interactive") { Description = "Allow interactive prompts" };
        var fixNoYesOption = new Option<bool>("--no-yes") { Description = "Do not auto-confirm prompts" };
        var fixSnapshotOption = new Option<string?>("--snapshot") { Description = "Snapshot/archive output path" };
        var fixDiagnosticsOption = new Option<bool>("--diagnostics") { Description = "Export diagnostics bundle" };
        fix.Add(passwordOption);
        fix.Add(fixForceOption);
        fix.Add(fixInteractiveOption);
        fix.Add(fixNoYesOption);
        fix.Add(fixSnapshotOption);
        fix.Add(fixDiagnosticsOption);
        fix.Add(diagnosticsOutOption);
        fix.Add(jsonOption);
        fix.SetAction(async result =>
        {
            var input = new FixInput(
                Password: ResolvePassword(result.GetValue(passwordOption)),
                NonInteractive: !result.GetValue(fixInteractiveOption),
                Yes: !result.GetValue(fixNoYesOption),
                Force: result.GetValue(fixForceOption),
                SnapshotPath: result.GetValue(fixSnapshotOption),
                ExportDiagnostics: result.GetValue(fixDiagnosticsOption),
                DiagnosticsOutputPath: result.GetValue(diagnosticsOutOption));
            return await RunActionAsync(executor, "fix", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var recover = new Command("recover", "Run fix + restore recovery flow");
        var recoverNoResetOption = new Option<bool>("--no-reset") { Description = "Skip safe reset before restore" };
        var recoverNoDoctorOption = new Option<bool>("--no-doctor") { Description = "Skip doctor step" };
        var recoverNoFixOption = new Option<bool>("--no-fix") { Description = "Skip fix step" };
        var recoverNoDiagnosticsOption = new Option<bool>("--no-diagnostics") { Description = "Skip diagnostics export" };
        recover.Add(snapshotOption);
        recover.Add(destOption);
        recover.Add(passwordOption);
        recover.Add(restoreScopeOption);
        recover.Add(previewOption);
        recover.Add(recoverNoResetOption);
        recover.Add(resetModeOption);
        recover.Add(confirmResetOption);
        recover.Add(recoverNoDoctorOption);
        recover.Add(recoverNoFixOption);
        recover.Add(recoverNoDiagnosticsOption);
        recover.Add(diagnosticsOutOption);
        recover.Add(jsonOption);
        recover.SetAction(async result =>
        {
            if (!TryParseResetMode(result.GetValue(resetModeOption), out var resetMode, out var resetError))
            {
                var errorResult = new ActionResult(false, Error: resetError, ExitCode: CliExitCodes.InvalidUsage);
                return CliResultFormatter.Render("recover", errorResult, result.GetValue(jsonOption));
            }

            var input = new RecoverInput(
                result.GetValue(snapshotOption),
                result.GetValue(destOption),
                ResolvePassword(result.GetValue(passwordOption)),
                result.GetValue(restoreScopeOption),
                result.GetValue(previewOption),
                SafeReset: !result.GetValue(recoverNoResetOption),
                ResetMode: resetMode,
                ConfirmReset: result.GetValue(confirmResetOption),
                RunDoctor: !result.GetValue(recoverNoDoctorOption),
                RunFix: !result.GetValue(recoverNoFixOption),
                ExportDiagnostics: !result.GetValue(recoverNoDiagnosticsOption),
                DiagnosticsOutputPath: result.GetValue(diagnosticsOutOption));
            return await RunActionAsync(executor, "recover", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var diagnostics = new Command("diagnostics", "Diagnostics operations");
        var diagnosticsExport = new Command("export", "Export diagnostics bundle");
        diagnosticsExport.Add(outOption);
        diagnosticsExport.Add(jsonOption);
        diagnosticsExport.SetAction(async result =>
        {
            var input = new DiagnosticsExportInput(result.GetValue(outOption));
            return await RunActionAsync(executor, "diagnostics-export", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });
        diagnostics.Add(diagnosticsExport);

        var status = new Command("status", "Show local paths and backup inventory");
        status.Add(jsonOption);
        status.SetAction(async result =>
        {
            return await RunActionAsync(executor, "status", new StatusInput(), context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var gateway = new Command("gateway", "Gateway operations");
        var gatewayStart = new Command("start", "Start the OpenClaw gateway");
        gatewayStart.Add(jsonOption);
        gatewayStart.SetAction(async result =>
        {
            return await RunActionAsync(executor, "gateway-start", new EmptyInput(), context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });
        var gatewayStatus = new Command("status", "Check gateway status");
        gatewayStatus.Add(jsonOption);
        gatewayStatus.SetAction(async result =>
        {
            return await RunActionAsync(executor, "gateway-status", new EmptyInput(), context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });
        var gatewayStop = new Command("stop", "Stop the OpenClaw gateway");
        gatewayStop.Add(jsonOption);
        gatewayStop.SetAction(async result =>
        {
            return await RunActionAsync(executor, "gateway-stop", new EmptyInput(), context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });
        var gatewayLogs = new Command("logs", "Tail gateway logs");
        gatewayLogs.Add(jsonOption);
        gatewayLogs.SetAction(async result =>
        {
            return await RunActionAsync(executor, "gateway-logs", new EmptyInput(), context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });
        var gatewayRepair = new Command("repair", "Repair the OpenClaw gateway");
        gatewayRepair.Add(jsonOption);
        gatewayRepair.SetAction(async result =>
        {
            return await RunActionAsync(executor, "gateway-repair", new EmptyInput(), context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });
        var gatewayUrl = new Command("url", "Show dashboard URL");
        var gatewayUrlModeOption = new Option<string?>("--mode") { Description = "local|remote" };
        var gatewayRuntimeOption = new Option<string?>("--runtime") { Description = "Override OpenClaw runtime path" };
        var gatewayConfigOption = new Option<string?>("--config") { Description = "Override OpenClaw config path" };
        gatewayUrl.Add(gatewayUrlModeOption);
        gatewayUrl.Add(gatewayRuntimeOption);
        gatewayUrl.Add(gatewayConfigOption);
        gatewayUrl.Add(jsonOption);
        gatewayUrl.SetAction(async result =>
        {
            if (!TryParseBrowserMode(result.GetValue(gatewayUrlModeOption), out var mode, out var modeError))
            {
                var errorResult = new ActionResult(false, Error: modeError, ExitCode: CliExitCodes.InvalidUsage);
                return CliResultFormatter.Render("gateway-url", errorResult, result.GetValue(jsonOption));
            }
            var input = new GatewayUrlInput(mode, result.GetValue(gatewayRuntimeOption), result.GetValue(gatewayConfigOption));
            return await RunActionAsync(executor, "gateway-url", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });
        var gatewayToken = new Command("token", "Gateway token operations");
        var tokenRevealOption = new Option<bool>("--reveal") { Description = "Reveal full token" };
        var gatewayTokenShow = new Command("show", "Show gateway token");
        gatewayTokenShow.Add(tokenRevealOption);
        gatewayTokenShow.Add(jsonOption);
        gatewayTokenShow.SetAction(async result =>
        {
            var input = new GatewayTokenInput(result.GetValue(tokenRevealOption));
            return await RunActionAsync(executor, "gateway-token-show", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });
        var gatewayTokenGenerate = new Command("generate", "Generate gateway token");
        gatewayTokenGenerate.Add(jsonOption);
        gatewayTokenGenerate.SetAction(async result =>
        {
            return await RunActionAsync(executor, "gateway-token-generate", new EmptyInput(), context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });
        gatewayToken.Add(gatewayTokenShow);
        gatewayToken.Add(gatewayTokenGenerate);
        var gatewayDiagnostics = new Command("browser-diagnostics", "Run browser access diagnostics");
        var browserModeOption = new Option<string?>("--mode") { Description = "local|remote" };
        var browserRuntimeOption = new Option<string?>("--runtime") { Description = "Override OpenClaw runtime path" };
        var browserConfigOption = new Option<string?>("--config") { Description = "Override OpenClaw config path" };
        gatewayDiagnostics.Add(browserModeOption);
        gatewayDiagnostics.Add(browserRuntimeOption);
        gatewayDiagnostics.Add(browserConfigOption);
        gatewayDiagnostics.Add(jsonOption);
        gatewayDiagnostics.SetAction(async result =>
        {
            if (!TryParseBrowserMode(result.GetValue(browserModeOption), out var mode, out var modeError))
            {
                var errorResult = new ActionResult(false, Error: modeError, ExitCode: CliExitCodes.InvalidUsage);
                return CliResultFormatter.Render("gateway-browser-diagnostics", errorResult, result.GetValue(jsonOption));
            }

            var input = new BrowserDiagnosticsInput(
                mode,
                result.GetValue(browserRuntimeOption),
                result.GetValue(browserConfigOption));
            return await RunActionAsync(executor, "gateway-browser-diagnostics", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });
        gateway.Add(gatewayStart);
        gateway.Add(gatewayStatus);
        gateway.Add(gatewayStop);
        gateway.Add(gatewayLogs);
        gateway.Add(gatewayRepair);
        gateway.Add(gatewayUrl);
        gateway.Add(gatewayToken);
        gateway.Add(gatewayDiagnostics);

        var cleanupRelated = new Command("cleanup-related", "List or clean related OpenClaw artifacts");
        var cleanupApplyOption = new Option<bool>("--apply") { Description = "Apply cleanup (requires --confirm)" };
        var cleanupConfirmOption = new Option<bool>("--confirm") { Description = "Confirm cleanup" };
        cleanupRelated.Add(cleanupApplyOption);
        cleanupRelated.Add(cleanupConfirmOption);
        cleanupRelated.Add(jsonOption);
        cleanupRelated.SetAction(async result =>
        {
            var input = new OpenClawCleanupInput(
                Apply: result.GetValue(cleanupApplyOption),
                Confirm: result.GetValue(cleanupConfirmOption));
            return await RunActionAsync(executor, "openclaw-cleanup-related", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var openclaw = new Command("openclaw", "OpenClaw maintenance");
        var openclawCleanup = new Command("cleanup-related", "List or clean related OpenClaw artifacts");
        openclawCleanup.Add(cleanupApplyOption);
        openclawCleanup.Add(cleanupConfirmOption);
        openclawCleanup.Add(jsonOption);
        openclawCleanup.SetAction(async result =>
        {
            var input = new OpenClawCleanupInput(
                Apply: result.GetValue(cleanupApplyOption),
                Confirm: result.GetValue(cleanupConfirmOption));
            return await RunActionAsync(executor, "openclaw-cleanup-related", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        var noPreserveConfigOption = new Option<bool>("--no-preserve-config") { Description = "Do not preserve config on rebuild" };
        var noPreserveCredsOption = new Option<bool>("--no-preserve-creds") { Description = "Do not preserve credentials on rebuild" };
        var noPreserveSessionsOption = new Option<bool>("--no-preserve-sessions") { Description = "Do not preserve sessions on rebuild" };
        var noPreserveWorkspaceOption = new Option<bool>("--no-preserve-workspace") { Description = "Do not preserve workspace on rebuild" };
        var cleanInstallOption = new Option<bool>("--clean-install") { Description = "Perform a clean install during rebuild" };
        var confirmDestructiveOption = new Option<bool>("--confirm-destructive") { Description = "Confirm destructive rebuild" };
        var openclawRebuild = new Command("rebuild", "Rebuild OpenClaw");
        openclawRebuild.Add(noPreserveConfigOption);
        openclawRebuild.Add(noPreserveCredsOption);
        openclawRebuild.Add(noPreserveSessionsOption);
        openclawRebuild.Add(noPreserveWorkspaceOption);
        openclawRebuild.Add(cleanInstallOption);
        openclawRebuild.Add(confirmDestructiveOption);
        openclawRebuild.Add(passwordOption);
        openclawRebuild.Add(jsonOption);
        openclawRebuild.SetAction(async result =>
        {
            var input = new OpenClawRebuildInput(
                PreserveConfig: !result.GetValue(noPreserveConfigOption),
                PreserveCredentials: !result.GetValue(noPreserveCredsOption),
                PreserveSessions: !result.GetValue(noPreserveSessionsOption),
                PreserveWorkspace: !result.GetValue(noPreserveWorkspaceOption),
                CleanInstall: result.GetValue(cleanInstallOption),
                ConfirmDestructive: result.GetValue(confirmDestructiveOption),
                Password: ResolvePassword(result.GetValue(passwordOption)));
            return await RunActionAsync(executor, "openclaw-rebuild", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });

        openclaw.Add(openclawCleanup);
        openclaw.Add(openclawRebuild);

        var version = new Command("version", "Show CLI version");
        version.SetAction(_ =>
        {
            var assemblyVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            Console.WriteLine($"ReClaw CLI {assemblyVersion}");
        });

        root.Add(backup);
        root.Add(doctor);
        root.Add(fix);
        root.Add(recover);
        root.Add(rollback);
        root.Add(reset);
        root.Add(status);
        root.Add(gateway);
        root.Add(cleanupRelated);
        root.Add(openclaw);
        root.Add(version);
        root.Add(diagnostics);
        root.Add(actionList);
        root.Add(actionSchema);
        var dashboard = new Command("dashboard", "Open the gateway dashboard");
        dashboard.Add(jsonOption);
        dashboard.SetAction(async result =>
        {
            return await RunActionAsync(executor, "dashboard-open", new DashboardOpenInput(), context, result.GetValue(jsonOption)).ConfigureAwait(false);
        });
        root.Add(dashboard);

        // Legacy top-level aliases (create/verify/restore)
        root.Add(new Command("create", "Create a backup")
        {
            sourceOption,
            outOption,
            passwordOption
        }.WithAliasAction(async result =>
        {
            var input = new BackupCreateInput(
                result.GetValue(sourceOption),
                result.GetValue(outOption),
                ResolvePassword(result.GetValue(passwordOption)));
            return await RunActionAsync(executor, "backup-create", input, context, json: false).ConfigureAwait(false);
        }));

        root.Add(new Command("verify", "Verify a backup")
        {
            snapshotOption,
            passwordOption,
            jsonOption
        }.WithAliasAction(async result =>
        {
            var input = new BackupVerifyInput(
                result.GetValue(snapshotOption),
                ResolvePassword(result.GetValue(passwordOption)));
            return await RunActionAsync(executor, "backup-verify", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        }));

        root.Add(new Command("restore", "Restore a backup")
        {
            snapshotOption,
            destOption,
            passwordOption,
            restoreScopeOption,
            previewOption,
            safeResetOption,
            skipVerifyOption,
            resetModeOption,
            confirmResetOption,
            jsonOption
        }.WithAliasAction(async result =>
        {
            if (!TryParseResetMode(result.GetValue(resetModeOption), out var resetMode, out var resetError))
            {
                var errorResult = new ActionResult(false, Error: resetError, ExitCode: CliExitCodes.InvalidUsage);
                return CliResultFormatter.Render("backup-restore", errorResult, result.GetValue(jsonOption));
            }

            var input = new BackupRestoreInput(
                result.GetValue(snapshotOption),
                result.GetValue(destOption),
                ResolvePassword(result.GetValue(passwordOption)),
                result.GetValue(restoreScopeOption),
                result.GetValue(previewOption),
                result.GetValue(safeResetOption),
                resetMode,
                result.GetValue(confirmResetOption),
                VerifyFirst: !result.GetValue(skipVerifyOption));
            return await RunActionAsync(executor, "backup-restore", input, context, result.GetValue(jsonOption)).ConfigureAwait(false);
        }));

        var parseResult = CommandLineParser.Parse(root, args, new ParserConfiguration());
        return await parseResult.InvokeAsync(new InvocationConfiguration());
    }

    private static ActionContext CreateContext()
    {
        var context = PathDefaults.CreateDefaultContext();
        var backupDir = Environment.GetEnvironmentVariable("RECLAW_BACKUP_DIR")
            ?? Environment.GetEnvironmentVariable("BACKUP_DIR");
        if (!string.IsNullOrWhiteSpace(backupDir))
        {
            context = context with { BackupDirectory = backupDir };
            Directory.CreateDirectory(backupDir);
        }
        return context;
    }

    private static string? ResolvePassword(string? explicitPassword)
    {
        if (!string.IsNullOrWhiteSpace(explicitPassword)) return explicitPassword;
        return Environment.GetEnvironmentVariable("RECLAW_PASSWORD");
    }

    private static bool TryParseResetMode(string? raw, out ResetMode mode, out string? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            mode = ResetMode.PreserveBackups;
            error = null;
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "preserve-cli":
            case "preserve-cli-only":
            case "cli-only":
            case "cli":
                mode = ResetMode.PreserveCliOnly;
                error = null;
                return true;
            case "preserve-config":
            case "config":
                mode = ResetMode.PreserveConfig;
                error = null;
                return true;
            case "preserve-backups":
            case "backups":
            case "backup":
                mode = ResetMode.PreserveBackups;
                error = null;
                return true;
            case "full":
            case "full-reset":
            case "full-local":
                mode = ResetMode.FullLocalReset;
                error = null;
                return true;
            default:
                mode = ResetMode.PreserveBackups;
                error = $"Invalid reset mode '{raw}'. Use: preserve-cli, preserve-config, preserve-backups, full.";
                return false;
        }
    }

    private static bool TryParseScheduleMode(string? raw, out BackupScheduleMode mode, out string? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            mode = BackupScheduleMode.Gateway;
            error = null;
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "gateway":
                mode = BackupScheduleMode.Gateway;
                error = null;
                return true;
            case "os":
            case "os-native":
            case "native":
                mode = BackupScheduleMode.OsNative;
                error = null;
                return true;
            default:
                mode = BackupScheduleMode.Gateway;
                error = $"Invalid schedule mode '{raw}'. Use: gateway, os.";
                return false;
        }
    }

    private static bool TryParseScheduleKind(string? raw, out BackupScheduleKind kind, out string? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            kind = BackupScheduleKind.Daily;
            error = null;
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "daily":
                kind = BackupScheduleKind.Daily;
                error = null;
                return true;
            case "weekly":
                kind = BackupScheduleKind.Weekly;
                error = null;
                return true;
            case "monthly":
                kind = BackupScheduleKind.Monthly;
                error = null;
                return true;
            case "cron":
                kind = BackupScheduleKind.Cron;
                error = null;
                return true;
            default:
                kind = BackupScheduleKind.Daily;
                error = $"Invalid schedule kind '{raw}'. Use: daily, weekly, monthly, cron.";
                return false;
        }
    }

    private static bool TryParseBrowserMode(string? raw, out BrowserAccessMode mode, out string? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            mode = BrowserAccessMode.Local;
            error = null;
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "local":
                mode = BrowserAccessMode.Local;
                error = null;
                return true;
            case "remote":
                mode = BrowserAccessMode.Remote;
                error = null;
                return true;
            default:
                mode = BrowserAccessMode.Local;
                error = $"Invalid browser mode '{raw}'. Use: local, remote.";
                return false;
        }
    }

    private static async Task<int> RunActionAsync(ActionExecutor executor, string actionId, object? input, ActionContext context, bool json)
    {
        var progress = json
            ? new Progress<ActionEvent>(_ => { })
            : new Progress<ActionEvent>(HandleEvent);
        var result = await executor.ExecuteAsync(actionId, input ?? new EmptyInput(), context, progress, CancellationToken.None)
            .ConfigureAwait(false);
        return CliResultFormatter.Render(actionId, result, json);
    }

    private static void HandleEvent(ActionEvent evt)
    {
        switch (evt)
        {
            case LogReceived log:
                if (log.IsError) Console.Error.WriteLine(log.Line);
                else Console.WriteLine(log.Line);
                break;
            case StatusChanged status:
                Console.WriteLine($"> {status.Status} {status.Detail}".Trim());
                break;
            case ActionFailed failed:
                Console.Error.WriteLine(failed.Error);
                break;
        }
    }

    // Output rendering is handled by CliResultFormatter.
}

internal static class CommandExtensions
{
    public static Command WithAliasAction(this Command command, Func<ParseResult, Task<int>> handler)
    {
        command.SetAction(handler);
        return command;
    }
}
