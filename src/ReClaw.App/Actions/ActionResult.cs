namespace ReClaw.App.Actions;

public sealed record ActionResult(
    bool Success,
    object? Output = null,
    string? Error = null,
    int? ExitCode = null,
    IReadOnlyList<WarningItem>? Warnings = null
);
