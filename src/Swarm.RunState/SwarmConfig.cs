using System.Text.Json.Serialization;

namespace Swarm.RunState;

/// <summary>Adaptive batch-size bounds.</summary>
public sealed record BatchSizeConfig
{
    /// <summary>Gets the first batch size.</summary>
    public int Start { get; init; } = 4;

    /// <summary>Gets the smallest batch size (after red batches).</summary>
    public int Min { get; init; } = 2;

    /// <summary>Gets the largest batch size (after green batches).</summary>
    public int Max { get; init; } = 8;
}

/// <summary>Shared configuration of testgate and batch (<c>.swarm/batch.json</c>, schema version 1).</summary>
public sealed record SwarmConfig
{
    /// <summary>Gets the schema version (must be 1).</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Gets the number of concurrent test slots on this machine.</summary>
    public int Slots { get; init; } = 2;

    /// <summary>Gets the batch-size bounds.</summary>
    public BatchSizeConfig Batch { get; init; } = new();

    /// <summary>Gets the age in seconds after which an unrefreshed slot lock is stale.</summary>
    public int ExpirySec { get; init; } = 60;

    /// <summary>Gets the heartbeat interval in seconds.</summary>
    public int HeartbeatSec { get; init; } = 5;

    /// <summary>Gets the slot polling interval in milliseconds.</summary>
    public int PollMs { get; init; } = 200;

    /// <summary>Gets the maximum seconds to wait for a slot (0 = forever) before exit 5.</summary>
    public int MaxWaitSec { get; init; } = 3600;

    /// <summary>Gets the base branch (consumed by epic creation in Plan C).</summary>
    public string BaseBranch { get; init; } = "main";

    /// <summary>Gets the epic id.</summary>
    public string Epic { get; init; } = "E1";

    /// <summary>Gets the epic branch template; <c>{epic}</c> is replaced by <see cref="Epic"/>.</summary>
    public string EpicBranchTemplate { get; init; } = "epic/{epic}";

    /// <summary>Gets the full-suite command (program and arguments).</summary>
    public IReadOnlyList<string> TestCommand { get; init; } = ["dotnet", "test"];

    /// <summary>Gets the run-state directory; relative paths are under the main worktree.</summary>
    public string StateDir { get; init; } = ".docs/runs";

    /// <summary>Gets the absolute root for tool worktrees, or null for <c>&lt;main parent&gt;/&lt;repo&gt;-wt</c>.</summary>
    public string? WorktreeRoot { get; init; }

    /// <summary>Gets how many times a conflicting task is automatically rebased (copy ref) and requeued.</summary>
    public int MaxRebaseAttempts { get; init; } = 1;

    /// <summary>Gets a value indicating whether tasks touching the same files are kept in separate batches.</summary>
    public bool Prebatch { get; init; } = true;

    /// <summary>Gets how many finished runs are kept under the state dir.</summary>
    public int KeepRuns { get; init; } = 20;

    /// <summary>Gets how batch lands green tasks: <c>squash</c> (default) or <c>fast-forward</c>.</summary>
    public string Lander { get; init; } = LanderNames.Squash;

    /// <summary>Gets the squash lander settings.</summary>
    public SquashConfig Squash { get; init; } = new();

    /// <summary>Gets the <c>worktree</c> tool section (task branch naming; Plan C).</summary>
    public WorktreeSection Worktree { get; init; } = new();

    /// <summary>Gets the <c>epicTool</c> section (epic branch naming; Plan C).</summary>
    public EpicSection EpicTool { get; init; } = new();

    /// <summary>Gets the epic branch name.</summary>
    [JsonIgnore]
    public string EpicBranch => EpicBranchTemplate.Replace("{epic}", Epic, StringComparison.Ordinal);
}

/// <summary>Command-line values that override the config file (null = not given).</summary>
public sealed record ConfigOverrides
{
    /// <summary>Gets no overrides.</summary>
    public static ConfigOverrides None { get; } = new();

    /// <summary>Gets the slot count override.</summary>
    public int? Slots { get; init; }

    /// <summary>Gets the start batch size override.</summary>
    public int? Start { get; init; }

    /// <summary>Gets the minimum batch size override.</summary>
    public int? Min { get; init; }

    /// <summary>Gets the maximum batch size override.</summary>
    public int? Max { get; init; }

    /// <summary>Gets the expiry override.</summary>
    public int? ExpirySec { get; init; }

    /// <summary>Gets the heartbeat override.</summary>
    public int? HeartbeatSec { get; init; }

    /// <summary>Gets the poll interval override.</summary>
    public int? PollMs { get; init; }

    /// <summary>Gets the maximum wait override.</summary>
    public int? MaxWaitSec { get; init; }

    /// <summary>Gets the epic id override.</summary>
    public string? Epic { get; init; }

    /// <summary>Gets the state dir override (already absolute when it comes from a flag).</summary>
    public string? StateDir { get; init; }

    /// <summary>Gets the test command override.</summary>
    public IReadOnlyList<string>? TestCommand { get; init; }

    /// <summary>Applies the overrides (flags win).</summary>
    /// <param name="config">Config from file or defaults.</param>
    /// <returns>The merged config (not yet validated).</returns>
    public SwarmConfig ApplyTo(SwarmConfig config) => config with
    {
        Slots = Slots ?? config.Slots,
        Batch = config.Batch with { Start = Start ?? config.Batch.Start, Min = Min ?? config.Batch.Min, Max = Max ?? config.Batch.Max },
        ExpirySec = ExpirySec ?? config.ExpirySec,
        HeartbeatSec = HeartbeatSec ?? config.HeartbeatSec,
        PollMs = PollMs ?? config.PollMs,
        MaxWaitSec = MaxWaitSec ?? config.MaxWaitSec,
        Epic = Epic ?? config.Epic,
        StateDir = StateDir ?? config.StateDir,
        TestCommand = TestCommand ?? config.TestCommand,
    };
}
