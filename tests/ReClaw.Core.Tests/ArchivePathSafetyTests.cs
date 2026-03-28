using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReClaw.Core;
using ReClaw.Core.IO;
using Xunit;

namespace ReClaw.Core.Tests;

public sealed class ArchivePathSafetyTests
{
    [Fact]
    public void TarExtraction_RejectsTraversal()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        using var destDir = new TempDir();

        try
        {
            using (var outFs = File.Create(archivePath))
            using (var gz = new GZipStream(outFs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, leaveOpen: true))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "../evil.txt")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("nope"))
                };
                tar.WriteEntry(entry);
            }

            Assert.Throws<InvalidDataException>(() => TarUtils.ExtractTarGzToDirectory(archivePath, destDir.Path));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public void TarExtraction_Rejects_WindowsAbsolutePaths()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        using var destDir = new TempDir();

        try
        {
            using (var outFs = File.Create(archivePath))
            using (var gz = new GZipStream(outFs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, leaveOpen: true))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "C:\\evil.txt")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("nope"))
                };
                tar.WriteEntry(entry);
            }

            Assert.Throws<InvalidDataException>(() => TarUtils.ExtractTarGzToDirectory(archivePath, destDir.Path));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public void TarExtraction_Rejects_DriveRelativePaths()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        using var destDir = new TempDir();

        try
        {
            using (var outFs = File.Create(archivePath))
            using (var gz = new GZipStream(outFs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, leaveOpen: true))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "C:evil.txt")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("nope"))
                };
                tar.WriteEntry(entry);
            }

            Assert.Throws<InvalidDataException>(() => TarUtils.ExtractTarGzToDirectory(archivePath, destDir.Path));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public void TarExtraction_Rejects_UncPaths()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        using var destDir = new TempDir();

        try
        {
            using (var outFs = File.Create(archivePath))
            using (var gz = new GZipStream(outFs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, leaveOpen: true))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "\\\\server\\share\\evil.txt")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("nope"))
                };
                tar.WriteEntry(entry);
            }

            Assert.Throws<InvalidDataException>(() => TarUtils.ExtractTarGzToDirectory(archivePath, destDir.Path));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task ZipExtraction_RejectsTraversal()
    {
        using var destDir = new TempDir();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("../evil.txt");
            await using var entryStream = entry.Open();
            var payload = Encoding.UTF8.GetBytes("nope");
            await entryStream.WriteAsync(payload);
        }

        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ZipHandler.ExtractAsync(ms, destDir.Path));
    }

    [Fact]
    public async Task ZipExtraction_Rejects_WindowsAbsolutePaths()
    {
        using var destDir = new TempDir();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("C:\\evil.txt");
            await using var entryStream = entry.Open();
            var payload = Encoding.UTF8.GetBytes("nope");
            await entryStream.WriteAsync(payload);
        }

        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ZipHandler.ExtractAsync(ms, destDir.Path));
    }

    [Fact]
    public async Task ZipExtraction_Rejects_UncPaths()
    {
        using var destDir = new TempDir();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("\\\\server\\share\\evil.txt");
            await using var entryStream = entry.Open();
            var payload = Encoding.UTF8.GetBytes("nope");
            await entryStream.WriteAsync(payload);
        }

        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ZipHandler.ExtractAsync(ms, destDir.Path));
    }

    [Fact]
    public void TarExtraction_Rejects_CaseVariantCollisions()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        using var destDir = new TempDir();

        try
        {
            using (var outFs = File.Create(archivePath))
            using (var gz = new GZipStream(outFs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, leaveOpen: true))
            {
                var entry1 = new PaxTarEntry(TarEntryType.RegularFile, "Config/one.txt")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("one"))
                };
                tar.WriteEntry(entry1);

                var entry2 = new PaxTarEntry(TarEntryType.RegularFile, "config/one.txt")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("two"))
                };
                tar.WriteEntry(entry2);
            }

            Assert.Throws<InvalidDataException>(() => TarUtils.ExtractTarGzToDirectory(archivePath, destDir.Path));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task ZipExtraction_Rejects_TotalPathLength()
    {
        using var destDir = new TempDir();
        using var ms = new MemoryStream();
        var segment = new string('a', 200);
        var longPath = string.Join("/", Enumerable.Repeat(segment, 6));
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry($"{longPath}/file.txt");
            await using var entryStream = entry.Open();
            var payload = Encoding.UTF8.GetBytes("nope");
            await entryStream.WriteAsync(payload);
        }

        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ZipHandler.ExtractAsync(ms, destDir.Path));
    }

    [Fact]
    public void TarExtraction_Rejects_TotalPathLength()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        using var destDir = new TempDir();
        var segment = new string('a', 200);
        var longPath = string.Join("/", Enumerable.Repeat(segment, 6));

        try
        {
            using (var outFs = File.Create(archivePath))
            using (var gz = new GZipStream(outFs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, leaveOpen: true))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{longPath}/file.txt")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("nope"))
                };
                tar.WriteEntry(entry);
            }

            Assert.Throws<InvalidDataException>(() => TarUtils.ExtractTarGzToDirectory(archivePath, destDir.Path));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public void TarExtraction_Rejects_LongSegments()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        using var destDir = new TempDir();
        var longName = new string('a', 300) + ".txt";

        try
        {
            using (var outFs = File.Create(archivePath))
            using (var gz = new GZipStream(outFs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, leaveOpen: true))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, longName)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("nope"))
                };
                tar.WriteEntry(entry);
            }

            Assert.Throws<InvalidDataException>(() => TarUtils.ExtractTarGzToDirectory(archivePath, destDir.Path));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task ZipExtraction_Rejects_LongSegments()
    {
        using var destDir = new TempDir();
        using var ms = new MemoryStream();
        var longName = new string('a', 300) + ".txt";
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(longName);
            await using var entryStream = entry.Open();
            var payload = Encoding.UTF8.GetBytes("nope");
            await entryStream.WriteAsync(payload);
        }

        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ZipHandler.ExtractAsync(ms, destDir.Path));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}");

        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }
}
