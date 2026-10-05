using System.CommandLine;
using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.RunState.Cli;

namespace Swarm.Worktree.Cli;

/// <summary>The <c>worktree</c> tool: per-task git worktrees branched from an epic branch.</summary>
public static class Program
{
    /// <summary>Process entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error, Directory.GetCurrentDirectory());

    /// <summary>Runs the tool (testable entry point).</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="stdout">Receives exactly one JSON line per command.</param>
    /// <param name="stderr">Receives warnings and the one-line error.</param>
    /// <param name="currentDirectory">Directory treated as the current directory.</param>
    /// <returns>The exit code.</returns>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory)
    {
        var common = new CommonOptions();
        var epic = new Option<string?>("--epic") { Description = "Epic id (its branch is read from <state>/epics/<id>.json)" };
        var baseBranch = new Option<string?>("--base") { Description = "Base branch name, instead of --epic" };

        var ticket = new Argument<string>("ticket") { Description = "Ticket id, e.g. 9933" };
        var slug = new Argument<string>("slug") { Description = "Lowercase hyphenated slug, e.g. login-form" };
        var kind = new Option<string?>("--kind") { Description = "Value for {kind} in worktree.branchTemplate (e.g. feature, bugfix)" };
        var create = new Command("create", "Create a task worktree on a new branch from the epic branch; prints one JSON line") { ticket, slug, epic, baseBranch, kind };
        common.AddTo(create);
        create.SetAction(p =>
        {
            var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
            var manager = new WorktreeManager(ctx.Repo, ctx.Config);
            var from = BaseOption.Resolve(p, epic, baseBranch, manager.State, required: true, allowClosed: false)!;
            var result = manager.Create(new CreateRequest(p.GetValue(ticket)!, p.GetValue(slug)!, from, p.GetValue(kind)));
            var progress = new Progress(stderr, ctx.Verbosity);
            foreach (var w in result.Warnings)
            {
                progress.Warn(w);
            }

            stdout.WriteLine(SwarmJson.Line(result));
            return ExitCodes.Ok;
        });

        var all = new Option<bool>("--all") { Description = "Also list worktrees this tool did not create" };
        var list = new Command("list", "List task worktrees with dirty/empty/merged/locked state; prints one JSON line") { epic, baseBranch, all };
        common.AddTo(list);
        list.SetAction(p =>
        {
            var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
            var manager = new WorktreeManager(ctx.Repo, ctx.Config);
            var filter = BaseOption.Resolve(p, epic, baseBranch, manager.State, required: false, allowClosed: true);
            stdout.WriteLine(SwarmJson.Line(new WorktreeListResult(SwarmJson.SchemaVersion, manager.Root, manager.List(filter, p.GetValue(all)))));
            return ExitCodes.Ok;
        });

        var dryRun = new Option<bool>("--dry-run") { Description = "Report what would be removed; change nothing" };
        var force = new Option<bool>("--force") { Description = "Also remove unmerged, dirty and empty worktrees (never locked ones, never a stale registration whose directory still exists)" };
        var prune = new Command("prune", "Remove merged task worktrees and their branches; prints one JSON line") { epic, baseBranch, dryRun, force };
        common.AddTo(prune);
        prune.SetAction(p =>
        {
            var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
            var manager = new WorktreeManager(ctx.Repo, ctx.Config);
            var filter = BaseOption.Resolve(p, epic, baseBranch, manager.State, required: false, allowClosed: true);
            var report = new Pruner(manager).Prune(filter, p.GetValue(dryRun), p.GetValue(force));
            stdout.WriteLine(SwarmJson.Line(report));
            if (report.Failed == 0)
            {
                return ExitCodes.Ok;
            }

            stderr.WriteLine(ToolException.Format($"{report.Failed} worktree(s) could not be pruned", "see items[].error; a process may hold files there"));
            return ExitCodes.Environment;
        });

        var root = new RootCommand("worktree - per-task git worktrees from an epic branch") { create, list, prune };
        return CliHost.Invoke(root, args, stdout, stderr);
    }
}

/// <summary>Resolves <c>--epic</c> / <c>--base</c> to a branch name.</summary>
internal static class BaseOption
{
    /// <summary>Resolves the base branch.</summary>
    /// <param name="p">Parse result.</param>
    /// <param name="epic">The <c>--epic</c> option.</param>
    /// <param name="baseBranch">The <c>--base</c> option.</param>
    /// <param name="state">State layout (epic records).</param>
    /// <param name="required">Whether one of the two must be given.</param>
    /// <param name="allowClosed">Whether a closed epic is acceptable.</param>
    /// <returns>The branch, or null when neither was given and none is required.</returns>
    /// <exception cref="ToolException">Both or (when required) neither given (2); unknown or closed epic (3).</exception>
    public static string? Resolve(ParseResult p, Option<string?> epic, Option<string?> baseBranch, StateLayout state, bool required, bool allowClosed)
    {
        var (id, name) = (p.GetValue(epic), p.GetValue(baseBranch));
        if (id is not null && name is not null)
        {
            throw new ToolException(ExitCodes.Usage, "pass only one of --epic or --base");
        }

        if (id is null)
        {
            return name ?? (required ? throw new ToolException(ExitCodes.Usage, "pass --epic <id> or --base <branch>") : null);
        }

        var record = new EpicStore(state).Get(id);
        return allowClosed || record.State == EpicStates.Open
            ? record.Branch
            : throw new ToolException(ExitCodes.BadInput, $"epic '{id}' is closed", "open a new epic for new work");
    }
}
