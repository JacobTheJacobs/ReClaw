using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Win32.SafeHandles;

namespace ReClaw.Core.IO
{
    public static class ZipHandler
    {
        private const uint LocalFileHeaderSignature = 0x04034b50;
        private const int LocalHeaderFixedSize = 30; // bytes

        public static async Task<bool> ContainsWinZipAesAsync(Stream zipStream, CancellationToken ct = default)
        {
            if (!zipStream.CanRead) throw new ArgumentException("Stream must be readable", nameof(zipStream));

            long originalPos = zipStream.CanSeek ? zipStream.Position : -1;
            var buf = new byte[LocalHeaderFixedSize];
            try
            {
                while (true)
                {
                    int sig = await ReadInt32Async(zipStream, ct).ConfigureAwait(false);
                    if (sig == -1) break;
                    if ((uint)sig != LocalFileHeaderSignature)
                    {
                        if (!TrySeek(zipStream, -3, SeekOrigin.Current)) break;
                        continue;
                    }

                    int read = await zipStream.ReadAsync(buf, 0, LocalHeaderFixedSize - 4, ct).ConfigureAwait(false);
                    if (read < LocalHeaderFixedSize - 4) break;
                    ushort compressionMethod = BitConverter.ToUInt16(buf, 4);
                    if (compressionMethod == 99) return true;

                    ushort nameLen = BitConverter.ToUInt16(buf, 22);
                    ushort extraLen = BitConverter.ToUInt16(buf, 24);

                    if (nameLen > 0) await zipStream.SkipAsync(nameLen, ct).ConfigureAwait(false);
                    if (extraLen > 0) await zipStream.SkipAsync(extraLen, ct).ConfigureAwait(false);

                    // For brevity, inspect only the first entry header; extend to central directory if needed.
                    break;
                }
            }
            finally
            {
                if (originalPos >= 0) zipStream.Seek(originalPos, SeekOrigin.Begin);
            }
            return false;
        }

        private static async Task<int> ReadInt32Async(Stream s, CancellationToken ct)
        {
            var b = new byte[4];
            int r = await s.ReadAsync(b, 0, 4, ct).ConfigureAwait(false);
            if (r < 4) return -1;
            return BitConverter.ToInt32(b, 0);
        }

        private static bool TrySeek(Stream s, long offset, SeekOrigin origin)
        {
            if (!s.CanSeek) return false;
            try { s.Seek(offset, origin); return true; } catch { return false; }
        }

        public static async Task ExtractAsync(Stream zipStream, string destination, CancellationToken ct = default)
        {
            if (await ContainsWinZipAesAsync(zipStream, ct).ConfigureAwait(false))
            {
                var exe = ZipUtils.Find7zExecutable();
                if (exe == null) throw new InvalidOperationException("WinZip AES detected and no 7z available.");
                string tmp = Path.GetTempFileName();
                try
                {
                    using (var fs = File.OpenWrite(tmp))
                    {
                        if (zipStream.CanSeek) zipStream.Seek(0, SeekOrigin.Begin);
                        await zipStream.CopyToAsync(fs, 16*1024, ct).ConfigureAwait(false);
                    }
                    var entries = await ZipUtils.List7zEntriesAsync(exe, tmp, ct).ConfigureAwait(false);
                    ValidateEntryNames(entries, "7z listing");

                    var staging = Path.Combine(destination, $".reclaw-7z-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(staging);
                    try
                    {
                        await ZipUtils.Run7zExtractAsync(exe, tmp, staging, ct).ConfigureAwait(false);
                        ValidateExtractedTree(staging);
                        CopyDirectory(staging, destination);
                    }
                    catch
                    {
                        try
                        {
                            if (Directory.Exists(staging))
                            {
                                Directory.Delete(staging, recursive: true);
                            }
                        }
                        catch
                        {
                            // best effort cleanup
                        }
                        throw;
                    }
                    finally
                    {
                        try
                        {
                            if (Directory.Exists(staging))
                            {
                                Directory.Delete(staging, recursive: true);
                            }
                        }
                        catch
                        {
                            // best effort cleanup
                        }
                    }
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            else
            {
                if (zipStream.CanSeek) zipStream.Seek(0, SeekOrigin.Begin);
                using var zr = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zr.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.FullName))
                    {
                        continue;
                    }

                    var outPath = GetSafeDestinationPath(destination, entry.FullName);
                    var normalized = NormalizeEntryName(entry.FullName, "Zip entry path");
                    if (!seen.Add(normalized))
                    {
                        throw new InvalidDataException($"Backup archive contains duplicate entry: {normalized}");
                    }

                    if (entry.FullName.EndsWith("/"))
                    {
                        Directory.CreateDirectory(outPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? destination);
                    using var inStream = entry.Open();
                    using var outFs = File.Create(outPath);
                    await inStream.CopyToAsync(outFs, 16 * 1024, ct).ConfigureAwait(false);
                }
            }
        }

        internal static void ValidateEntryNames(IReadOnlyList<ZipEntryInfo> entries, string source)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry.IsLink)
                {
                    throw new InvalidDataException($"Archive contains link entry: {entry.Path}");
                }

