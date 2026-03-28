using System;
using System.Collections.Generic;
using ReClaw.Core;

namespace ReClaw.App.Actions;

public static class ActionCatalog
{
    public static IReadOnlyList<ActionDescriptor> All { get; } = BuildAll();

    private static IReadOnlyList<ActionDescriptor> BuildAll()
    {
        var list = new List<ActionDescriptor>();

        list.Add(InternalAction(
            "backup",
            "Save Backup",
            "Save everything safely now.",
            "easy",
            "💾",
            typeof(BackupCreateInput),
            typeof(OpenClawBackupCreateSummary),
            optionalPassword: true,
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "backup-create",
            "Backup Create",
            "Create a new backup archive.",
            "easy",
            "💾",
            typeof(BackupCreateInput),
            typeof(OpenClawBackupCreateSummary),
            optionalPassword: true,
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "backup-verify",
            "Backup Verify",
            "Verify a backup archive.",
            "tools",
            "🔍",
            typeof(BackupVerifyInput),
            typeof(BackupVerificationSummary),
            optionalPassword: true,
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "backup-restore",
            "Backup Restore",
            "Verify, preview, and restore a backup archive.",
            "tools",
            "♻️",
            typeof(BackupRestoreInput),
            typeof(RestoreSummary),
            optionalPassword: true,
            requiresArchive: true,
            tags: new[] { "supports-preview", "supports-json" }));

        list.Add(InternalAction(
            "backup-diff",
            "Backup Compare",
            "Compare two backups and summarize changes.",
            "tools",
            "🧾",
            typeof(BackupDiffInput),
            typeof(BackupDiffSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "restore-latest",
            "Restore Latest",
            "Bring back your newest saved backup.",
            "easy",
            "♻️",
            typeof(BackupRestoreInput),
            typeof(RestoreSummary),
            optionalPassword: true));

        list.Add(InternalAction(
            "restore-archive",
            "Restore From Archive",
            "Pick any backup archive and restore it.",
            "tools",
            "📦",
            typeof(BackupRestoreInput),
            typeof(RestoreSummary),
            optionalPassword: true,
            requiresArchive: true));

        list.Add(InternalAction(
            "rollback",
            "Rollback From Snapshot",
            "Restore from a rollback snapshot with confirmation.",
            "danger",
            "⏪",
            typeof(RollbackInput),
            typeof(RollbackSummary),
            capabilities: ActionCapability.Destructive,
            optionalPassword: true,
            requiresArchive: true));

        list.Add(InternalAction(
            "verify-all",
            "Check Health",
            "Quickly check files and gateway health.",
            "easy",
            "✅",
            typeof(BackupVerifyInput),
            typeof(BackupVerificationSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "status",
            "Status",
            "Show local paths and backup inventory.",
            "easy",
            "📊",
            typeof(StatusInput),
            typeof(StatusSummary)));

        list.Add(InternalAction(
            "setup-install",
            "Install OpenClaw",
            "Install OpenClaw, run onboarding, verify gateway health, repair if needed, and open the dashboard.",
            "easy",
            "🧭",
            typeof(EmptyInput),
            typeof(SetupAssistantSummary)));

        list.Add(InternalAction(
            "setup-restore",
            "Restore From Backup",
            "Install OpenClaw, onboard, restore a backup, verify health, repair if needed, and open the dashboard.",
            "tools",
            "♻️",
            typeof(BackupRestoreInput),
            typeof(SetupAssistantSummary),
            optionalPassword: true,
            requiresArchive: true));

        list.Add(InternalAction(
            "setup-advanced",
            "Setup Advanced",
            "Run advanced source install helpers.",
            "tools",
            "🧰",
            typeof(EmptyInput),
            typeof(OpenClawCommandSummary)));

        list.Add(InternalAction(
            "openclaw-terminal",
            "OpenClaw Terminal",
            "Open a terminal at the OpenClaw repo.",
            "tools",
            "🖥️",
            typeof(EmptyInput),
            typeof(OpenClawCommandSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "gateway-token-show",
            "Show Gateway Token",
            "Reveal the gateway auth token with masking.",
            "tools",
            "🔑",
            typeof(GatewayTokenInput),
            typeof(GatewayTokenSummary),
            capabilities: ActionCapability.Destructive,
            confirmPhrase: "REVEAL",
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "gateway-token-generate",
            "Generate Gateway Token",
            "Generate a new gateway auth token.",
            "tools",
            "🧬",
            typeof(EmptyInput),
            typeof(OpenClawCommandSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "openclaw-cleanup-related",
            "Cleanup Related Artifacts",
            "List and optionally clean related OpenClaw artifacts.",
            "tools",
            "🧹",
            typeof(OpenClawCleanupInput),
            typeof(OpenClawCleanupSummary),
            capabilities: ActionCapability.Destructive,
            confirmPhrase: "CLEAN",
            tags: new[] { "supports-preview", "supports-json" }));

        list.Add(InternalAction(
            "gateway-start",
            "Gateway Start",
            "Start the OpenClaw gateway.",
            "easy",
            "▶️",
            typeof(EmptyInput),
            typeof(GatewayRepairSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "gateway-stop",
            "Gateway Stop",
            "Stop the OpenClaw gateway.",
            "danger",
            "⏹️",
            typeof(EmptyInput),
            typeof(GatewayRepairSummary),
            capabilities: ActionCapability.Destructive,
            confirmPhrase: "STOP",
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "gateway-status",
            "Gateway Status",
            "Check the OpenClaw gateway status.",
            "easy",
            "📍",
            typeof(EmptyInput),
            typeof(GatewayRepairSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "gateway-troubleshoot",
            "Gateway Troubleshoot",
            "Run the official gateway troubleshooting ladder.",
            "tools",
            "🧭",
            typeof(EmptyInput),
            typeof(GatewayTroubleshootSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "gateway-logs",
            "Gateway Logs",
            "Stream gateway logs.",
            "tools",
            "📝",
            typeof(EmptyInput),
            typeof(GatewayRepairSummary),
            capabilities: ActionCapability.Cancellable,
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "gateway-url",
            "Show Dashboard Link",
            "Show secure dashboard URL with token.",
            "tools",
            "🔗",
            typeof(GatewayUrlInput),
            typeof(OpenClawCommandSummary),
            capabilities: ActionCapability.RequiresGateway,
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "dashboard-open",
            "Open Dashboard",
            "Open the secure dashboard in browser.",
            "easy",
            "🧭",
            typeof(DashboardOpenInput),
            typeof(OpenClawCommandSummary),
            capabilities: ActionCapability.RequiresGateway,
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "browser-diagnostics",
            "Browser Diagnostics",
            "Check browser access settings and gateway auth hints.",
            "tools",
            "🌐",
            typeof(BrowserDiagnosticsInput),
            typeof(BrowserDiagnosticsSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "gateway-browser-diagnostics",
            "Gateway Browser Diagnostics",
            "Check browser access settings and gateway auth hints.",
            "tools",
            "🌐",
            typeof(BrowserDiagnosticsInput),
            typeof(BrowserDiagnosticsSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "backup-schedule-create",
            "Schedule Backup",
            "Create or update a scheduled backup.",
            "tools",
            "⏱️",
            typeof(BackupScheduleInput),
            typeof(BackupScheduleSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "backup-schedule-list",
            "List Backup Schedules",
            "List scheduled backups.",
            "tools",
            "📅",
            typeof(BackupScheduleListInput),
            typeof(BackupScheduleSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "backup-schedule-remove",
            "Remove Backup Schedule",
            "Remove a scheduled backup.",
            "tools",
            "🧹",
            typeof(BackupScheduleRemoveInput),
            typeof(BackupScheduleSummary),
            capabilities: ActionCapability.Destructive,
            confirmPhrase: "REMOVE",
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "backup-export",
            "Backup Export",
            "Export a scoped backup archive.",
            "tools",
            "📤",
            typeof(BackupExportInput),
            typeof(BackupExportSummary),
            optionalPassword: true,
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "backup-prune",
            "Backup Prune",
            "Preview or apply backup pruning.",
            "tools",
            "🧹",
            typeof(BackupPruneInput),
            typeof(EmptyOutput),
            capabilities: ActionCapability.Destructive,
            confirmPhrase: "PRUNE",
            tags: new[] { "supports-preview", "supports-json" }));

        list.Add(InternalAction(
            "doctor",
            "Doctor",
            "Run openclaw doctor with safety logging.",
            "easy",
            "🩺",
            typeof(DoctorInput),
            typeof(DoctorSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "fix",
            "Fix",
            "Run openclaw doctor --fix with snapshot protection.",
            "tools",
            "🧰",
            typeof(FixInput),
            typeof(FixSummary),
            optionalPassword: true,
            tags: new[] { "supports-json" }));

        list.AddRange(OpenClawActions());

        list.Add(InternalAction(
            "oc-fix-missing-plugins",
            "OC Fix Missing Plugins",
            "Remove missing plugin paths from OpenClaw config.",
            "tools",
            "🧩",
            typeof(EmptyInput),
            typeof(EmptyOutput)));

        list.Add(InternalAction(
            "oc-gateway-disable-autostart",
            "Disable Gateway Autostart",
            "Remove gateway login task/startup (fixes flashing console).",
            "danger",
            "🚫",
            typeof(EmptyInput),
            typeof(EmptyOutput),
            capabilities: ActionCapability.Destructive | ActionCapability.RequiresElevation,
            confirmPhrase: "DISABLE"));

        list.Add(InternalAction(
            "reset",
            "Wipe Local Data",
            "Delete local OpenClaw data and clone on this computer.",
            "danger",
            "🧹",
            typeof(ResetInput),
            typeof(ResetSummary),
            capabilities: ActionCapability.Destructive,
            confirmPhrase: "yes"));

        list.Add(InternalAction(
            "nuke",
            "Nuke Local Data",
            "Remove all local OpenClaw data without creating a backup.",
            "danger",
            "💥",
            typeof(EmptyInput),
            typeof(EmptyOutput),
            capabilities: ActionCapability.Destructive,
            confirmPhrase: "yes"));

        list.Add(InternalAction(
            "recover",
            "Fix & Recover",
            "Reclone, rebuild, restore, and restart gateway.",
            "easy",
            "🛠️",
            typeof(RecoverInput),
            typeof(RecoverSummary),
            optionalPassword: true));

        list.Add(InternalAction(
            "diagnostics-export",
            "Diagnostics Export",
            "Build a diagnostics bundle for support.",
            "tools",
            "🧪",
            typeof(DiagnosticsExportInput),
            typeof(DiagnosticsBundleSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "gateway-repair",
            "Gateway Repair",
            "Diagnose and repair the OpenClaw gateway.",
            "easy",
            "🛠️",
            typeof(EmptyInput),
            typeof(GatewayRepairSummary),
            tags: new[] { "supports-json" }));

        list.Add(InternalAction(
            "openclaw-rebuild",
            "Rebuild OpenClaw",
            "Rebuild OpenClaw from a verified backup with optional preservation.",
            "danger",
            "🧱",
            typeof(OpenClawRebuildInput),
            typeof(OpenClawRebuildSummary),
            capabilities: ActionCapability.Destructive,
            confirmPhrase: "REBUILD",
            optionalPassword: true,
            tags: new[] { "supports-preview", "supports-json" }));

        list.Add(InternalAction(
            "fresh-install",
            "Fresh Install (No Restore)",
            "Clone and rebuild OpenClaw without restoring a backup.",
            "tools",
            "🧼",
            typeof(EmptyInput),
            typeof(EmptyOutput)));

        list.Add(InternalAction(
            "clone-openclaw",
            "Clone OpenClaw Repo",
            "Clone the OpenClaw source repo to the default location.",
            "tools",
            "📥",
            typeof(EmptyInput),
            typeof(EmptyOutput)));

        list.Add(InternalAction(
            "drill",
            "Full Recovery Test",
            "Run full backup, wipe, recover, and verify flow.",
            "danger",
            "🧪",
            typeof(BackupCreateInput),
            typeof(EmptyOutput),
            capabilities: ActionCapability.Destructive | ActionCapability.RequiresPassword,
            confirmPhrase: "DRILL"));

        return list;
    }

    private static IEnumerable<ActionDescriptor> OpenClawActions()
    {
        var list = new List<ActionDescriptor>
        {
            InternalAction("reclaw-backup-list", "ReClaw Backup List", "Run reclaw backup list.", "tools", "🗂️", typeof(BackupListInput), typeof(EmptyOutput)),
            InternalAction("reclaw-backup-prune-plan", "ReClaw Backup Prune Plan", "Run reclaw backup prune dry-run policy.", "tools", "🧪", typeof(BackupPruneInput), typeof(EmptyOutput)),
            InternalAction("reclaw-backup-export", "ReClaw Backup Export", "Run reclaw backup export scoped archive.", "tools", "📤", typeof(BackupExportInput), typeof(BackupExportSummary), optionalPassword: true),
            InternalAction("reclaw-backup-verify", "ReClaw Backup Verify", "Run reclaw backup verify.", "tools", "🔍", typeof(BackupVerifyInput), typeof(BackupVerificationSummary), optionalPassword: true),
            OpenClaw("oc-backup-create", "OC Backup Create", "🧰", "Run openclaw backup create.", "tools", new[] { "backup", "create" }),
            OpenClaw("oc-backup-create-verify", "OC Backup Verify Create", "✅", "Run openclaw backup create --verify.", "tools", new[] { "backup", "create", "--verify" }),
            OpenClaw("oc-backup-create-plan", "OC Backup Plan", "🧪", "Run openclaw backup create --dry-run --json.", "tools", new[] { "backup", "create", "--dry-run", "--json" }),
            OpenClaw("oc-backup-create-only-config", "OC Backup Config Only", "🧾", "Run openclaw backup create --only-config.", "tools", new[] { "backup", "create", "--only-config" }),
            OpenClaw("oc-backup-create-no-workspace", "OC Backup No Workspace", "📁", "Run openclaw backup create --no-include-workspace.", "tools", new[] { "backup", "create", "--no-include-workspace" }),
            OpenClaw("oc-backup-verify", "OC Backup Verify", "🔍", "Run openclaw backup verify.", "tools", new[] { "backup", "verify" }),
            OpenClaw("oc-reset-safe", "OC Reset Safe", "🧹", "Run openclaw reset safe scope non-interactive.", "danger", new[] { "reset", "--scope", "config+creds+sessions", "--yes", "--non-interactive" }, ActionCapability.Destructive, "RESET"),
            OpenClaw("oc-reset-dry-run", "OC Reset Dry Run", "🧪", "Run openclaw reset --dry-run.", "tools", new[] { "reset", "--dry-run" }),
            OpenClaw("oc-doctor", "OC Doctor", "🩺", "Run openclaw doctor --non-interactive --yes.", "easy", new[] { "doctor", "--non-interactive", "--yes" }),
            InternalAction("oc-update-pull", "OC Update Pull", "Pull latest OpenClaw source with git pull --ff-only.", "tools", "⬇️", typeof(EmptyInput), typeof(EmptyOutput)),
            OpenClaw("oc-doctor-repair", "OC Doctor Repair", "🛠️", "Run openclaw doctor --repair --non-interactive --yes.", "tools", new[] { "doctor", "--repair", "--non-interactive", "--yes" }),
            OpenClaw("oc-doctor-repair-force", "OC Doctor Force", "⚙️", "Run openclaw doctor --repair --force --non-interactive --yes.", "tools", new[] { "doctor", "--repair", "--force", "--non-interactive", "--yes" }),
            OpenClaw("oc-doctor-non-interactive", "OC Doctor NonInteractive", "🤖", "Run openclaw doctor --non-interactive --yes.", "tools", new[] { "doctor", "--non-interactive", "--yes" }),
            OpenClaw("oc-doctor-deep", "OC Doctor Deep", "🧬", "Run openclaw doctor --deep --non-interactive --yes.", "tools", new[] { "doctor", "--deep", "--non-interactive", "--yes" }),
            OpenClaw("oc-doctor-yes", "OC Doctor Yes", "👍", "Run openclaw doctor --yes --non-interactive.", "tools", new[] { "doctor", "--yes", "--non-interactive" }),
            OpenClaw("oc-doctor-token", "OC Doctor Token", "🔐", "Run openclaw doctor --generate-gateway-token --non-interactive --yes.", "tools", new[] { "doctor", "--generate-gateway-token", "--non-interactive", "--yes" }),
            OpenClaw("oc-doctor-fix", "OC Doctor Fix", "🧰", "Run openclaw doctor --fix --non-interactive --yes.", "tools", new[] { "doctor", "--fix", "--non-interactive", "--yes" }),
            OpenClaw("oc-security-audit", "OC Security Audit", "🛡️", "Run openclaw security audit.", "easy", new[] { "security", "audit" }),
            OpenClaw("oc-security-deep", "OC Security Deep", "🔒", "Run openclaw security audit --deep.", "tools", new[] { "security", "audit", "--deep" }),
            OpenClaw("oc-security-fix", "OC Security Fix", "🧯", "Run openclaw security audit --fix.", "tools", new[] { "security", "audit", "--fix" }),
            OpenClaw("oc-security-json", "OC Security JSON", "📋", "Run openclaw security audit --json.", "tools", new[] { "security", "audit", "--json" }),
            OpenClaw("oc-secrets-reload", "OC Secrets Reload", "🔄", "Run openclaw secrets reload.", "tools", new[] { "secrets", "reload" }),
            OpenClaw("oc-secrets-audit", "OC Secrets Audit", "🔍", "Run openclaw secrets audit.", "tools", new[] { "secrets", "audit" }),
            OpenClaw("oc-status", "OC Status", "📊", "Run openclaw status.", "easy", new[] { "status" }),
            OpenClaw("oc-status-deep", "OC Status Deep", "📈", "Run openclaw status --deep.", "tools", new[] { "status", "--deep" }),
            OpenClaw("oc-status-all", "OC Status All", "🧩", "Run openclaw status --all.", "tools", new[] { "status", "--all" }),
            OpenClaw("oc-status-usage", "OC Status Usage", "📐", "Run openclaw status --usage.", "tools", new[] { "status", "--usage" }),
            OpenClaw("oc-health", "OC Health", "💓", "Run openclaw health.", "easy", new[] { "health" }),
            OpenClaw("oc-health-json", "OC Health JSON", "🧷", "Run openclaw health --json.", "tools", new[] { "health", "--json" }),
            OpenClaw("oc-channels-status", "OC Channels Status", "📡", "Run openclaw channels status.", "tools", new[] { "channels", "status" }),
            OpenClaw("oc-channels-probe", "OC Channels Probe", "📶", "Run openclaw channels status --probe.", "tools", new[] { "channels", "status", "--probe" }),
            OpenClaw("oc-models-status", "OC Models Status", "🧠", "Run openclaw models status.", "tools", new[] { "models", "status" }),
            OpenClaw("oc-models-probe", "OC Models Probe", "🛰️", "Run openclaw models status --probe.", "tools", new[] { "models", "status", "--probe" }),
            OpenClaw("oc-gateway-start", "OC Gateway Start", "▶️", "Run openclaw gateway start.", "easy", new[] { "gateway", "start" }),
            InternalAction("oc-gateway-run", "OC Gateway Run (No Autostart)", "Run openclaw gateway run --port 18789 without registering a login task.", "easy", "🚀", typeof(EmptyInput), typeof(EmptyOutput)),
            OpenClaw("oc-gateway-stop", "OC Gateway Stop", "⏹️", "Run openclaw gateway stop.", "danger", new[] { "gateway", "stop" }, ActionCapability.Destructive, "STOP"),
            OpenClaw("oc-gateway-status", "OC Gateway Status", "📍", "Run openclaw gateway status.", "easy", new[] { "gateway", "status" }),
            OpenClaw("oc-gateway-status-deep", "OC Gateway Deep", "🧪", "Run openclaw gateway status --deep.", "tools", new[] { "gateway", "status", "--deep" }),
            OpenClaw("oc-gateway-restart", "OC Gateway Restart", "🔁", "Run openclaw gateway restart.", "tools", new[] { "gateway", "restart" }),
            OpenClaw("oc-gateway-install", "OC Gateway Install", "📥", "Run openclaw gateway install.", "tools", new[] { "gateway", "install" }),
            InternalAction("oc-gateway-install-start", "OC Gateway Install + Start", "Force install then start gateway (18789).", "easy", "⚡", typeof(EmptyInput), typeof(EmptyOutput)),
            InternalAction("oc-gateway-kill", "Kill Gateway Processes", "Force-kill processes using gateway (port 18789).", "danger", "🛑", typeof(EmptyInput), typeof(EmptyOutput), capabilities: ActionCapability.Destructive | ActionCapability.RequiresElevation, confirmPhrase: "KILL"),
            InternalAction("install-openclaw-cli", "Install OpenClaw CLI", "Run npm install -g openclaw@latest.", "tools", "⬇️", typeof(EmptyInput), typeof(EmptyOutput), capabilities: ActionCapability.RequiresElevation),
            OpenClaw("oc-gateway-uninstall", "OC Gateway Uninstall", "🗑️", "Run openclaw gateway uninstall.", "danger", new[] { "gateway", "uninstall" }, ActionCapability.Destructive, "UNINSTALL"),
            OpenClaw("oc-logs-follow", "OC Logs Follow", "📝", "Run openclaw logs --follow (Stop to end).", "tools", new[] { "logs", "--follow" }, ActionCapability.Cancellable),
            OpenClaw("oc-setup", "OC Setup", "🧱", "Run openclaw setup.", "tools", new[] { "setup" })
        };

        return list;
    }

    private static ActionDescriptor OpenClaw(
        string id,
        string label,
        string emoji,
        string description,
        string group,
        string[] commandArgs,
        ActionCapability capabilities = ActionCapability.None,
        string? confirmPhrase = null)
    {
        return new ActionDescriptor(
            id,
            label,
            description,
            group,
            emoji,
            ExecutionMode.OpenClawPassthrough,
            capabilities,
            typeof(EmptyInput),
            typeof(OpenClawCommandSummary),
            commandArgs,
            confirmPhrase);
    }

    private static ActionDescriptor InternalAction(
        string id,
        string label,
        string description,
        string group,
        string emoji,
        Type inputType,
        Type outputType,
        ActionCapability capabilities = ActionCapability.None,
        string? confirmPhrase = null,
        bool optionalPassword = false,
        bool requiresArchive = false,
        string[]? tags = null)
    {
        return new ActionDescriptor(
            id,
            label,
            description,
            group,
            emoji,
            ExecutionMode.Internal,
            capabilities,
            inputType,
            outputType,
            null,
            confirmPhrase,
            optionalPassword,
            requiresArchive,
            tags);
    }
}
