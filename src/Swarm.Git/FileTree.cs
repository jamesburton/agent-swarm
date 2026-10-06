namespace Swarm.Git;

/// <summary>Deletes directory trees the way Windows needs.</summary>
public static class FileTree
{
    /// <summary>Deletes a directory tree, clearing read-only attributes (git objects) first; a missing directory is a no-op.</summary>
    /// <param name="directory">The directory.</param>
    public static void DeleteTree(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        // Skip reparse points so a junction never leads the attribute reset outside the tree.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        SharedFile.Retry(() => Directory.Delete(directory, recursive: true));
    }
}
