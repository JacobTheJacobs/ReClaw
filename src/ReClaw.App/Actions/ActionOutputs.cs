using ReClaw.App.Execution;
using ReClaw.Core;

namespace ReClaw.App.Actions;

public sealed record BackupExportSummary(
    string ArchivePath,
    string Scope,
    bool Verified,
    string? EncryptedArchivePath = null,
    string? RawArchivePath = null);

public sealed record OpenClawBackupAsset(
    string Kind,
    string SourcePath,
    string DisplayPath,
    string ArchivePath);

public sealed record OpenClawBackupSkipped(
    string Kind,
    string SourcePath,
    string DisplayPath,
    string Reason,
    string? CoveredBy);

public sealed record OpenClawBackupCreateSummary(
    string CreatedAt,
    string ArchiveRoot,
    string ArchivePath,
    bool DryRun,
    bool IncludeWorkspace,
    bool OnlyConfig,
    bool Verified,
    IReadOnlyList<OpenClawBackupAsset> Assets,
    IReadOnlyList<OpenClawBackupSkipped> Skipped);

public sealed record OpenClawBackupVerifySummary(
    string ArchivePath,
    string ArchiveRoot,
    string CreatedAt,
    string RuntimeVersion,
    int AssetCount,
    int EntryCount);

public sealed record BackupDiffSummary(
    string LeftArchive,
    string RightArchive,
    IReadOnlyList<string> AddedAssets,
    IReadOnlyList<string> RemovedAssets,
    IReadOnlyList<string> ChangedAssets,
    IReadOnlyList<string> ConfigDiff,
    IReadOnlyList<string> WorkspaceAdded,
    IReadOnlyList<string> WorkspaceRemoved,
    IReadOnlyList<string> CredentialChanges,
    string? RedactedNote,
    int RedactedCount = 0,
    bool ConfigChanged = false,
    bool WorkspaceChanged = false,
    bool CredentialChangesPresent = false);

public sealed record BackupScheduleSummary(
    IReadOnlyList<BackupSchedule> Schedules,
    bool Applied,
    string? Message,
    string? NextRun = null);

public sealed record GatewayTokenSummary(
    string? TokenMasked,
    bool Revealed,
    string? SourcePath,
    bool TokenPresent = true,
    string? TokenSource = null,
    IReadOnlyList<string>? Warnings = null);

public sealed record GatewayTroubleshootSummary(
    OpenClawCommandSummary? Status,
    OpenClawCommandSummary? GatewayStatus,
    OpenClawCommandSummary? Logs,
    OpenClawCommandSummary? Doctor,
    OpenClawCommandSummary? ChannelsProbe,
    string? StartupReason,
    bool GatewayHealthy);

public sealed record OpenClawCommandSummary(
    string Command,
    int ExitCode,
    bool TimedOut,
    IReadOnlyList<string> StdOut,
    IReadOnlyList<string> StdErr,
    int StdOutLineCount,
    int StdErrLineCount,
    bool OutputTruncated);

public sealed record BrowserDiagnosticsSummary(
    string? LocalUrl,
    string? DashboardUrl,
    bool AuthRequired,
    bool TokenPresent,
    bool AllowedOriginsValid,
    bool SecureContextWarning,
    bool RemoteSafe,
    bool RemoteUnsafe,
    IReadOnlyList<string> Warnings,
    OpenClawCommandSummary? StatusCommand = null);

public sealed record SetupSecurityCheck(
    string Name,
    int ExitCode,
    bool Success,
    string? Detail = null);

public sealed record SetupSecuritySummary(
    string Status,
    IReadOnlyList<SetupSecurityCheck> Checks);

public sealed record SetupStepSummary(
    string Step,
    string Status,
    string? Detail = null);

public sealed record SetupAssistantSummary(
    string Summary,
    string InstallRuntime,
    bool GatewayHealthy,
    bool RepairAttempted,
    bool DetachedFallback,
    SetupSecuritySummary Security,
    IReadOnlyList<SetupStepSummary> Steps,
    string? DashboardUrl = null);

public sealed record OpenClawDetectionSummary(
    string? ExecutablePath,
    string? WorkingDirectory,
    string? ConfigPath,
    string? RuntimeVersion,
    string? ConfigVersion,
    bool GatewayServiceExists,
    bool GatewayActive,
    IReadOnlyList<OpenClawCandidateSummary> Candidates);

public sealed record OpenClawCandidateSummary(
    string Source,
    string? ExecutablePath,
    string? WorkingDirectory,
    string? Version);

public sealed record GatewayRepairStep(
    string Step,
    string Status,
    string? Detail,
    OpenClawCommandSummary? Command);

public sealed record GatewayRepairAttempt(
    string StepId,
    string Title,
    bool Succeeded,
    bool MutatedState,
    string Summary,
    string? CommandLine);

