using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ReClaw.Core;
using Xunit;

namespace ReClaw.Core.Tests;

public sealed class BackupEdgeCaseTests
{
    [Fact]
    public async Task VerifySnapshot_EmptyArchive_Fails()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        try
        {
            using (var outFs = File.Create(archivePath))
            using (var gz = new GZipStream(outFs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, leaveOpen: true))
            {
                // no entries
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
    public async Task VerifySnapshot_CorruptManifest_Fails()
    {
        using var tempDir = new TempDir();
        WriteFile(Path.Combine(tempDir.Path, "manifest.json"), "not-json");
        WriteFile(Path.Combine(tempDir.Path, "openclaw.json"), "{ \"ok\": true }");

        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        try
        {
            TarUtils.CreateTarGzDirectory(tempDir.Path, archivePath);
            var service = new BackupService();
            await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifySnapshotAsync(archivePath));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task VerifySnapshot_MissingPayloadEntry_Fails()
    {
        using var tempDir = new TempDir();
        var manifest = new
        {
            schemaVersion = 1,
            createdAt = DateTimeOffset.UtcNow.ToString("O"),
            timestamp = "missing-payload",
            assets = new[]
            {
                new { kind = "config", sourcePath = "openclaw.json", archivePath = "openclaw.json" }
            },
            payload = new[]
            {
                new { archivePath = "missing.txt", size = 1, sha256 = new string('a', 64) }
            }
        };
        File.WriteAllText(Path.Combine(tempDir.Path, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        try
        {
            TarUtils.CreateTarGzDirectory(tempDir.Path, archivePath);
            var service = new BackupService();
            await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifySnapshotAsync(archivePath));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task VerifySnapshot_DuplicateEntries_Fails()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        try
        {
            using (var outFs = File.Create(archivePath))
            using (var gz = new GZipStream(outFs, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gz, leaveOpen: true))
            {
                var entry1 = new PaxTarEntry(TarEntryType.RegularFile, "openclaw.json")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":1}"))
                };
                tar.WriteEntry(entry1);

                var entry2 = new PaxTarEntry(TarEntryType.RegularFile, "openclaw.json")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":2}"))
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
    public async Task PreviewRestore_InvalidScope_Fails()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, scope: "full");
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.PreviewRestoreAsync(archivePath, sourceDir.Path, scope: "invalid+scope"));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task PreviewRestore_Detects_FileDirectoryConflict()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "config", "settings.json"), "{ \"ok\": true }");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, scope: "full");

            using var destDir = new TempDir();
            WriteFile(Path.Combine(destDir.Path, "config"), "i am a file");

            var preview = await service.PreviewRestoreAsync(archivePath, destDir.Path, scope: "full");
            Assert.Equal(1, preview.RestorePayloadEntries);
            Assert.Equal(1, preview.OverwritePayloadEntries);
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, content);
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
