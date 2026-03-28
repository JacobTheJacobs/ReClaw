using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Actions;
using ReClaw.Core;

namespace ReClaw.App.Execution;

public sealed class BackupDiffService
{
    private const string ManifestFileName = "manifest.json";

    public async Task<BackupDiffSummary> DiffAsync(
        string leftArchive,
        string rightArchive,
        bool redactSecrets,
        string? password,
        CancellationToken cancellationToken)
    {
        var left = await ReadArchiveAsync(leftArchive, password, cancellationToken).ConfigureAwait(false);
        var right = await ReadArchiveAsync(rightArchive, password, cancellationToken).ConfigureAwait(false);

        var leftAssets = left.Manifest.Assets.Select(ToAssetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightAssets = right.Manifest.Assets.Select(ToAssetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedAssets = rightAssets.Except(leftAssets, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var removedAssets = leftAssets.Except(rightAssets, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var changedAssets = leftAssets.Intersect(rightAssets, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

        var configDiff = DiffConfig(left, right, redactSecrets);
        var workspaceAdded = DiffWorkspaceAdded(left, right);
        var workspaceRemoved = DiffWorkspaceRemoved(left, right);
        var credentialChanges = DiffCredentials(left, right);

        var redactedNote = redactSecrets
            ? "Config/credential values are redacted. Review backups locally for full details."
            : null;

        var redactedCount = redactSecrets
            ? configDiff.Count(line => IsSensitiveKey(ExtractDiffKey(line)))
            : 0;
        var configChanged = configDiff.Any(line => line.StartsWith("added:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("removed:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("changed:", StringComparison.OrdinalIgnoreCase));
        var workspaceChanged = workspaceAdded.Count > 0 || workspaceRemoved.Count > 0;
        var credentialChangesPresent = credentialChanges.Any(line => line.StartsWith("added:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("removed:", StringComparison.OrdinalIgnoreCase));

        return new BackupDiffSummary(
            leftArchive,
            rightArchive,
            addedAssets,
            removedAssets,
            changedAssets,
            configDiff,
            workspaceAdded,
            workspaceRemoved,
            credentialChanges,
            redactedNote,
            redactedCount,
            configChanged,
            workspaceChanged,
            credentialChangesPresent);
    }

    private static string ToAssetKey(OpenClawManifestAsset asset)
        => $"{asset.Kind}:{asset.SourcePath}";

    private static List<string> DiffConfig(BackupArchiveSnapshot left, BackupArchiveSnapshot right, bool redactSecrets)
    {
        if (left.ConfigText == null || right.ConfigText == null)
        {
            return new List<string> { "Config missing in one or both archives." };
        }

        if (!Json5Reader.TryParse(left.ConfigText, out var leftDoc) || !Json5Reader.TryParse(right.ConfigText, out var rightDoc))
        {
            return new List<string> { "Config diff unavailable (parse failed)." };
        }

        using (leftDoc)
        using (rightDoc)
        {
            var leftMap = FlattenJson(leftDoc!.RootElement, redactSecrets);
            var rightMap = FlattenJson(rightDoc!.RootElement, redactSecrets);

            var diffs = new List<string>();
            foreach (var key in leftMap.Keys.Except(rightMap.Keys).OrderBy(x => x))
            {
                diffs.Add($"removed: {key}");
            }
            foreach (var key in rightMap.Keys.Except(leftMap.Keys).OrderBy(x => x))
            {
                diffs.Add($"added: {key}");
            }
            foreach (var key in leftMap.Keys.Intersect(rightMap.Keys).OrderBy(x => x))
            {
                if (!string.Equals(leftMap[key], rightMap[key], StringComparison.Ordinal))
                {
                    diffs.Add($"changed: {key}");
                }
            }

            return diffs.Count == 0 ? new List<string> { "No config changes detected." } : diffs;
        }
    }

    private static Dictionary<string, string> FlattenJson(JsonElement element, bool redact, string prefix = "")
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var path = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                foreach (var pair in FlattenJson(prop.Value, redact, path))
                {
                    output[pair.Key] = pair.Value;
                }
            }
            return output;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            output[prefix] = $"array({element.GetArrayLength()})";
            return output;
        }

        if (string.IsNullOrEmpty(prefix))
        {
            output["<root>"] = RedactValue(prefix, element, redact);
            return output;
        }

        output[prefix] = RedactValue(prefix, element, redact);
        return output;
    }

    private static string RedactValue(string keyPath, JsonElement value, bool redact)
    {
        if (redact && IsSensitiveKey(keyPath))
        {
            return "<redacted>";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => value.GetRawText()
        };
    }

    private static bool IsSensitiveKey(string keyPath)
    {
        var lowered = keyPath.ToLowerInvariant();
        return lowered.Contains("token") || lowered.Contains("secret") || lowered.Contains("password") || lowered.Contains("key");
    }

    private static string ExtractDiffKey(string diffLine)
    {
        if (string.IsNullOrWhiteSpace(diffLine))
        {
            return string.Empty;
        }

        var colon = diffLine.IndexOf(':');
        if (colon < 0 || colon + 1 >= diffLine.Length)
        {
            return diffLine.Trim();
        }

        return diffLine[(colon + 1)..].Trim();
    }

    private static List<string> DiffWorkspaceAdded(BackupArchiveSnapshot left, BackupArchiveSnapshot right)
        => right.WorkspaceEntries.Except(left.WorkspaceEntries, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

    private static List<string> DiffWorkspaceRemoved(BackupArchiveSnapshot left, BackupArchiveSnapshot right)
        => left.WorkspaceEntries.Except(right.WorkspaceEntries, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

    private static List<string> DiffCredentials(BackupArchiveSnapshot left, BackupArchiveSnapshot right)
    {
        if (left.CredentialEntries.SetEquals(right.CredentialEntries))
        {
            return new List<string> { "No credential file changes detected." };
        }

        var added = right.CredentialEntries.Except(left.CredentialEntries, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var removed = left.CredentialEntries.Except(right.CredentialEntries, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var changes = new List<string>();
        changes.AddRange(added.Select(entry => $"added: {entry}"));
        changes.AddRange(removed.Select(entry => $"removed: {entry}"));
        return changes;
    }

    private static async Task<BackupArchiveSnapshot> ReadArchiveAsync(string archivePath, string? password, CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("Archive not found.", archivePath);
        }

        var prepared = PrepareArchive(archivePath, password);
        try
        {
            if (prepared.ArchiveType == "zip")
            {
                return await ReadZipAsync(prepared).ConfigureAwait(false);
            }

            return await ReadTarAsync(prepared, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (prepared.IsTemporary)
            {
                TryDelete(prepared.ArchivePath);
            }
        }
    }

    private static PreparedArchive PrepareArchive(string archivePath, string? password)
    {
        if (IsEncryptedArchive(archivePath))
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidDataException("Password required for encrypted backup diff.");
            }

            var temp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tar.gz");
            CryptoHelpers.DecryptFileWithPassword(archivePath, temp, password);
            return new PreparedArchive(archivePath, temp, "tar.gz", true);
        }

        var type = archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "zip" : "tar.gz";
        return new PreparedArchive(archivePath, archivePath, type, false);
    }

    private static async Task<BackupArchiveSnapshot> ReadZipAsync(PreparedArchive prepared)
    {
        using var fs = File.OpenRead(prepared.ArchivePath);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = FindManifestEntry(archive.Entries.Select(entry => entry.FullName));
        if (manifestEntry == null)
        {
            throw new InvalidDataException("Backup manifest not found in archive.");
        }

        var manifestText = await ReadEntryAsync(archive.GetEntry(manifestEntry)!).ConfigureAwait(false);
        var manifest = ParseManifest(manifestText);
        var configText = TryReadAssetText(archive, manifest, "config");
        var workspaceEntries = ListAssetEntries(archive.Entries.Select(entry => entry.FullName), manifest, "workspace");
        var credentialEntries = ListAssetEntries(archive.Entries.Select(entry => entry.FullName), manifest, "credentials");
        return new BackupArchiveSnapshot(prepared.OriginalPath, manifest, configText, workspaceEntries, credentialEntries);
    }

    private static async Task<BackupArchiveSnapshot> ReadTarAsync(PreparedArchive prepared, CancellationToken cancellationToken)
    {
        using var fs = File.OpenRead(prepared.ArchivePath);
        Stream stream = fs;
        if (prepared.ArchivePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            stream = new GZipStream(fs, CompressionMode.Decompress);
        }
        using var reader = new TarReader(stream);
        string? manifestPath = null;
        var entries = new List<string>();
        var entryContent = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            if (entry.EntryType == TarEntryType.Directory) continue;
            var path = entry.Name;
            entries.Add(path);
            if (path.EndsWith("/" + ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                manifestPath ??= path;
            }

            if (entry.DataStream != null)
            {
                using var ms = new MemoryStream();
                await entry.DataStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                entryContent[path] = ms.ToArray();
            }
        }

        if (manifestPath == null)
        {
            throw new InvalidDataException("Backup manifest not found in archive.");
        }

        var manifestText = ReadBytes(entryContent, manifestPath);
        var manifest = ParseManifest(manifestText);
        var configText = TryReadAssetText(entryContent, manifest, "config");
        var workspaceEntries = ListAssetEntries(entries, manifest, "workspace");
        var credentialEntries = ListAssetEntries(entries, manifest, "credentials");
        return new BackupArchiveSnapshot(prepared.OriginalPath, manifest, configText, workspaceEntries, credentialEntries);
    }

    private static string? FindManifestEntry(IEnumerable<string> entries)
    {
        foreach (var entry in entries)
        {
            var normalized = entry.Replace('\\', '/');
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && string.Equals(parts[1], ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }
        }

        return entries.FirstOrDefault(e => e.EndsWith("/" + ManifestFileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadBytes(Dictionary<string, byte[]> content, string path)
    {
        if (!content.TryGetValue(path, out var bytes))
        {
            throw new InvalidDataException($"Archive entry missing: {path}");
        }
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static string? TryReadAssetText(ZipArchive archive, OpenClawBackupManifest manifest, string kind)
    {
        var asset = manifest.Assets.FirstOrDefault(a => string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (asset == null) return null;
        var entry = archive.GetEntry(asset.ArchivePath);
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static string? TryReadAssetText(Dictionary<string, byte[]> content, OpenClawBackupManifest manifest, string kind)
    {
        var asset = manifest.Assets.FirstOrDefault(a => string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (asset == null) return null;
        return content.TryGetValue(asset.ArchivePath, out var bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null;
    }

    private static HashSet<string> ListAssetEntries(IEnumerable<string> entries, OpenClawBackupManifest manifest, string kind)
    {
        var asset = manifest.Assets.FirstOrDefault(a => string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (asset == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var prefix = asset.ArchivePath.TrimEnd('/') + "/";
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static OpenClawBackupManifest ParseManifest(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var archiveRoot = root.GetProperty("archiveRoot").GetString() ?? "";
        var normalizedRoot = NormalizePath(archiveRoot);
        var assets = new List<OpenClawManifestAsset>();
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var archivePath = asset.GetProperty("archivePath").GetString() ?? "";
            var combined = CombineArchiveRoot(normalizedRoot, archivePath);
            assets.Add(new OpenClawManifestAsset(
                asset.GetProperty("kind").GetString() ?? "",
                asset.GetProperty("sourcePath").GetString() ?? "",
                combined));
        }

        return new OpenClawBackupManifest(normalizedRoot, assets);
    }

    private static string CombineArchiveRoot(string archiveRoot, string archivePath)
    {
        var root = NormalizePath(archiveRoot);
        var path = NormalizePath(archivePath);
        if (string.IsNullOrWhiteSpace(root)) return path;
        if (path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return path;
        return $"{root}/{path}";
    }

    private static string NormalizePath(string value)
    {
        return value.Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');
    }

    private static bool IsEncryptedArchive(string archivePath)
    {
        try
        {
            using var fs = File.OpenRead(archivePath);
            var header = new byte[8];
            var read = fs.Read(header, 0, header.Length);
            if (read != header.Length) return false;
            var magic = System.Text.Encoding.ASCII.GetBytes("RCLAWENC1");
            return header.SequenceEqual(magic);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record OpenClawBackupManifest(string ArchiveRoot, IReadOnlyList<OpenClawManifestAsset> Assets);

    private sealed record OpenClawManifestAsset(string Kind, string SourcePath, string ArchivePath);

    private sealed record BackupArchiveSnapshot(
        string ArchivePath,
        OpenClawBackupManifest Manifest,
        string? ConfigText,
        HashSet<string> WorkspaceEntries,
        HashSet<string> CredentialEntries);

    private sealed record PreparedArchive(string OriginalPath, string ArchivePath, string ArchiveType, bool IsTemporary);
}
