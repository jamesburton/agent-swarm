namespace Swarm.Git;

/// <summary>Retries file operations that fail transiently on Windows (sharing violations, delete-pending, scanners).</summary>
public static class SharedFile
{
    /// <summary>Runs an action, retrying transient file failures.</summary>
    /// <param name="action">The file operation.</param>
    /// <param name="attempts">Total attempts.</param>
    /// <param name="delayMs">Delay between attempts.</param>
    public static void Retry(Action action, int attempts = 40, int delayMs = 25) =>
        Retry(() => { action(); return true; }, attempts, delayMs);

    /// <summary>Runs a function, retrying transient file failures.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="action">The file operation.</param>
    /// <param name="attempts">Total attempts.</param>
    /// <param name="delayMs">Delay between attempts.</param>
    /// <returns>The function's result.</returns>
    public static T Retry<T>(Func<T> action, int attempts = 40, int delayMs = 25)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception e) when (attempt < attempts && IsTransient(e))
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    /// <summary>Decides whether a failure is worth retrying.</summary>
    /// <param name="e">The failure.</param>
    /// <returns>True for sharing/lock/access failures; false for missing files or directories.</returns>
    public static bool IsTransient(Exception e) =>
        e is (IOException and not FileNotFoundException and not DirectoryNotFoundException) or UnauthorizedAccessException;
}
