using System;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Collections.Generic;
using System.Linq;
using ReClaw.Core.IO;

namespace ReClaw.Core
{
    public static class TarUtils
    {
        public static void CreateTarGzDirectory(string sourceDir, string outPath)
        {
            if (!Directory.Exists(sourceDir)) throw new DirectoryNotFoundException(sourceDir);

            using var outFs = File.Create(outPath);
            using var gz = new GZipStream(outFs, CompressionLevel.Optimal);
            using var tar = new TarWriter(gz, leaveOpen: true);

            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file).Replace(Path.DirectorySeparatorChar, '/');
                tar.WriteEntry(file, relative);
            }
        }

    public static void ExtractTarGzToDirectory(string tarGzPath, string destination)
    {
        Directory.CreateDirectory(destination);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extractedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var inFs = File.OpenRead(tarGzPath);
        using var gz = new GZipStream(inFs, CompressionMode.Decompress);
        using var tr = new TarReader(gz);
        TarEntry? entry;
        while ((entry = tr.GetNextEntry()) != null)
        {
            var normalized = NormalizeEntryName(entry.Name);
            if (!seen.Add(normalized))
            {
                throw new InvalidDataException($"Archive contains duplicate entry: {normalized}");
            }

            var destPath = GetSafeDestinationPath(destination, entry.Name);
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(destPath);
                continue;
            }

            if (entry.EntryType == TarEntryType.GlobalExtendedAttributes ||
                entry.EntryType == TarEntryType.ExtendedAttributes)
            {
                continue;
            }

            if (entry.EntryType == TarEntryType.SymbolicLink)
            {
                throw new InvalidDataException($"Archive contains symbolic link entry: {entry.Name}");
            }

            if (entry.EntryType == TarEntryType.HardLink)
            {
                if (string.IsNullOrWhiteSpace(entry.LinkName))
                {
                    throw new InvalidDataException($"Archive hardlink entry missing target: {entry.Name}");
                }

                var linkNormalized = NormalizeEntryName(entry.LinkName);
                if (!extractedFiles.TryGetValue(linkNormalized, out var sourcePath))
                {
                    throw new InvalidDataException($"Archive hardlink target not found for entry '{entry.Name}': {entry.LinkName}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? destination);
                File.Copy(sourcePath, destPath, overwrite: false);
                extractedFiles[normalized] = destPath;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? destination);
            using var outFs = File.Create(destPath);
            if (entry.DataStream is null)
            {
                // Some tar writers emit link/special entries without a data stream.
                // Create an empty file to preserve the entry while avoiding traversal risks.
                extractedFiles[normalized] = destPath;
                continue;
            }
            entry.DataStream.CopyTo(outFs);
            extractedFiles[normalized] = destPath;
        }
    }

        private static string GetSafeDestinationPath(string destinationRoot, string entryName)
        {
            var trimmed = (entryName ?? string.Empty).Replace('\\', '/').Trim();
            while (trimmed.StartsWith("./", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..];
            }
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Archive entry path must be relative: {entryName}");
            }

            if (trimmed.StartsWith("/") || (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':'))
            {
                throw new InvalidDataException($"Archive entry path must be relative: {entryName}");
            }

            trimmed = trimmed.TrimStart('/');

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new InvalidDataException("Archive entry path is empty.");
            }

            var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment == "." || segment == ".."))
            {
                throw new InvalidDataException($"Archive entry path contains traversal segments: {entryName}");
            }
            if (segments.Any(segment => segment.Length > PathSafetyPolicy.MaxPathSegmentLength))
            {
                throw new InvalidDataException($"Archive entry path contains an overly long segment: {entryName}");
            }

            var relative = string.Join('/', segments).Replace('/', Path.DirectorySeparatorChar);
            var combined = Path.Combine(destinationRoot, relative);
            var destinationFull = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var combinedFull = Path.GetFullPath(combined);
            PathSafetyPolicy.EnsureTotalPathLength(combinedFull, "Archive entry path");

            if (!combinedFull.Equals(destinationFull, StringComparison.OrdinalIgnoreCase) &&
                !combinedFull.StartsWith(destinationFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Archive entry path escapes destination: {entryName}");
            }

            return combinedFull;
        }

        private static string NormalizeEntryName(string entryName)
        {
            var trimmed = (entryName ?? string.Empty).Replace('\\', '/').Trim();
            while (trimmed.StartsWith("./", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..];
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Archive entry path must be relative: {entryName}");
            }

            if (trimmed.StartsWith("/") || (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':'))
            {
                throw new InvalidDataException($"Archive entry path must be relative: {entryName}");
            }

            trimmed = trimmed.TrimStart('/');

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new InvalidDataException("Archive entry path is empty.");
            }

            var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment == "." || segment == ".."))
            {
                throw new InvalidDataException($"Archive entry path contains traversal segments: {entryName}");
            }
            if (segments.Any(segment => segment.Length > PathSafetyPolicy.MaxPathSegmentLength))
            {
                throw new InvalidDataException($"Archive entry path contains an overly long segment: {entryName}");
            }

            return string.Join('/', segments);
        }
    }
}
