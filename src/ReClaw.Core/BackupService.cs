using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.Core.IO;

namespace ReClaw.Core;

public interface IBackupService
{
    Task CreateBackupAsync(string sourcePath, string outputPath, string? password = null, string? scope = null);
    Task<BackupVerificationSummary> VerifySnapshotAsync(string snapshotPath, string? password = null);
    Task<RestorePreview> PreviewRestoreAsync(string snapshotPath, string destinationPath, string? password = null, string? scope = null, bool skipVerify = false);
    Task RestoreAsync(string snapshotPath, string destinationPath, string? password = null, string? scope = null);
}

public sealed record BackupVerificationSummary(
    string ArchivePath,
    string ArchiveType,
    int EntryCount,
    int AssetCount,
    int PayloadEntryCount,
    string? CreatedAt,
    int? SchemaVersion);

public class BackupService : IBackupService
{
    private readonly IFileFaultInjector faultInjector;

    public BackupService()
        : this(null)
    {
    }

    internal BackupService(IFileFaultInjector? faultInjector)
    {
        this.faultInjector = faultInjector ?? new NullFileFaultInjector();
    }

    public bool IsFaultInjected => faultInjector is not NullFileFaultInjector;

    public ResetService CreateResetService()
    {
        return new ResetService(faultInjector);
    }
    private static readonly byte[] EncryptionMagic = Encoding.ASCII.GetBytes("RCLAWENC1");
    private static readonly Regex Sha256Regex = new("^[a-f0-9]{64}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task CreateBackupAsync(string sourcePath, string outputPath, string? password = null, string? scope = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("sourcePath");
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("outputPath");
        if (!Directory.Exists(sourcePath)) throw new DirectoryNotFoundException(sourcePath);

        faultInjector.BeforeSnapshotCreate(outputPath);
        EnsureOutputOutsideSource(sourcePath, outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);

        var scopeInfo = BackupScope.Parse(scope, "full");
        var staging = StageBackup(sourcePath, scopeInfo);
        var tempFile = Path.GetTempFileName();
        var tempTarGz = Path.ChangeExtension(tempFile, ".tar.gz");
        File.Delete(tempFile);

        try
        {
            WriteManifest(staging.StageDirectory, staging.Assets);

            TarUtils.CreateTarGzDirectory(staging.StageDirectory, tempTarGz);

            if (!string.IsNullOrWhiteSpace(password))
            {
                CryptoHelpers.EncryptFileWithPassword(tempTarGz, outputPath, password);
            }
            else
            {
                File.Move(tempTarGz, outputPath, overwrite: true);
            }
        }
        finally
        {
            try { if (File.Exists(tempTarGz)) File.Delete(tempTarGz); } catch { }
            try { if (Directory.Exists(staging.StageDirectory)) Directory.Delete(staging.StageDirectory, recursive: true); } catch { }
        }

        await Task.CompletedTask;
    }

