using System.Collections.Generic;

namespace ReClaw.Core;

public sealed record RestoreAssetImpact(
    string Kind,
    string ArchivePath,
    string DestinationPath,
    bool Exists,
    int PayloadEntries,
    int OverwriteEntries);

public sealed record RestorePreview(
    string ArchivePath,
    string ArchiveType,
    BackupArchiveKind ArchiveKind,
    string DestinationPath,
    string Scope,
    int TotalPayloadEntries,
    int RestorePayloadEntries,
    int OverwritePayloadEntries,
    string? CreatedAt,
    int? SchemaVersion,
    IReadOnlyList<RestoreAssetImpact> Assets);
