using Swarm.Git;

namespace Swarm.Tools.Tests.Git;

public class FileTreeTests
{
    [Fact]
    public void DeletesReadOnlyFiles()
    {
        // Git object files are read-only on Windows; Directory.Delete alone fails on them.
        var dir = Path.Combine(Path.GetTempPath(), "swt-ft-" + Guid.NewGuid().ToString("N")[..8]);
        var file = Path.Combine(dir, "objects", "ab", "cdef");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        FileTree.DeleteTree(dir);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void MissingDirectory_IsNoOp()
    {
        var missing = Path.Combine(Path.GetTempPath(), "swt-none-" + Guid.NewGuid().ToString("N"));
        Assert.Null(Record.Exception(() => FileTree.DeleteTree(missing)));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Retry_SucceedsAfterTransientFailures()
    {
        var calls = 0;
        var value = SharedFile.Retry(() => ++calls < 3 ? throw new IOException("busy") : calls, attempts: 5, delayMs: 1);
        Assert.Equal(3, value);
    }

    [Fact]
    public void Retry_DoesNotRetryFileNotFound()
    {
        var calls = 0;
        Assert.Throws<FileNotFoundException>(() => SharedFile.Retry(() => { calls++; throw new FileNotFoundException(); }, attempts: 5, delayMs: 1));
        Assert.Equal(1, calls);
    }
}
