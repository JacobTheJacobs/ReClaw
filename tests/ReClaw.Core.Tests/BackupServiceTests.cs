using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ReClaw.Core;
using Xunit;

namespace ReClaw.Core.Tests;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task VerifySnapshot_Fails_OnTamperedArchive()
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

            await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifySnapshotAsync(tamperedPath));
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteFile(tamperedPath);
        }
    }

    [Fact]
    public async Task VerifySnapshot_Fails_OnWrongPassword()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz.enc");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, password: "correct-password", scope: "full");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifySnapshotAsync(archivePath, "wrong-password"));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task CreateBackup_WithScopedExport_IncludesExpectedAssets()
    {
        using var sourceDir = new TempDir();
        WriteFile(Path.Combine(sourceDir.Path, "openclaw.json"), "{ \"ok\": true }");
        WriteFile(Path.Combine(sourceDir.Path, ".env"), "TOKEN=secret");
        WriteFile(Path.Combine(sourceDir.Path, "credentials", "tokens.json"), "{ \"token\": \"x\" }");
        WriteFile(Path.Combine(sourceDir.Path, "sessions", "session.json"), "{ \"id\": 1 }");
        WriteFile(Path.Combine(sourceDir.Path, "workspace", "notes.txt"), "skip");

        var service = new BackupService();
        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");

        try
        {
            await service.CreateBackupAsync(sourceDir.Path, archivePath, scope: "config+creds+sessions");
            var manifestJson = ReadManifestFromTarGz(archivePath);

            using var doc = JsonDocument.Parse(manifestJson);
            var assets = doc.RootElement.GetProperty("assets")
                .EnumerateArray()
                .Select(asset => asset.GetProperty("archivePath").GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Contains("openclaw.json", assets);
            Assert.Contains("credentials", assets);
            Assert.Contains("sessions", assets);
            Assert.DoesNotContain("workspace", assets);
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

    private static string ReadManifestFromTarGz(string tarGzPath)
    {
        using var fs = File.OpenRead(tarGzPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new TarReader(gz);
        TarEntry entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            var name = entry.Name.Replace('\\', '/');
            if (!string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.DataStream == null)
            {
                throw new InvalidDataException("manifest.json entry has no data stream.");
            }

            using var ms = new MemoryStream();
            entry.DataStream.CopyTo(ms);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        throw new InvalidDataException("manifest.json not found in archive.");
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
