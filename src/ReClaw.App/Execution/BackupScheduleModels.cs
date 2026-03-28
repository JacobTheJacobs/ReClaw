using System;

namespace ReClaw.App.Execution;

public enum BackupScheduleMode
{
    Gateway = 0,
    OsNative = 1
}

public enum BackupScheduleKind
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
    Cron = 3
}

public sealed record BackupSchedule(
    string Id,
    BackupScheduleMode Mode,
    BackupScheduleKind Kind,
    string Expression,
    int RetentionKeepLast,
    string RetentionOlderThan,
    bool VerifyAfter,
    bool IncludeWorkspace,
    bool OnlyConfig,
    string Command,
    DateTimeOffset UpdatedAt,
    string? ProviderId = null,
    string? Notes = null);
