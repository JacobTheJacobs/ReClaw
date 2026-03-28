using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using ReClaw.Core;
using Xunit;

namespace ReClaw.Core.Tests;

public sealed class BackupAdversarialTests
{
    [Fact]
    public async Task VerifySnapshot_MissingPassword_Throws()
    {
        using var source = new TempDir();
        File.WriteAllText(Path.Combine(source.Path, "openclaw.json"), "{ \"ok\": true }");

        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        var service = new BackupService();
        try
        {
            await service.CreateBackupAsync(source.Path, archivePath, password: "secret", scope: "config");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifySnapshotAsync(archivePath));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task VerifySnapshot_WrongPassword_Throws()
    {
        using var source = new TempDir();
        File.WriteAllText(Path.Combine(source.Path, "openclaw.json"), "{ \"ok\": true }");

        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        var service = new BackupService();
        try
        {
            await service.CreateBackupAsync(source.Path, archivePath, password: "secret", scope: "config");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifySnapshotAsync(archivePath, "wrong"));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task VerifySnapshot_MismatchedExtension_Throws()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        try
        {
            using (var fs = File.Create(archivePath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("manifest.json");
                await using var es = entry.Open();
                await es.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{}"));
            }

            var service = new BackupService();
            await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifySnapshotAsync(archivePath));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task VerifySnapshot_TruncatedZip_Throws()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.zip");
        try
        {
            using (var fs = File.Create(archivePath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("manifest.json");
                await using var es = entry.Open();
                await es.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{}"));
            }

            var truncated = File.ReadAllBytes(archivePath);
            await File.WriteAllBytesAsync(archivePath, truncated.AsSpan(0, Math.Min(12, truncated.Length)).ToArray());

            var service = new BackupService();
            await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifySnapshotAsync(archivePath));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task VerifySnapshot_AllowsZeroByteFiles()
    {
        using var source = new TempDir();
        File.WriteAllText(Path.Combine(source.Path, "openclaw.json"), "");
        var zeroPath = Path.Combine(source.Path, "credentials", "tokens.json");
        Directory.CreateDirectory(Path.GetDirectoryName(zeroPath)!);
        File.WriteAllBytes(zeroPath, Array.Empty<byte>());

        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        var service = new BackupService();
        try
        {
            await service.CreateBackupAsync(source.Path, archivePath, scope: "config+creds");
            var summary = await service.VerifySnapshotAsync(archivePath);
            Assert.Equal("tar.gz", summary.ArchiveType);
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task VerifySnapshot_DuplicateTarEntries_Throws()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        try
        {
            using (var outFs = File.Create(archivePath))
            using (var gz = new GZipStream(outFs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, leaveOpen: true))
            {
                var entry1 = new PaxTarEntry(TarEntryType.RegularFile, "dup.txt")
                {
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("one"))
                };
                tar.WriteEntry(entry1);
                var entry2 = new PaxTarEntry(TarEntryType.RegularFile, "dup.txt")
                {
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("two"))
                };
                tar.WriteEntry(entry2);
            }

            var service = new BackupService();
            await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifySnapshotAsync(archivePath));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task VerifySnapshot_DuplicateZipEntries_Throws()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.zip");
        try
        {
            using (var fs = File.Create(archivePath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry1 = zip.CreateEntry("dup.txt");
                await using (var entryStream = entry1.Open())
                {
                    await entryStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("one"));
                }

                var entry2 = zip.CreateEntry("dup.txt");
                await using (var entryStream = entry2.Open())
                {
                    await entryStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("two"));
                }
            }

            var service = new BackupService();
            await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifySnapshotAsync(archivePath));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task VerifySnapshot_MissingPayloadEntry_Throws()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        try
        {
            var manifest = new
            {
                schemaVersion = 1,
                createdAt = "2026-01-01T00:00:00Z",
                timestamp = "20260101-000000",
                assets = new[]
                {
                    new { kind = "config", sourcePath = "C:\\fake", archivePath = "config/openclaw.json" }
                },
                payload = new[]
                {
                    new { archivePath = "config/openclaw.json", size = 5, sha256 = "deadbeef" }
                }
            };
            var json = JsonSerializer.Serialize(manifest);

            using (var outFs = File.Create(archivePath))
            using (var gz = new GZipStream(outFs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, leaveOpen: true))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "manifest.json")
                {
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))
                };
                tar.WriteEntry(entry);
            }

            var service = new BackupService();
            await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifySnapshotAsync(archivePath));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
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
