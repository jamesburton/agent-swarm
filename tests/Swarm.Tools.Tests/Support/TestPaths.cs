using Swarm.Git;

namespace Swarm.Tools.Tests.Support;

/// <summary>Short sandbox roots, far below the 200-char path guard.</summary>
public static class TestPaths
{
    /// <summary>Gets the sandbox parent directory (<c>SWARM_TEST_ROOT</c> or <c>%TEMP%\swt</c>).</summary>
    public static string Root =>
        Environment.GetEnvironmentVariable("SWARM_TEST_ROOT") is { Length: > 0 } r ? r : Path.Combine(Path.GetTempPath(), "swt");

    /// <summary>Creates a fresh sandbox directory named by 8 hex characters under <see cref="Root"/>.</summary>
    /// <returns>The full path of the new directory.</returns>
    public static string NewSandbox()
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Path.GetFullPath(dir);
    }
}

/// <summary>A throwaway directory that is deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    /// <summary>Gets the full path of the directory.</summary>
    public string Dir { get; } = TestPaths.NewSandbox();

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            FileTree.DeleteTree(Dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A lingering child process may still hold a file; the temp root is disposable.
        }
    }
}
