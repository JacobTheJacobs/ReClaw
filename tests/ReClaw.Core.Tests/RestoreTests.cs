using System;
using System.IO;
using System.Threading.Tasks;
using ReClaw.Core;
using Xunit;

namespace ReClaw.Core.Tests;

public sealed class RestoreTests
{
    [Fact]
    public async Task PreviewRestore_ReportsOverwrites()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");
        WriteFile(Path.Combine(sourceDir.Path, "credentials", "tokens.json"), "{ \"token\": \"x\" }");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, scope: "full");

            using var destDir = new TempDir();
            WriteFile(Path.Combine(destDir.Path, "openclaw.json"), "{ \"ok\": false }");

            var preview = await service.PreviewRestoreAsync(archivePath, destDir.Path, scope: "config");

            Assert.Equal(1, preview.RestorePayloadEntries);
            Assert.Equal(1, preview.OverwritePayloadEntries);
            Assert.Contains(preview.Assets, asset => asset.ArchivePath == "openclaw.json" && asset.Exists);
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task RestoreAsync_WithScope_RestoresOnlySelected()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");
        WriteFile(Path.Combine(sourceDir.Path, ".env"), "TOKEN=secret");
        WriteFile(Path.Combine(sourceDir.Path, "credentials", "tokens.json"), "{ \"token\": \"x\" }");
        WriteFile(Path.Combine(sourceDir.Path, "sessions", "session.json"), "{ \"id\": 1 }");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, scope: "full");

            using var destDir = new TempDir();
            await service.RestoreAsync(archivePath, destDir.Path, scope: "config");

            Assert.True(File.Exists(Path.Combine(destDir.Path, "openclaw.json")));
            Assert.True(File.Exists(Path.Combine(destDir.Path, ".env")));
            Assert.False(Directory.Exists(Path.Combine(destDir.Path, "credentials")));
            Assert.False(Directory.Exists(Path.Combine(destDir.Path, "sessions")));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task RestoreAsync_Fails_OnWrongPassword()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz.enc");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, password: "correct-password", scope: "full");

            using var destDir = new TempDir();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAsync(archivePath, destDir.Path, "wrong-password"));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task RestoreAsync_Fails_OnTamperedArchive()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        var tamperedPath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}-tampered.tar.gz");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, scope: "full");

            using var extracted = new TempDir();
            TarUtils.ExtractTarGzToDirectory(archivePath, extracted.Path);
            WriteFile(Path.Combine(extracted.Path, "openclaw.json"), "{ \"ok\": false }");
            TarUtils.CreateTarGzDirectory(extracted.Path, tamperedPath);

            using var destDir = new TempDir();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAsync(tamperedPath, destDir.Path));
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteFile(tamperedPath);
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
