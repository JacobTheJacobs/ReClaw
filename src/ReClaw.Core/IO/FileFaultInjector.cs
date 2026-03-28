using System;
using System.IO;

namespace ReClaw.Core.IO;

internal interface IFileFaultInjector
{
    void BeforeSnapshotCreate(string snapshotPath);
    void BeforeCreateDirectory(string path);
    void BeforeCopyFile(string sourcePath, string destinationPath);
    Stream WrapWriteStream(string destinationPath, Stream inner);
    void BeforeDeletePath(string path);
}

internal sealed class NullFileFaultInjector : IFileFaultInjector
{
    public void BeforeSnapshotCreate(string snapshotPath) { }
    public void BeforeCreateDirectory(string path) { }
    public void BeforeCopyFile(string sourcePath, string destinationPath) { }
    public Stream WrapWriteStream(string destinationPath, Stream inner) => inner;
    public void BeforeDeletePath(string path) { }
}
