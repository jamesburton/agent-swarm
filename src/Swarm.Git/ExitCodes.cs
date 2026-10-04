namespace Swarm.Git;

/// <summary>
/// Process exit codes shared by every swarm tool. Schema version 1: values are never renumbered, only appended.
/// </summary>
public static class ExitCodes
{
    /// <summary>Everything succeeded (batch: every task landed, including after an automatic rebase).</summary>
    public const int Ok = 0;

    /// <summary>The tool worked but work came back: tasks returned (batch) or the gated command failed (testgate).</summary>
    public const int Returned = 1;

    /// <summary>Usage or configuration error, including over-long paths.</summary>
    public const int Usage = 2;

    /// <summary>Bad input: tasks file unreadable or invalid, epic branch missing or checked out.</summary>
    public const int BadInput = 3;

    /// <summary>Environment failure: git, worktree, file system, a command that cannot start, a lander contract violation, cancellation.</summary>
    public const int Environment = 4;

    /// <summary>No test slot became free within the configured maximum wait.</summary>
    public const int GateTimeout = 5;
}
