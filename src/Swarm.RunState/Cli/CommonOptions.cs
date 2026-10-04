using System.CommandLine;
using Swarm.Git;

namespace Swarm.RunState.Cli;

/// <summary>Everything a command needs after option and config resolution.</summary>
/// <param name="Repo">The repository.</param>
/// <param name="Config">Validated config with flags applied.</param>
/// <param name="State">State layout.</param>
/// <param name="Verbosity">Progress level.</param>
public sealed record ToolContext(RepoPaths Repo, SwarmConfig Config, StateLayout State, Verbosity Verbosity);

/// <summary>Options shared by testgate and batch commands.</summary>
public sealed class CommonOptions
{
    /// <summary>Gets <c>--config</c>.</summary>
    public Option<string?> ConfigOption { get; } = new("--config") { Description = "Config file (default: <main worktree>/.swarm/batch.json)" };

    /// <summary>Gets <c>--state</c>.</summary>
    public Option<string?> StateOption { get; } = new("--state") { Description = "Run-state directory (default: config stateDir, .docs/runs in the main worktree)" };

    /// <summary>Gets <c>--slots</c>.</summary>
    public Option<int?> SlotsOption { get; } = new("--slots") { Description = "Concurrent test slots on this machine" };

    /// <summary>Gets <c>--max-wait</c>.</summary>
    public Option<int?> MaxWaitOption { get; } = new("--max-wait") { Description = "Seconds to wait for a slot before exit 5 (0 = forever)" };

    /// <summary>Gets <c>--verbosity</c>.</summary>
    public Option<Verbosity> VerbosityOption { get; } = new("--verbosity") { Description = "quiet, normal or detail", DefaultValueFactory = _ => Verbosity.Normal };

    /// <summary>Adds the shared options to a command.</summary>
    /// <param name="command">The command.</param>
    public void AddTo(Command command)
    {
        command.Options.Add(ConfigOption);
        command.Options.Add(StateOption);
        command.Options.Add(SlotsOption);
        command.Options.Add(MaxWaitOption);
        command.Options.Add(VerbosityOption);
    }

    /// <summary>Locates the repo, loads config (file, then flags) and resolves the state dir.</summary>
    /// <param name="parse">The parse result.</param>
    /// <param name="currentDirectory">Directory relative flag paths are based on.</param>
    /// <param name="extra">Command-specific overrides.</param>
    /// <returns>The context.</returns>
    /// <exception cref="ToolException">Not in a repo (3), bad config or path (2).</exception>
    public ToolContext Resolve(ParseResult parse, string currentDirectory, ConfigOverrides extra)
    {
        var repo = RepoLocator.Locate(currentDirectory);
        string? Absolute(string? p) => p is null ? null : Path.GetFullPath(Path.Combine(currentDirectory, p));
        var overrides = extra with
        {
            Slots = parse.GetValue(SlotsOption) ?? extra.Slots,
            MaxWaitSec = parse.GetValue(MaxWaitOption) ?? extra.MaxWaitSec,
            StateDir = Absolute(parse.GetValue(StateOption)) ?? extra.StateDir,
        };
        var config = ConfigLoader.Load(repo, Absolute(parse.GetValue(ConfigOption)), overrides);
        return new ToolContext(repo, config, new StateLayout(StatePaths.Resolve(repo, config.StateDir)), parse.GetValue(VerbosityOption));
    }
}