public sealed record GatewayRepairSummary(
    string Action,
    string Outcome,
    OpenClawDetectionSummary Detection,
    IReadOnlyList<GatewayRepairStep> Steps,
    IReadOnlyList<GatewayRepairAttempt> Attempts,
    OpenClawCommandSummary? FinalCommand,
    string? SnapshotPath,
    string? SelectedRuntime,
    IReadOnlyList<string> SuggestedActions,
    IReadOnlyList<string> Notes,
    OpenClawInventory? Inventory,
    string? JournalPath = null) : IJournalCarrier;

public sealed record RebuildStep(
    string StepId,
    string Title,
    string Status,
    string? Command,
    string? Detail);

public sealed record CommandOutputSummary(
    string Command,
    int ExitCode,
    IReadOnlyList<string> StdOut,
    IReadOnlyList<string> StdErr);

public sealed record RebuildGatewayDiagnostics(
    string? ServiceStatus,
    string? ServiceEntrypoint,
    bool ServiceExists,
    bool ServiceActive,
    OpenClawCommandSummary? GatewayStatus,
    OpenClawCommandSummary? LogsStatus,
    BrowserDiagnosticsSummary? BrowserDiagnostics,
    CommandOutputSummary? ServiceTaskStatus,
    string? Remediation);

public sealed record RebuildVerificationSummary(
    OpenClawCommandSummary? GatewayStatus,
    OpenClawCommandSummary? DashboardStatus,
    OpenClawCommandSummary? LogsStatus,
    BrowserDiagnosticsSummary? BrowserDiagnostics,
    string? GatewayUrl = null,
    bool GatewayHealthy = false,
    bool LogsAvailable = false,
    bool BrowserReady = false,
    IReadOnlyList<string>? VerificationWarnings = null,
    IReadOnlyList<string>? VerificationFailures = null,
    RebuildGatewayDiagnostics? GatewayDiagnostics = null);

public sealed record OpenClawRebuildSummary(
    string BackupPath,
    string Scope,
    IReadOnlyList<string> PreserveScopes,
    ResetMode ResetMode,
    string RuntimeStrategy,
    IReadOnlyList<string> RemovedItems,
    IReadOnlyList<string> InstalledItems,
    IReadOnlyList<RebuildStep> Steps,
    RebuildVerificationSummary Verification,
    OpenClawInventory? Inventory,
    string? JournalPath = null) : IJournalCarrier;

public sealed record RestoreSummary(
    string ArchivePath,
    string DestinationPath,
    string Scope,
    bool Applied,
    RestorePreview? Preview,
    string? SnapshotPath,
    ResetMode? ResetMode,
    ResetPlan? ResetPlan,
    string? JournalPath = null) : IJournalCarrier;

public interface IDiagnosticsBundleCarrier
{
    string? DiagnosticsBundlePath { get; }
}

public interface IJournalCarrier
{
    string? JournalPath { get; }
}

public sealed record DoctorSummary(
    OpenClawCommandSummary Command,
    string? DiagnosticsBundlePath,
    string? JournalPath = null) : IDiagnosticsBundleCarrier, IJournalCarrier;

public sealed record FixSummary(
    OpenClawCommandSummary Command,
    string? SnapshotPath,
    string? DiagnosticsBundlePath,
    string? JournalPath = null) : IDiagnosticsBundleCarrier, IJournalCarrier;

public sealed record RecoverSummary(
    string? ArchivePath,
    string? DestinationPath,
    string? Scope,
    bool Applied,
    RestoreSummary? Restore,
    DoctorSummary? Doctor,
    FixSummary? Fix,
    string? DiagnosticsBundlePath,
    IReadOnlyList<RecoveryStep> Steps,
    string? NextEscalation,
    string? JournalPath = null) : IDiagnosticsBundleCarrier, IJournalCarrier;

public sealed record RecoveryStep(
    string Step,
    string Status,
    string? Error,
    string? Next);

public sealed record DiagnosticsBundleSummary(
    string BundlePath,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> IncludedFiles,
    IReadOnlyList<string> SkippedFiles,
    string? JournalPath = null) : IDiagnosticsBundleCarrier, IJournalCarrier
{
    public string? DiagnosticsBundlePath => BundlePath;
}

public sealed record RollbackSummary(
    string SnapshotPath,
    string DestinationPath,
    string Scope,
    bool Applied,
    RestorePreview Preview,
    string? JournalPath = null) : IJournalCarrier;

public sealed record ResetSummary(
    ResetMode Mode,
    ResetPlan Plan,
    bool Applied,
    string? JournalPath = null) : IJournalCarrier;

public sealed record StatusSummary(
    string OpenClawHome,
    string ConfigDirectory,
    string DataDirectory,
    string BackupDirectory,
    string LogsDirectory,
    string TempDirectory,
    string? OpenClawExecutable,
    string? OpenClawEntry,
    int BackupCount,
    bool OpenClawHomeExists,
    bool ConfigDirectoryExists,
    bool DataDirectoryExists,
    bool BackupDirectoryExists,
    string? JournalPath = null) : IJournalCarrier;

public sealed record OpenClawCleanupSummary(
    IReadOnlyList<OpenClawArtifactInfo> Candidates,
    IReadOnlyList<string> Removed,
    bool Applied,
    string? JournalPath = null) : IJournalCarrier;
