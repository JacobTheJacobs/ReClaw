using System;
using System.IO;
using System.Threading.Tasks;
using ReClaw.Core;
using ReClaw.Core.IO;
using Xunit;

namespace ReClaw.Core.Tests;

public sealed class RestoreFaultInjectionTests
{
    [Fact]
    public async Task Restore_FailsOnSecondCopy_CleansUpNewFiles()
    {
        using var source = new TempDir();
        File.WriteAllText(Path.Combine(source.Path, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(source.Path, "z.txt"), "zulu");

        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        var creator = new BackupService();
        try
        {
            await creator.CreateBackupAsync(source.Path, archivePath, scope: "full");

            using var dest = new TempDir();
            var injector = new TestFaultInjector
            {
                FailCopyPredicate = path => path.EndsWith("z.txt", StringComparison.OrdinalIgnoreCase)
            };
            var restoreService = new BackupService(injector);

            var ex = await Assert.ThrowsAsync<BackupService.RestoreApplyException>(() =>
                restoreService.RestoreAsync(archivePath, dest.Path, scope: "full"));

            Assert.True(ex.PartialWrite);
            Assert.True(ex.CleanupSucceeded);
            Assert.False(File.Exists(Path.Combine(dest.Path, "a.txt")));
            Assert.False(File.Exists(Path.Combine(dest.Path, "z.txt")));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task Restore_PartialWriteFailure_RemovesPartialFile()
    {
        using var source = new TempDir();
        File.WriteAllText(Path.Combine(source.Path, "openclaw.json"), "{ \"ok\": true }");

        var archivePath = Path.Combine(Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}.tar.gz");
        var creator = new BackupService();
        try
        {
            await creator.CreateBackupAsync(source.Path, archivePath, scope: "full");

            using var dest = new TempDir();
            var injector = new TestFaultInjector
            {
                FailAfterBytes = 4
            };
            var restoreService = new BackupService(injector);

            var ex = await Assert.ThrowsAsync<BackupService.RestoreApplyException>(() =>
                restoreService.RestoreAsync(archivePath, dest.Path, scope: "config"));

            Assert.True(ex.PartialWrite);
            Assert.True(ex.CleanupSucceeded);
            Assert.False(File.Exists(Path.Combine(dest.Path, "openclaw.json")));
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    [Fact]
    public async Task ResetService_FaultInjection_Throws()
    {
        using var root = new TempDir();
        var openClawHome = Path.Combine(root.Path, "openclaw");
        var configDir = Path.Combine(root.Path, "config");
        var dataDir = Path.Combine(root.Path, "data");
        var backupDir = Path.Combine(dataDir, "backups");

        Directory.CreateDirectory(openClawHome);
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(backupDir);

        var injector = new TestFaultInjector
        {
            FailDeletePredicate = _ => true
        };
        var resetService = new ResetService(injector);
        var plan = resetService.BuildPlan(new ResetContext(openClawHome, configDir, dataDir, backupDir), ResetMode.PreserveBackups);

        await Assert.ThrowsAsync<IOException>(() => resetService.ExecuteAsync(plan));
    }

    private sealed class TestFaultInjector : IFileFaultInjector
    {
        public Func<string, bool> FailCopyPredicate { get; init; } = _ => false;
        public int? FailAfterBytes { get; init; }
        public Func<string, bool> FailDeletePredicate { get; init; } = _ => false;

        public void BeforeSnapshotCreate(string snapshotPath)
        {
        }

        public void BeforeCreateDirectory(string path)
        {
        }

        public void BeforeCopyFile(string sourcePath, string destinationPath)
        {
            if (FailCopyPredicate(destinationPath))
            {
                throw new IOException("Simulated disk full during restore.");
            }
        }

        public Stream WrapWriteStream(string destinationPath, Stream inner)
        {
            if (FailAfterBytes is { } limit)
            {
                return new FaultyWriteStream(inner, limit);
            }
            return inner;
        }

        public void BeforeDeletePath(string path)
        {
            if (FailDeletePredicate(path))
            {
                throw new IOException("Simulated reset delete failure.");
            }
        }
    }

    private sealed class FaultyWriteStream : Stream
    {
        private readonly Stream inner;
        private readonly long limit;
        private long written;

        public FaultyWriteStream(Stream inner, long limit)
        {
            this.inner = inner;
            this.limit = limit;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (written >= limit)
            {
                throw new IOException("Simulated partial write failure.");
            }

            var remaining = limit - written;
            var toWrite = (int)Math.Min(count, remaining);
            inner.Write(buffer, offset, toWrite);
            written += toWrite;

            if (toWrite < count)
            {
                throw new IOException("Simulated partial write failure.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
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
