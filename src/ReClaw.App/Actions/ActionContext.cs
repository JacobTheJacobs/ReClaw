namespace ReClaw.App.Actions;

public sealed record ActionContext(
    string ConfigDirectory,
    string DataDirectory,
    string BackupDirectory,
    string LogsDirectory,
    string TempDirectory,
    string OpenClawHome,
    string? OpenClawExecutable,
    string? OpenClawEntry
);