                var normalized = NormalizeEntryName(entry.Path, source);
                if (!seen.Add(normalized))
                {
                    throw new InvalidDataException($"Archive contains duplicate entry: {normalized}");
                }
            }
        }

        internal static void ValidateExtractedTree(string root)
        {
            if (!Directory.Exists(root))
            {
                throw new InvalidDataException("Extraction root does not exist.");
            }

            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(path);
                PathSafetyPolicy.EnsureTotalPathLength(full, "Extracted output path");
                if (!full.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
                    !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Extracted path escapes root: {path}");
                }

                var relative = Path.GetRelativePath(rootFull, full);
                if (relative.StartsWith("..", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Extracted path escapes root: {path}");
                }

                if (!seen.Add(relative.Replace('\\', '/')))
                {
                    throw new InvalidDataException($"Extracted output contains case-colliding entries: {relative}");
                }

                var info = new FileInfo(full);
                var attrs = info.Attributes;
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException($"Extracted output contains reparse points: {relative}");
                }

                if (info.LinkTarget != null)
                {
                    throw new InvalidDataException($"Extracted output contains symbolic links: {relative}");
                }

                if (!Directory.Exists(full))
                {
                    var linkCount = FileLinkInspector.TryGetLinkCount(full);
                    if (linkCount.HasValue && linkCount.Value > 1)
                    {
                        throw new InvalidDataException($"Extracted output contains hard links: {relative}");
                    }
                }
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

        internal static string NormalizeEntryName(string entryName, string source)
        {
            if (string.IsNullOrWhiteSpace(entryName))
            {
                throw new InvalidDataException($"{source} entry path is empty.");
            }

            var hasForward = entryName.Contains('/');
            var hasBackward = entryName.Contains('\\');
            if (hasForward && hasBackward)
            {
                throw new InvalidDataException($"{source} entry path contains mixed separators: {entryName}");
            }

            var normalized = entryName.Replace('\\', '/').Trim();
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }

            if (normalized.StartsWith("//", StringComparison.Ordinal) || normalized.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{source} entry path must be relative: {entryName}");
            }

            if (normalized.StartsWith("/") || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
            {
                throw new InvalidDataException($"{source} entry path must be relative: {entryName}");
            }

            normalized = normalized.TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new InvalidDataException($"{source} entry path is empty.");
            }

            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment == "." || segment == ".."))
            {
                throw new InvalidDataException($"{source} entry path contains traversal segments: {entryName}");
            }
            if (segments.Any(segment => segment.Length > PathSafetyPolicy.MaxPathSegmentLength))
            {
                throw new InvalidDataException($"{source} entry path contains an overly long segment: {entryName}");
            }

            return string.Join('/', segments);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, dir);
                var targetDir = Path.Combine(destination, rel);
                Directory.CreateDirectory(targetDir);
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
                using var inStream = File.OpenRead(file);
                using var outStream = File.Create(target);
                inStream.CopyTo(outStream);
            }
        }
    }

    static class StreamExt
    {
        public static async Task SkipAsync(this Stream s, int bytes, CancellationToken ct = default)
        {
            var buf = new byte[8192];
            int remaining = bytes;
            while (remaining > 0)
            {
                int read = await s.ReadAsync(buf, 0, Math.Min(buf.Length, remaining), ct).ConfigureAwait(false);
                if (read <= 0) break;
                remaining -= read;
            }
        }
    }

    static class ZipUtils
    {
        public static string? Find7zExecutable()
        {
            string? exe = TryWhich("7z") ?? TryWhich("7za");
            if (exe != null) return exe;
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        private static string? TryWhich(string name)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                    Arguments = name,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                string? outp = p.StandardOutput.ReadLine();
                p.WaitForExit(2000);
                if (!string.IsNullOrWhiteSpace(outp) && File.Exists(outp)) return outp;
            }
            catch { }
            return null;
        }

        public static Task Run7zExtractAsync(string exePath, string archiveFile, string destination, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource();
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"x -y -o\"{destination}\" \"{archiveFile}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start 7z");
            Task.Run(async () =>
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                if (p.ExitCode != 0) tcs.SetException(new Exception($"7z exit code {p.ExitCode}"));
                else tcs.SetResult();
                p.Dispose();
            }, ct);
            return tcs.Task;
        }

        public static async Task<IReadOnlyList<ZipEntryInfo>> List7zEntriesAsync(string exePath, string archiveFile, CancellationToken ct = default)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"l -slt \"{archiveFile}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start 7z");
            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"7z list failed: {error}".Trim());
            }

            return Parse7zEntries(output);
        }

        private static IReadOnlyList<ZipEntryInfo> Parse7zEntries(string output)
        {
            var entries = new List<ZipEntryInfo>();
            string? path = null;
            bool isLink = false;
            bool isDir = false;

            foreach (var raw in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        entries.Add(new ZipEntryInfo(path, isDir, isLink));
                    }
                    path = null;
                    isLink = false;
                    isDir = false;
                    continue;
                }

                if (line.StartsWith("Path = ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        entries.Add(new ZipEntryInfo(path, isDir, isLink));
                        isLink = false;
                        isDir = false;
                    }
                    path = line.Substring("Path = ".Length);
                    continue;
                }

                if (line.StartsWith("Folder = ", StringComparison.OrdinalIgnoreCase))
                {
                    isDir = line.EndsWith("+", StringComparison.OrdinalIgnoreCase);
                }
                else if (line.StartsWith("Attributes = ", StringComparison.OrdinalIgnoreCase))
                {
                    var attrs = line.Substring("Attributes = ".Length);
                    if (attrs.Contains('D'))
                    {
                        isDir = true;
                    }
                }
                else if (line.StartsWith("Link = ", StringComparison.OrdinalIgnoreCase))
                {
                    isLink = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                entries.Add(new ZipEntryInfo(path, isDir, isLink));
            }

            return entries;
        }
    }

    internal sealed record ZipEntryInfo(string Path, bool IsDirectory, bool IsLink);

    internal static class FileLinkInspector
    {
        public static int? TryGetLinkCount(string path)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return GetWindowsLinkCount(path);
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static int? GetWindowsLinkCount(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            {
                return null;
            }
            return (int)info.NumberOfLinks;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
