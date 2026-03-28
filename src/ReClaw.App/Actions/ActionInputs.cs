using ReClaw.Core;
using ReClaw.App.Execution;

namespace ReClaw.App.Actions;

public sealed record BackupCreateInput(
    string? SourcePath = null,
    string? OutputPath = null,
    string? Password = null,
    bool Verify = false,
    string? Scope = null,
    string? BackupPath = null,
    bool NoEncrypt = false);

public sealed record BackupRestoreInput(
    string? ArchivePath = null,
    string? DestinationPath = null,
    string? Password = null,
    string? Scope = null,
    bool Preview = false,
    bool SafeReset = false,
    ResetMode ResetMode = ResetMode.PreserveBackups,
    bool ConfirmReset = false,
    bool VerifyFirst = true);

public sealed record BackupVerifyInput(string? ArchivePath = null, string? Password = null);

public sealed record BackupListInput;

public sealed record BackupPruneInput(int KeepLast = 5, string? OlderThan = "30d", bool DryRun = true);

public sealed record BackupExportInput(
    string Scope = "config+creds+sessions",
    bool Verify = true,
    string? OutputPath = null,
    string? Password = null,
    bool Encrypt = true);

public sealed record BackupDiffInput(
    string? LeftArchivePath = null,
    string? RightArchivePath = null,
    bool RedactSecrets = true,
    string? Password = null);

public sealed record BackupScheduleInput(
    BackupScheduleMode Mode = BackupScheduleMode.Gateway,
    BackupScheduleKind Kind = BackupScheduleKind.Daily,
    string? Expression = null,
    string? AtTime = null,
    string? DayOfWeek = null,
    int? DayOfMonth = null,
    int RetentionKeepLast = 7,
    string RetentionOlderThan = "30d",
    bool VerifyAfter = true,
    bool IncludeWorkspace = true,
    bool OnlyConfig = false);

public sealed record BackupScheduleRemoveInput(string? ScheduleId = null);

public sealed record BackupScheduleListInput;

public sealed record DoctorInput(
    bool Repair = false,
    bool Force = false,
    bool Deep = false,
    bool NonInteractive = true,
    bool Yes = true,
    bool GenerateToken = false,
    bool ExportDiagnostics = false,
    string? DiagnosticsOutputPath = null);

public sealed record FixInput(
    string? Password = null,
    bool NonInteractive = true,
    bool Yes = true,
    bool Force = false,
    string? SnapshotPath = null,
    bool ExportDiagnostics = false,
    string? DiagnosticsOutputPath = null);

public sealed record RecoverInput(
    string? ArchivePath = null,
    string? DestinationPath = null,
    string? Password = null,
    string? Scope = null,
    bool Preview = false,
    bool SafeReset = true,
    ResetMode ResetMode = ResetMode.PreserveBackups,
    bool ConfirmReset = false,
    bool RunDoctor = true,
    bool RunFix = true,
    bool ExportDiagnostics = true,
    string? DiagnosticsOutputPath = null);

public sealed record RollbackInput(
    string? SnapshotPath = null,
    string? DestinationPath = null,
    string? Password = null,
    string? Scope = null,
    bool Preview = false,
    bool ConfirmRollback = false);

public sealed record ResetInput(
    ResetMode Mode = ResetMode.PreserveBackups,
    bool Preview = false,
    bool Confirm = false);

public sealed record StatusInput;

public sealed record DiagnosticsExportInput(string? OutputPath = null);

public sealed record DashboardOpenInput;

public sealed record GatewayTokenInput(bool Reveal = false);

public enum BrowserAccessMode
{
    Local = 0,
    Remote = 1
}

public sealed record BrowserDiagnosticsInput(
    BrowserAccessMode Mode = BrowserAccessMode.Local,
    string? RuntimeOverride = null,
    string? ConfigOverride = null);

public sealed record GatewayUrlInput(
    BrowserAccessMode Mode = BrowserAccessMode.Local,
    string? RuntimeOverride = null,
    string? ConfigOverride = null);

public sealed record OpenClawCleanupInput(
    bool Apply = false,
    bool Confirm = false);

public enum AiProvider
{
    None = 0,
    DeepSeek,
    Anthropic,
    OpenAI,
    Bailian,
    BailianCodingPlan
}

[Flags]
public enum UseCase
{
    None = 0,
    DailyProductivity = 1,
    InformationTracker = 2,
    EfficiencyTools = 4,
    StockMarket = 8
}

public enum ImPlatform
{
    None = 0,
    DingTalk,
    Feishu,
    QQ,
    Discord
}

public sealed record SetupWizardInput(
    AiProvider Provider = AiProvider.None,
    string? ApiKey = null,
    bool SkipAiSetup = false,
    UseCase UseCases = UseCase.None,
    ImPlatform ImPlatform = ImPlatform.None);

public sealed record OpenClawRebuildInput(
    bool PreserveConfig = true,
    bool PreserveCredentials = true,
    bool PreserveSessions = true,
    bool PreserveWorkspace = true,
    bool CleanInstall = false,
    bool ConfirmDestructive = false,
    string? Password = null);
