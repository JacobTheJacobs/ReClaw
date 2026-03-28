using System.Collections.Generic;

namespace ReClaw.App.Execution;

public sealed record OpenClawInventory(
    OpenClawRuntimeInfo? ActiveRuntime,
    IReadOnlyList<OpenClawRuntimeInfo> CandidateRuntimes,
    OpenClawConfigInfo? Config,
    OpenClawGatewayInfo Gateway,
    IReadOnlyList<OpenClawServiceInfo> Services,
    IReadOnlyList<OpenClawArtifactInfo> Artifacts,
    IReadOnlyList<OpenClawWarning> Warnings
);

public sealed record OpenClawRuntimeInfo(
    string Kind,
    string ExecutablePath,
    string? WorkingDirectory,
    string? Version,
    bool IsSelected,
    int Score
);

public sealed record OpenClawConfigInfo(
    string ConfigPath,
    string? StateDir,
    string? WorkspaceDir,
    string? LogPath,
    string? WrittenByVersion,
    bool Exists
);

public sealed record OpenClawGatewayInfo(
    bool IsRunning,
    bool IsReachable,
    bool CanTailLogs,
    string? StatusSummary,
    string? RpcSummary
);

public sealed record OpenClawServiceInfo(
    string PlatformKind,
    string Name,
    string? Entrypoint,
    bool Exists,
    bool IsActive,
    bool IsLegacy,
    bool IsMismatched
);

public sealed record OpenClawArtifactInfo(
    string Kind,
    string Path,
    bool IsSafeToClean,
    string Summary
);

public sealed record OpenClawWarning(
    string Code,
    string Message,
    string? Remediation
);