    public async Task<BackupVerificationSummary> VerifySnapshotAsync(string snapshotPath, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath)) throw new ArgumentException("snapshotPath");
        if (!File.Exists(snapshotPath)) throw new FileNotFoundException("Backup archive not found.", snapshotPath);

        var prepared = PrepareArchiveForRead(snapshotPath, password);
        try
        {
            var archiveType = prepared.ArchiveType;
            var inspection = archiveType == "zip"
                ? await InspectZipArchive(prepared.ArchivePath).ConfigureAwait(false)
                : await InspectTarArchive(prepared.ArchivePath).ConfigureAwait(false);

            if (inspection.NormalizedEntries.Count == 0)
            {
                throw new InvalidDataException("Backup archive is empty.");
            }

            ParsedManifest? manifest = null;
            OpenClawManifest? openClawManifest = null;
            if (!string.IsNullOrWhiteSpace(inspection.ManifestRaw))
            {
                manifest = ParseManifest(inspection.ManifestRaw!);
                openClawManifest = TryParseOpenClawManifest(inspection.ManifestRaw!);
            }

            if (manifest != null)
            {
                foreach (var asset in manifest.Assets)
                {
                    var hasExact = inspection.FileMetadata.ContainsKey(asset.ArchivePath);
                    var hasNested = inspection.NormalizedEntries.Any(entry =>
                        entry.Length > asset.ArchivePath.Length &&
                        entry.StartsWith(asset.ArchivePath, StringComparison.OrdinalIgnoreCase) &&
                        entry[asset.ArchivePath.Length] == '/');

                    if (!hasExact && !hasNested)
                    {
                        throw new InvalidDataException($"Backup archive is missing payload for manifest asset: {asset.ArchivePath}");
                    }
                }

                foreach (var payload in manifest.Payload)
                {
                    if (!inspection.FileMetadata.TryGetValue(payload.ArchivePath, out var metadata))
                    {
                        throw new InvalidDataException($"Backup archive is missing manifest payload entry: {payload.ArchivePath}");
                    }

                    if (metadata.IsDirectory)
                    {
                        throw new InvalidDataException($"Backup manifest payload points to a directory: {payload.ArchivePath}");
                    }

                    if (metadata.Size != payload.Size)
                    {
                        throw new InvalidDataException($"Backup manifest payload size mismatch for {payload.ArchivePath}: expected {payload.Size}, got {metadata.Size}");
                    }

                    if (!string.Equals(metadata.Sha256, payload.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Backup manifest payload hash mismatch for {payload.ArchivePath}: expected {payload.Sha256}, got {metadata.Sha256}");
                    }
                }
            }

            return new BackupVerificationSummary(
                prepared.OriginalPath,
                prepared.ArchiveType,
                inspection.NormalizedEntries.Count,
                manifest?.Assets.Count ?? 0,
                manifest?.Payload.Count ?? 0,
                manifest?.CreatedAt,
                manifest?.SchemaVersion);
        }
        finally
        {
            prepared.Cleanup();
        }
    }

    public async Task<RestorePreview> PreviewRestoreAsync(string snapshotPath, string destinationPath, string? password = null, string? scope = null, bool skipVerify = false)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath)) throw new ArgumentException("snapshotPath");
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("destinationPath");
        if (!File.Exists(snapshotPath)) throw new FileNotFoundException("Backup archive not found.", snapshotPath);

        if (!skipVerify)
        {
            await VerifySnapshotAsync(snapshotPath, password).ConfigureAwait(false);
        }

        var scopeInfo = BackupScope.Parse(scope, "full");
        var previewDestination = Path.GetFullPath(destinationPath);

        var prepared = PrepareArchiveForRead(snapshotPath, password);
        try
        {
            var archiveType = prepared.ArchiveType;
            var inspection = archiveType == "zip"
                ? await InspectZipArchive(prepared.ArchivePath).ConfigureAwait(false)
                : await InspectTarArchive(prepared.ArchivePath).ConfigureAwait(false);

            if (inspection.NormalizedEntries.Count == 0)
            {
                throw new InvalidDataException("Backup archive is empty.");
            }

            ParsedManifest? manifest = null;
            OpenClawManifest? openClawManifest = null;
            if (!string.IsNullOrWhiteSpace(inspection.ManifestRaw))
            {
                manifest = ParseManifest(inspection.ManifestRaw!);
                openClawManifest = TryParseOpenClawManifest(inspection.ManifestRaw!);
            }

            var assets = BuildRestoreAssets(inspection, scopeInfo, manifest, openClawManifest);
            if (assets.Count == 0)
            {
                throw new InvalidOperationException($"No matching entries found for scope '{scopeInfo.Raw}'.");
            }

            var payloadEntries = BuildPayloadEntries(manifest, inspection, openClawManifest);
            var restorePayloadEntries = openClawManifest is null
                ? payloadEntries.Where(entry => assets.Any(asset => string.Equals(asset.ArchivePath, GetRootSegment(entry), StringComparison.OrdinalIgnoreCase))).ToList()
                : payloadEntries.Where(entry => assets.Any(asset => IsUnderArchiveRoot(entry, asset.ArchivePath))).ToList();

            var payloadCountByRoot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var overwriteCountByRoot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var rootHasNestedEntries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var overwriteTotal = 0;

            foreach (var entry in restorePayloadEntries)
            {
                var root = openClawManifest is null ? GetRootSegment(entry) : ResolveAssetRoot(entry, assets);
                payloadCountByRoot[root] = payloadCountByRoot.TryGetValue(root, out var count) ? count + 1 : 1;
                if (entry.Contains('/'))
                {
                    rootHasNestedEntries[root] = true;
                }

                var destPath = openClawManifest is null
                    ? Path.Combine(previewDestination, entry.Replace('/', Path.DirectorySeparatorChar))
                    : MapOpenClawEntryToDestination(entry, assets, previewDestination, openClawManifest);
                if (File.Exists(destPath) || Directory.Exists(destPath))
                {
                    overwriteTotal++;
                    overwriteCountByRoot[root] = overwriteCountByRoot.TryGetValue(root, out var overwriteCount) ? overwriteCount + 1 : 1;
                }
            }

            foreach (var (root, hasNested) in rootHasNestedEntries)
            {
                if (!hasNested) continue;
                var destRootPath = openClawManifest is null
                    ? Path.Combine(previewDestination, root.Replace('/', Path.DirectorySeparatorChar))
                    : ResolveAssetDestinationRoot(root, assets, previewDestination, openClawManifest);
                if (!File.Exists(destRootPath)) continue;

                var desired = payloadCountByRoot.TryGetValue(root, out var payloadCount) ? payloadCount : 0;
                var existing = overwriteCountByRoot.TryGetValue(root, out var overwriteCount) ? overwriteCount : 0;
                if (desired > existing)
                {
                    overwriteCountByRoot[root] = desired;
                    overwriteTotal += desired - existing;
                }
            }

            var impact = new List<RestoreAssetImpact>();
            foreach (var asset in assets)
            {
                var destPath = openClawManifest is null
                    ? Path.Combine(previewDestination, asset.ArchivePath.Replace('/', Path.DirectorySeparatorChar))
                    : ResolveAssetDestinationRoot(asset.ArchivePath, assets, previewDestination, openClawManifest);
                var exists = File.Exists(destPath) || Directory.Exists(destPath);
                var payloadCount = payloadCountByRoot.TryGetValue(asset.ArchivePath, out var count) ? count : 0;
                var overwriteCount = overwriteCountByRoot.TryGetValue(asset.ArchivePath, out var overwrites) ? overwrites : 0;
                impact.Add(new RestoreAssetImpact(asset.Kind, asset.ArchivePath, destPath, exists, payloadCount, overwriteCount));
            }

            return new RestorePreview(
                prepared.OriginalPath,
                prepared.ArchiveType,
                ResolveArchiveKind(prepared, openClawManifest),
                previewDestination,
                scopeInfo.Raw,
                payloadEntries.Count,
                restorePayloadEntries.Count,
                overwriteTotal,
                manifest?.CreatedAt,
                manifest?.SchemaVersion,
                impact);
        }
        finally
        {
            prepared.Cleanup();
        }
    }

    public Task RestoreAsync(string snapshotPath, string destinationPath, string? password = null, string? scope = null)
    {
        return RestoreAsyncInternal(snapshotPath, destinationPath, password, scope, null);
    }

    public Task RestoreAsync(string snapshotPath, string destinationPath, string? password, string? scope, RestorePreview preview)
    {
        if (preview is null) throw new ArgumentNullException(nameof(preview));
        return RestoreAsyncInternal(snapshotPath, destinationPath, password, scope, preview);
    }

    private async Task RestoreAsyncInternal(string snapshotPath, string destinationPath, string? password, string? scope, RestorePreview? preview)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath)) throw new ArgumentException("snapshotPath");
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("destinationPath");

        var effectivePreview = preview ?? await PreviewRestoreAsync(snapshotPath, destinationPath, password, scope).ConfigureAwait(false);
        var scopeInfo = BackupScope.Parse(effectivePreview.Scope, "full");

        var prepared = PrepareArchiveForRead(snapshotPath, password);
        List<RestoreAsset> restoreAssets;
        OpenClawManifest? openClawManifest = null;
        try
        {
            var inspection = prepared.ArchiveType == "zip"
                ? await InspectZipArchive(prepared.ArchivePath).ConfigureAwait(false)
                : await InspectTarArchive(prepared.ArchivePath).ConfigureAwait(false);
            ParsedManifest? manifest = null;
            if (!string.IsNullOrWhiteSpace(inspection.ManifestRaw))
            {
                manifest = ParseManifest(inspection.ManifestRaw!);
                openClawManifest = TryParseOpenClawManifest(inspection.ManifestRaw!);
            }

            restoreAssets = BuildRestoreAssets(inspection, scopeInfo, manifest, openClawManifest);
        }
        catch
        {
            prepared.Cleanup();
            throw;
        }

        if (restoreAssets.Count == 0)
        {
            prepared.Cleanup();
            throw new InvalidOperationException($"No matching entries found for scope '{effectivePreview.Scope}'.");
        }
        var tempDir = Path.Combine(Path.GetTempPath(), $"reclaw_restore_{Guid.NewGuid():N}");
        var applyContext = new RestoreApplyContext();
        try
        {
            EnsureDirectory(destinationPath, applyContext);
            Directory.CreateDirectory(tempDir);

            if (prepared.ArchiveType == "zip")
            {
                using var fs = File.OpenRead(prepared.ArchivePath);
                await ZipHandler.ExtractAsync(fs, tempDir, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                TarUtils.ExtractTarGzToDirectory(prepared.ArchivePath, tempDir);
            }

            foreach (var asset in restoreAssets)
            {
                var sourcePath = Path.Combine(tempDir, asset.ArchivePath.Replace('/', Path.DirectorySeparatorChar));
                var targetPath = openClawManifest is null
                    ? Path.Combine(destinationPath, asset.ArchivePath.Replace('/', Path.DirectorySeparatorChar))
                    : ResolveAssetDestinationRoot(asset.ArchivePath, restoreAssets, destinationPath, openClawManifest);

                if (Directory.Exists(sourcePath))
                {
                    CopyDirectoryWithTracking(sourcePath, targetPath, applyContext);
                }
                else if (File.Exists(sourcePath))
                {
                    CopyFileWithTracking(sourcePath, targetPath, applyContext);
                }
            }
        }
        catch (Exception ex)
        {
            if (applyContext.HasChanges)
            {
                var cleanupSucceeded = applyContext.TryCleanup();
                throw new RestoreApplyException("Restore failed after applying changes.", ex, applyContext.PartialWrite, applyContext.OverwroteExisting, cleanupSucceeded);
            }

            throw;
        }
        finally
        {
            prepared.Cleanup();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void EnsureOutputOutsideSource(string sourcePath, string outputPath)
    {
        var sourceFull = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var outputFull = Path.GetFullPath(outputPath);

        if (outputFull.Equals(sourceFull, StringComparison.OrdinalIgnoreCase) ||
            outputFull.StartsWith(sourceFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Backup output must not be written inside a source path: {outputPath} is inside {sourcePath}");
        }
    }

    private static BackupStaging StageBackup(string sourcePath, BackupScopeInfo scopeInfo)
    {
        var stageDir = Path.Combine(Path.GetTempPath(), $"reclaw_stage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDir);

        var assets = new List<ManifestAsset>();

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourcePath))
        {
            var name = Path.GetFileName(entry);
            if (!BackupScope.ShouldIncludeTopLevelEntry(name, scopeInfo))
            {
                continue;
            }

            var destination = Path.Combine(stageDir, name);
            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, destination);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? stageDir);
                File.Copy(entry, destination, overwrite: true);
            }

            assets.Add(new ManifestAsset(
                BackupScope.CategorizeTopLevelEntry(name),
                entry,
                name.Replace(Path.DirectorySeparatorChar, '/')));
        }

        if (assets.Count == 0)
        {
            throw new InvalidOperationException($"No matching entries found for scope '{scopeInfo.Raw}'.");
        }

        return new BackupStaging(stageDir, assets);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destFile = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var destDir = Path.Combine(destination, Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }

    private void CopyDirectoryWithTracking(string source, string destination, RestoreApplyContext context)
    {
        EnsureDirectory(destination, context);

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var destDir = Path.Combine(destination, Path.GetFileName(dir));
            CopyDirectoryWithTracking(dir, destDir, context);
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destFile = Path.Combine(destination, Path.GetFileName(file));
            CopyFileWithTracking(file, destFile, context);
        }
    }

    private void CopyFileWithTracking(string sourcePath, string destinationPath, RestoreApplyContext context)
    {
        var targetDir = Path.GetDirectoryName(destinationPath) ?? destinationPath;
        EnsureDirectory(targetDir, context);

        var destinationFull = Path.GetFullPath(destinationPath);
        PathSafetyPolicy.EnsureTotalPathLength(destinationFull, "Restore destination path");
        context.TrackTarget(destinationFull);

        var existedBefore = File.Exists(destinationPath);

        faultInjector.BeforeCopyFile(sourcePath, destinationPath);
        using var inStream = File.OpenRead(sourcePath);
        using var outStream = File.Create(destinationPath);
        if (existedBefore)
        {
            context.MarkOverwrite(destinationPath);
        }
        else
        {
            context.MarkCreatedFile(destinationPath);
        }
        using var wrapped = faultInjector.WrapWriteStream(destinationPath, outStream);
        inStream.CopyTo(wrapped);
    }

    private void EnsureDirectory(string path, RestoreApplyContext context)
    {
        var full = Path.GetFullPath(path);
        PathSafetyPolicy.EnsureTotalPathLength(full, "Restore destination path");
        context.TrackTarget(full);

        if (!Directory.Exists(full))
        {
            faultInjector.BeforeCreateDirectory(full);
            Directory.CreateDirectory(full);
            context.MarkCreatedDirectory(full);
        }
    }

    private static void WriteManifest(string stageDir, IReadOnlyList<ManifestAsset> assets)
    {
        var payload = BuildPayloadIndex(stageDir);
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToString("yyyyMMdd-HHmmss");

        var manifest = new
        {
            schemaVersion = 1,
            createdAt = now.ToString("O"),
            timestamp,
            assets = assets.Select(asset => new
            {
                kind = asset.Kind,
                sourcePath = asset.SourcePath,
                archivePath = asset.ArchivePath
            }),
            payload = payload.Select(entry => new
            {
                archivePath = entry.ArchivePath,
                size = entry.Size,
                sha256 = entry.Sha256
            })
        };

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(stageDir, "manifest.json"), json);
    }

    private static List<ManifestPayload> BuildPayloadIndex(string stageDir)
    {
        var payload = new List<ManifestPayload>();
        foreach (var file in Directory.EnumerateFiles(stageDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(stageDir, file).Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(relative, "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var fs = File.OpenRead(file);
            var (hash, size) = HashStream(fs);
            payload.Add(new ManifestPayload(relative, size, hash));
        }

        payload.Sort((left, right) => string.CompareOrdinal(left.ArchivePath, right.ArchivePath));
        return payload;
    }

    private static PreparedArchive PrepareArchiveForRead(string archivePath, string? password)
    {
        if (IsEncryptedArchive(archivePath))
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidDataException("Password required for encrypted snapshot.");
            }

            var tempTar = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tar.gz");
            CryptoHelpers.DecryptFileWithPassword(archivePath, tempTar, password);
            var payloadType = GetArchiveType(tempTar, requireSignature: true);
            if (payloadType != "tar.gz")
            {
                throw new InvalidDataException("Encrypted archive does not contain a tar.gz payload.");
            }
            return new PreparedArchive(archivePath, tempTar, "encrypted", true);
        }

        var type = GetArchiveType(archivePath, requireSignature: true);
        return new PreparedArchive(archivePath, archivePath, type, false);
    }

    private static bool IsEncryptedArchive(string archivePath)
    {
        try
        {
            using var fs = File.OpenRead(archivePath);
            if (fs.Length < EncryptionMagic.Length) return false;
            var header = new byte[EncryptionMagic.Length];
            var read = fs.Read(header, 0, header.Length);
            if (read != header.Length) return false;
            return header.SequenceEqual(EncryptionMagic);
        }
        catch
        {
            return false;
        }
    }

    private static string GetArchiveType(string archivePath, bool requireSignature)
    {
        var lower = archivePath.ToLowerInvariant();
        string? extensionType = null;
        if (lower.EndsWith(".zip")) extensionType = "zip";
        if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz")) extensionType = "tar.gz";

        var signatureType = DetectArchiveSignature(archivePath);
        if (extensionType != null)
        {
            if (signatureType != null && !string.Equals(extensionType, signatureType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Archive type mismatch: extension suggests {extensionType} but signature indicates {signatureType}.");
            }

            if (requireSignature && signatureType == null)
            {
                throw new InvalidDataException($"Archive signature not recognized for {archivePath}.");
            }

            return extensionType;
        }

        if (signatureType != null)
        {
            return signatureType;
        }

        throw new InvalidDataException($"Unknown archive type for {archivePath}.");
    }

    private static string? DetectArchiveSignature(string archivePath)
    {
        try
        {
            using var fs = File.OpenRead(archivePath);
            if (fs.Length < 4) return null;
            var buffer = new byte[4];
            var read = fs.Read(buffer, 0, buffer.Length);
            if (read < 2) return null;

            if (buffer[0] == 0x50 && buffer[1] == 0x4B)
            {
                return "zip";
            }

            if (buffer[0] == 0x1F && buffer[1] == 0x8B)
            {
                return "tar.gz";
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static Task<ArchiveInspection> InspectTarArchive(string tarPath)
    {
        var entries = new List<string>();
        var metadata = new Dictionary<string, EntryMetadata>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? manifestRaw = null;
        int manifestCount = 0;

        using var inFs = File.OpenRead(tarPath);
        Stream dataStream = tarPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(inFs, CompressionMode.Decompress) : inFs;
        using var reader = new TarReader(dataStream);
        try
        {
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) != null)
            {
                var normalized = NormalizeArchivePath(entry.Name, "Tar entry path");
                if (normalized == null) continue;

                if (!seen.Add(normalized))
                {
                    throw new InvalidDataException($"Backup archive contains duplicate entry: {normalized}");
                }
                entries.Add(normalized);

                if (entry.EntryType == TarEntryType.Directory)
                {
                    metadata[normalized] = new EntryMetadata(true, 0, string.Empty);
                    continue;
                }

                var entryStream = entry.DataStream;
                if (entryStream == null)
                {
                    if (entry.Length > 0)
                    {
                        metadata[normalized] = new EntryMetadata(false, entry.Length, string.Empty);
                        continue;
                    }

                    entryStream = Stream.Null;
                }

                if (IsRootManifestEntry(normalized))
                {
                    manifestCount++;
                    using var buffer = new MemoryStream();
                    var (hash, size) = HashStream(entryStream, buffer);
                    metadata[normalized] = new EntryMetadata(false, size, hash);
                    manifestRaw = Encoding.UTF8.GetString(buffer.ToArray());
                }
                else
                {
                    var (hash, size) = HashStream(entryStream);
                    metadata[normalized] = new EntryMetadata(false, size, hash);
                }
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Backup archive is empty or corrupted.", ex);
        }

        if (manifestCount > 1)
        {
            throw new InvalidDataException($"Expected exactly one root manifest.json entry, found {manifestCount}.");
        }

        return Task.FromResult(new ArchiveInspection(entries, metadata, manifestRaw));
    }

    private static Task<ArchiveInspection> InspectZipArchive(string zipPath)
    {
        var entries = new List<string>();
        var metadata = new Dictionary<string, EntryMetadata>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? manifestRaw = null;
        int manifestCount = 0;

        try
        {
            using var fs = File.OpenRead(zipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                var normalized = NormalizeArchivePath(entry.FullName, "Zip entry path");
                if (normalized == null) continue;

                if (!seen.Add(normalized))
                {
                    throw new InvalidDataException($"Backup archive contains duplicate entry: {normalized}");
                }
                entries.Add(normalized);

                if (entry.FullName.EndsWith("/"))
                {
                    metadata[normalized] = new EntryMetadata(true, 0, string.Empty);
                    continue;
                }

                using var entryStream = entry.Open();
                if (IsRootManifestEntry(normalized))
                {
                    manifestCount++;
                    using var buffer = new MemoryStream();
                    var (hash, size) = HashStream(entryStream, buffer);
                    metadata[normalized] = new EntryMetadata(false, size, hash);
                    manifestRaw = Encoding.UTF8.GetString(buffer.ToArray());
                }
                else
                {
                    var (hash, size) = HashStream(entryStream);
                    metadata[normalized] = new EntryMetadata(false, size, hash);
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("Backup zip archive is corrupted or invalid.", ex);
        }

        if (manifestCount > 1)
        {
            throw new InvalidDataException($"Expected exactly one root manifest.json entry, found {manifestCount}.");
        }

        return Task.FromResult(new ArchiveInspection(entries, metadata, manifestRaw));
    }

    private static (string Hash, long Size) HashStream(Stream stream, Stream? copyTo = null)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            copyTo?.Write(buffer, 0, read);
            total += read;
        }

        var hash = hasher.GetHashAndReset();
        return (Convert.ToHexString(hash).ToLowerInvariant(), total);
    }

    private static ParsedManifest ParseManifest(string rawManifest)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawManifest);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Backup manifest is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Backup manifest must be an object.");
            }

            var schemaVersion = 0;
            if (root.TryGetProperty("schemaVersion", out var schemaProp))
            {
                if (schemaProp.ValueKind == JsonValueKind.Number && schemaProp.TryGetInt32(out var sv))
                {
                    schemaVersion = sv;
                }
                else if (schemaProp.ValueKind == JsonValueKind.String && int.TryParse(schemaProp.GetString(), out var svStr))
                {
                    schemaVersion = svStr;
                }
            }

            if (schemaVersion == 1)
            {
                if (!root.TryGetProperty("assets", out var assetsProp) || assetsProp.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("Backup manifest schemaVersion=1 requires an assets array.");
                }

                var assets = new List<ManifestAsset>();
                int index = 0;
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidDataException($"Backup manifest asset at index {index} must be an object.");
                    }

                    var kind = asset.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() : null;
                    var sourcePath = asset.TryGetProperty("sourcePath", out var sourceProp) ? sourceProp.GetString() : null;
                    var archivePathRaw = asset.TryGetProperty("archivePath", out var archiveProp) ? archiveProp.GetString() : null;

                    if (string.IsNullOrWhiteSpace(kind))
                    {
                        throw new InvalidDataException($"Backup manifest asset at index {index} is missing kind.");
                    }
                    if (string.IsNullOrWhiteSpace(sourcePath))
                    {
                        throw new InvalidDataException($"Backup manifest asset at index {index} is missing sourcePath.");
                    }
                    if (string.IsNullOrWhiteSpace(archivePathRaw))
                    {
                        throw new InvalidDataException($"Backup manifest asset at index {index} is missing archivePath.");
                    }

                    var archivePath = NormalizeArchivePath(archivePathRaw!, "Backup manifest asset path")!;
                    assets.Add(new ManifestAsset(kind!, sourcePath!, archivePath));
                    index++;
                }

                var payload = new List<ManifestPayload>();
                if (root.TryGetProperty("payload", out var payloadProp) && payloadProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in payloadProp.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object)
                        {
                            throw new InvalidDataException("Backup manifest payload entry must be an object.");
                        }

                        var archivePathRaw = entry.TryGetProperty("archivePath", out var archiveProp) ? archiveProp.GetString() : null;
                        var shaRaw = entry.TryGetProperty("sha256", out var shaProp) ? shaProp.GetString() : null;
                        var sizeRaw = entry.TryGetProperty("size", out var sizeProp) ? sizeProp.GetRawText() : null;

                        if (string.IsNullOrWhiteSpace(archivePathRaw))
                        {
                            throw new InvalidDataException("Backup manifest payload path is missing.");
                        }
                        if (string.IsNullOrWhiteSpace(shaRaw) || !Sha256Regex.IsMatch(shaRaw))
                        {
                            throw new InvalidDataException($"Backup manifest payload SHA-256 is invalid for: {archivePathRaw}");
                        }

                        if (!long.TryParse(sizeRaw, out var size) || size < 0)
                        {
                            throw new InvalidDataException($"Backup manifest payload size is invalid for: {archivePathRaw}");
                        }

                        var archivePath = NormalizeArchivePath(archivePathRaw!, "Backup manifest payload path")!;
                        payload.Add(new ManifestPayload(archivePath, size, shaRaw.ToLowerInvariant()));
                    }
                }

                var createdAt = root.TryGetProperty("createdAt", out var createdProp) ? createdProp.GetString() : null;
                var timestamp = root.TryGetProperty("timestamp", out var timestampProp) ? timestampProp.GetString() : null;
                return new ParsedManifest(1, createdAt, timestamp, assets, payload);
            }

            if (root.TryGetProperty("files", out var legacyFilesProp) && legacyFilesProp.ValueKind == JsonValueKind.Array)
            {
                var assets = new List<ManifestAsset>();
                foreach (var entry in legacyFilesProp.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String) continue;
                    var raw = entry.GetString();
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var archivePath = NormalizeArchivePath(raw.Replace('\\', '/'), "Legacy manifest file path")!;
                    assets.Add(new ManifestAsset("state", raw!, archivePath));
                }

                if (assets.Count == 0)
                {
                    throw new InvalidDataException("Legacy backup manifest has no files to verify.");
                }

                var timestamp = root.TryGetProperty("timestamp", out var tsProp) ? tsProp.GetString() : null;
                return new ParsedManifest(0, null, timestamp, assets, new List<ManifestPayload>());
            }

            throw new InvalidDataException("Unsupported backup manifest format: expected schemaVersion=1 assets or legacy files array.");
        }
    }

    private static string? NormalizeArchivePath(string rawEntryPath, string label)
    {
        var trimmed = (rawEntryPath ?? string.Empty).Replace('\\', '/').Trim();
        while (trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }
        trimmed = trimmed.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidDataException($"{label} is empty.");
        }

        if (trimmed.StartsWith("/") || (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':'))
        {
            throw new InvalidDataException($"{label} must be relative: {rawEntryPath}");
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == "." || segment == ".."))
        {
            throw new InvalidDataException($"{label} contains path traversal segments: {rawEntryPath}");
        }
        if (segments.Any(segment => segment.Length > PathSafetyPolicy.MaxPathSegmentLength))
        {
            throw new InvalidDataException($"{label} contains an overly long segment: {rawEntryPath}");
        }

        var normalized = string.Join('/', segments);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{label} resolves outside the archive root: {rawEntryPath}");
        }

        return normalized;
    }

    public sealed class RestoreApplyException : Exception
    {
        public RestoreApplyException(string message, Exception inner, bool partialWrite, bool overwroteExisting, bool cleanupSucceeded)
            : base(message, inner)
        {
            PartialWrite = partialWrite;
            OverwroteExisting = overwroteExisting;
            CleanupSucceeded = cleanupSucceeded;
        }

        public bool PartialWrite { get; }
        public bool OverwroteExisting { get; }
        public bool CleanupSucceeded { get; }
    }

    private sealed class RestoreApplyContext
    {
        private readonly Dictionary<string, string> trackedTargets = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> createdFiles = new();
        private readonly List<string> createdDirs = new();

        public bool PartialWrite { get; private set; }
        public bool OverwroteExisting { get; private set; }
        public bool HasChanges => PartialWrite || createdDirs.Count > 0;

        public void TrackTarget(string fullPath)
        {
            if (trackedTargets.TryGetValue(fullPath, out var existing))
            {
                if (!string.Equals(existing, fullPath, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Restore output contains case-colliding entries: {fullPath}");
                }
                return;
            }

            trackedTargets[fullPath] = fullPath;
        }

        public void MarkCreatedFile(string path)
        {
            PartialWrite = true;
            createdFiles.Add(path);
        }

        public void MarkCreatedDirectory(string path)
        {
            PartialWrite = true;
            createdDirs.Add(path);
        }

        public void MarkOverwrite(string path)
        {
            PartialWrite = true;
            OverwroteExisting = true;
        }

        public bool TryCleanup()
        {
            var ok = true;

            foreach (var file in createdFiles)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    ok = false;
                }
            }

            foreach (var dir in createdDirs.OrderByDescending(path => path.Length))
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch
                {
                    ok = false;
                }
            }

            return ok;
        }
    }

    private static List<RestoreAsset> BuildRestoreAssets(
        ArchiveInspection inspection,
        BackupScopeInfo scopeInfo,
        ParsedManifest? manifest,
        OpenClawManifest? openClawManifest)
    {
        if (openClawManifest != null)
        {
            var openClawAssets = new List<RestoreAsset>();
            foreach (var asset in openClawManifest.Assets)
            {
                var normalizedKind = NormalizeOpenClawKind(asset.Kind);
                if (!ShouldIncludeOpenClawKind(normalizedKind, scopeInfo))
                {
                    continue;
                }

                openClawAssets.Add(new RestoreAsset(normalizedKind, asset.ArchivePath, asset.SourcePath));
            }

            openClawAssets.Sort((left, right) => string.CompareOrdinal(left.ArchivePath, right.ArchivePath));
            return openClawAssets;
        }

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inspection.NormalizedEntries)
        {
            var root = GetRootSegment(entry);
            if (string.Equals(root, "manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
            roots.Add(root);
        }

        var assets = new List<RestoreAsset>();
        foreach (var root in roots)
        {
            if (!BackupScope.ShouldIncludeTopLevelEntry(root, scopeInfo))
            {
                continue;
            }

            assets.Add(new RestoreAsset(BackupScope.CategorizeTopLevelEntry(root), root, root));
        }

        assets.Sort((left, right) => string.CompareOrdinal(left.ArchivePath, right.ArchivePath));
        return assets;
    }

    private static List<string> BuildPayloadEntries(
        ParsedManifest? manifest,
        ArchiveInspection inspection,
        OpenClawManifest? openClawManifest)
    {
        if (manifest is { Payload.Count: > 0 })
        {
            return manifest.Payload.Select(entry => entry.ArchivePath).ToList();
        }

        var openClawManifestEntry = openClawManifest is null
            ? null
            : $"{openClawManifest.ArchiveRoot}/manifest.json";

        var entries = new List<string>();
        foreach (var pair in inspection.FileMetadata)
        {
            if (pair.Value.IsDirectory) continue;
            if (string.Equals(pair.Key, "manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(openClawManifestEntry) &&
                string.Equals(pair.Key, openClawManifestEntry, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            entries.Add(pair.Key);
        }

        entries.Sort(StringComparer.OrdinalIgnoreCase);
        return entries;
    }

    private static string NormalizeOpenClawKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return "other";
        if (string.Equals(kind, "credentials", StringComparison.OrdinalIgnoreCase)) return "creds";
        if (string.Equals(kind, "oauth", StringComparison.OrdinalIgnoreCase)) return "creds";
        if (string.Equals(kind, "state", StringComparison.OrdinalIgnoreCase)) return "sessions";
        return kind.Trim().ToLowerInvariant();
    }

    private static bool ShouldIncludeOpenClawKind(string kind, BackupScopeInfo scopeInfo)
    {
        if (scopeInfo.Tokens.Contains("full")) return true;
        return scopeInfo.Tokens.Contains(kind);
    }

    private static bool IsUnderArchiveRoot(string entry, string root)
    {
        if (string.Equals(entry, root, StringComparison.OrdinalIgnoreCase)) return true;
        return entry.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAssetRoot(string entry, IReadOnlyList<RestoreAsset> assets)
    {
        foreach (var asset in assets)
        {
            if (IsUnderArchiveRoot(entry, asset.ArchivePath))
            {
                return asset.ArchivePath;
            }
        }

        return GetRootSegment(entry);
    }

    private static bool ShouldUseManifestPaths(OpenClawManifest? manifest, string destinationRoot)
    {
        if (manifest?.Paths?.StateDir is null) return false;
        var stateDir = Path.GetFullPath(manifest.Paths.StateDir);
        var dest = Path.GetFullPath(destinationRoot);
        return string.Equals(stateDir, dest, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAssetDestinationRoot(
        string assetArchivePath,
        IReadOnlyList<RestoreAsset> assets,
        string destinationRoot,
        OpenClawManifest manifest,
        bool useManifestPaths)
    {
        var asset = assets.FirstOrDefault(candidate =>
            string.Equals(candidate.ArchivePath, assetArchivePath, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            return destinationRoot;
        }

        if (useManifestPaths)
        {
            return asset.SourcePath;
        }

        var stateDir = manifest.Paths?.StateDir;
        if (!string.IsNullOrWhiteSpace(stateDir) &&
            IsPathUnderRoot(asset.SourcePath, stateDir!))
        {
            var relative = Path.GetRelativePath(stateDir!, asset.SourcePath);
            return Path.Combine(destinationRoot, relative);
        }

        if (string.Equals(asset.Kind, "config", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(destinationRoot, "openclaw.json");
        }

        if (string.Equals(asset.Kind, "creds", StringComparison.OrdinalIgnoreCase))
        {
            var leaf = asset.SourcePath.Contains("oauth", StringComparison.OrdinalIgnoreCase) ? "oauth" : "credentials";
            return Path.Combine(destinationRoot, leaf);
        }

        if (string.Equals(asset.Kind, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(asset.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = "workspace";
            return Path.Combine(destinationRoot, "workspace", name);
        }

        return Path.Combine(destinationRoot, Path.GetFileName(asset.SourcePath));
    }

    private static string MapOpenClawEntryToDestination(
        string entry,
        IReadOnlyList<RestoreAsset> assets,
        string destinationRoot,
        OpenClawManifest manifest)
    {
        var useManifestPaths = ShouldUseManifestPaths(manifest, destinationRoot);
        foreach (var asset in assets)
        {
            if (!IsUnderArchiveRoot(entry, asset.ArchivePath)) continue;

            var relative = entry.Substring(asset.ArchivePath.Length).TrimStart('/');
            var destRoot = ResolveAssetDestinationRoot(asset.ArchivePath, assets, destinationRoot, manifest, useManifestPaths);
            if (string.IsNullOrWhiteSpace(relative))
            {
                return destRoot;
            }

            return Path.Combine(destRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        return Path.Combine(destinationRoot, entry.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ResolveAssetDestinationRoot(
        string assetArchivePath,
        IReadOnlyList<RestoreAsset> assets,
        string destinationRoot,
        OpenClawManifest manifest)
    {
        var useManifestPaths = ShouldUseManifestPaths(manifest, destinationRoot);
        return ResolveAssetDestinationRoot(assetArchivePath, assets, destinationRoot, manifest, useManifestPaths);
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static BackupArchiveKind ResolveArchiveKind(PreparedArchive prepared, OpenClawManifest? manifest)
    {
        if (manifest is null)
        {
            return prepared.IsTemporary ? BackupArchiveKind.EncryptedReClaw : BackupArchiveKind.ReClaw;
        }

        return prepared.IsTemporary ? BackupArchiveKind.EncryptedOpenClaw : BackupArchiveKind.OpenClaw;
    }

    private static OpenClawManifest? TryParseOpenClawManifest(string rawManifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawManifest);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("archiveRoot", out var archiveRootProp) ||
                archiveRootProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var archiveRoot = archiveRootProp.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(archiveRoot)) return null;
            archiveRoot = NormalizeArchivePath(archiveRoot, "OpenClaw archive root")!;

            if (!root.TryGetProperty("assets", out var assetsProp) || assetsProp.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var assets = new List<OpenClawManifestAsset>();
            foreach (var asset in assetsProp.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) return null;
                var kind = asset.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() : null;
                var sourcePath = asset.TryGetProperty("sourcePath", out var sourceProp) ? sourceProp.GetString() : null;
                var archivePathRaw = asset.TryGetProperty("archivePath", out var archiveProp) ? archiveProp.GetString() : null;

                if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(archivePathRaw))
                {
                    return null;
                }

                var archivePath = NormalizeArchivePath(archivePathRaw!, "OpenClaw manifest asset path")!;
                if (!archivePath.StartsWith(archiveRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    archivePath = NormalizeArchivePath($"{archiveRoot}/{archivePath}", "OpenClaw manifest asset path")!;
                }
                assets.Add(new OpenClawManifestAsset(kind!, sourcePath!, archivePath));
            }

            OpenClawManifestPaths? paths = null;
            if (root.TryGetProperty("paths", out var pathsProp) && pathsProp.ValueKind == JsonValueKind.Object)
            {
                var stateDir = pathsProp.TryGetProperty("stateDir", out var stateProp) ? stateProp.GetString() : null;
                var configPath = pathsProp.TryGetProperty("configPath", out var configProp) ? configProp.GetString() : null;
                var oauthDir = pathsProp.TryGetProperty("oauthDir", out var oauthProp) ? oauthProp.GetString() : null;
                var workspaces = new List<string>();
                if (pathsProp.TryGetProperty("workspaceDirs", out var wsProp) && wsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in wsProp.EnumerateArray())
                    {
                        if (entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
                        {
                            workspaces.Add(entry.GetString()!);
                        }
                    }
                }

                paths = new OpenClawManifestPaths(stateDir, configPath, oauthDir, workspaces);
            }

            return new OpenClawManifest(archiveRoot, assets, paths);
        }
        catch
        {
            return null;
        }
    }

    private static string GetRootSegment(string entry)
    {
        var normalized = entry.Replace('\\', '/');
        var index = normalized.IndexOf('/');
        return index < 0 ? normalized : normalized[..index];
    }

    private static bool IsRootManifestEntry(string normalized)
    {
        if (string.Equals(normalized, "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !string.Equals(parts[1], "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return parts[0].IndexOf("openclaw-backup", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private sealed record RestoreAsset(string Kind, string ArchivePath, string SourcePath);

    private sealed record BackupStaging(string StageDirectory, IReadOnlyList<ManifestAsset> Assets);

    private sealed record ManifestAsset(string Kind, string SourcePath, string ArchivePath);

    private sealed record ManifestPayload(string ArchivePath, long Size, string Sha256);

    private sealed record ParsedManifest(
        int SchemaVersion,
        string? CreatedAt,
        string? Timestamp,
        IReadOnlyList<ManifestAsset> Assets,
        IReadOnlyList<ManifestPayload> Payload);

    private sealed record OpenClawManifest(
        string ArchiveRoot,
        IReadOnlyList<OpenClawManifestAsset> Assets,
        OpenClawManifestPaths? Paths);

    private sealed record OpenClawManifestAsset(
        string Kind,
        string SourcePath,
        string ArchivePath);

    private sealed record OpenClawManifestPaths(
        string? StateDir,
        string? ConfigPath,
        string? OAuthDir,
        IReadOnlyList<string> WorkspaceDirs);

    private sealed record EntryMetadata(bool IsDirectory, long Size, string Sha256);

    private sealed record ArchiveInspection(
        IReadOnlyList<string> NormalizedEntries,
        IReadOnlyDictionary<string, EntryMetadata> FileMetadata,
        string? ManifestRaw);

    private sealed record PreparedArchive(string OriginalPath, string ArchivePath, string ArchiveType, bool IsTemporary)
    {
        public void Cleanup()
        {
            if (!IsTemporary) return;
            try { if (File.Exists(ArchivePath)) File.Delete(ArchivePath); } catch { }
        }
    }
}
