using System.CommandLine;
using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.RunState.Cli;

namespace Swarm.Epic.Cli;

/// <summary>The <c>epic</c> tool: open, report and close epic branches.</summary>
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
        var into = new Option<string?>("--into") { Description = "Target branch (default: the branch the epic was opened from)" };

        var id = new Argument<string>("id") { Description = "Epic id, e.g. 42 or 9933" };
        var slug = new Argument<string>("slug") { Description = "Lowercase hyphenated slug, e.g. auth" };
        var from = new Option<string?>("--from") { Description = "Base branch (default: config baseBranch)" };
        var kind = new Option<string?>("--kind") { Description = "Value for {kind} in epicTool.branchTemplate (e.g. feature, bugfix)" };
        var open = new Command("open", "Create the epic branch (no checkout) and its record; prints one JSON line") { id, slug, from, kind };
        common.AddTo(open);
        open.SetAction(p =>
        {
            var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
            var result = new EpicOpener(ctx.Repo, ctx.Config).Open(p.GetValue(id)!, p.GetValue(slug)!, p.GetValue(from), p.GetValue(kind));
            WarnAll(stderr, ctx.Verbosity, result.Warnings);
            stdout.WriteLine(SwarmJson.Line(result));
            return ExitCodes.Ok;
        });

        var optionalId = new Argument<string?>("id") { Arity = ArgumentArity.ZeroOrOne, Description = "Epic id (default: every epic)" };
        var status = new Command("status", "Report run state, open tasks, worktrees and close blockers; prints one JSON line") { optionalId, into };
        common.AddTo(status);
        status.SetAction(p =>
        {
            var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
            var store = new EpicStore(new WorktreeManager(ctx.Repo, ctx.Config).State);

            // Listing every epic: one unreadable record file is reported on its own (warning + `unreadable`), never failing the rest.
            var listing = p.GetValue(optionalId) is { } one ? new EpicListing([store.Get(one)], []) : store.All();
            WarnAll(stderr, ctx.Verbosity, listing.Unreadable.Select(u => $"epic file '{u.Path}' skipped: {u.Error}").ToList());
            var assessor = new EpicAssessor(ctx.Repo, ctx.Config);
            stdout.WriteLine(SwarmJson.Line(new EpicStatusList(SwarmJson.SchemaVersion, listing.Records.Select(e => assessor.Assess(e, p.GetValue(into))).ToList(), listing.Unreadable)));
            return ExitCodes.Ok;
        });

        var closeId = new Argument<string>("id") { Description = "Epic id" };
        var force = new Option<bool>("--force") { Description = "Waive waivable blockers (returned tasks, unfinished runs, unmerged worktrees)" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Check and print the merge message; change nothing" };
        var deleteBranch = new Option<bool>("--delete-branch")
        {
            Description = "Delete the epic branch after merging, only while it still points at the merged tip and is checked out nowhere (else kept, with a warning)",
        };
        var close = new Command("close", "Merge the epic --no-ff onto the active branch (never squashed); prints one JSON line") { closeId, into, force, dryRun, deleteBranch };
        common.AddTo(close);
        close.SetAction(p =>
        {
            var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
            var options = new CloseOptions(p.GetValue(into), p.GetValue(force), p.GetValue(dryRun), p.GetValue(deleteBranch));
            var r = new EpicCloser(ctx.Repo, ctx.Config).Close(p.GetValue(closeId)!, options);
            return ReportClose(r, stdout, stderr, ctx.Verbosity);
        });

        var root = new RootCommand("epic - open, report and close epic branches") { open, status, close };
        return CliHost.Invoke(root, args, stdout, stderr);
    }

    /// <summary>
    /// Prints an <c>epic close</c> result: the JSON line on stdout, then every warning on stderr, then (for
    /// <c>blocked</c> and <c>conflict</c>) the one-line error.
    /// </summary>
    /// <param name="r">The close result.</param>
    /// <param name="stdout">Receives the JSON line.</param>
    /// <param name="stderr">Receives the warnings and the error line.</param>
    /// <param name="verbosity">Verbosity for the warnings (warnings print at every verbosity).</param>
    /// <returns><see cref="ExitCodes.Returned"/> for <c>blocked</c> and <c>conflict</c>, else <see cref="ExitCodes.Ok"/>.</returns>
    internal static int ReportClose(EpicCloseResult r, TextWriter stdout, TextWriter stderr, Verbosity verbosity)
    {
        stdout.WriteLine(SwarmJson.Line(r));

        // Every result can carry warnings (e.g. a temporary worktree left behind after a conflict): print them all
        // before the one-line error, so nothing in r.Warnings is visible only in the JSON.
        WarnAll(stderr, verbosity, r.Warnings);
        switch (r.Result)
        {
            case CloseResults.Blocked:
                stderr.WriteLine(ToolException.Format($"epic '{r.Id}' not closed: {r.Blockers[0].Detail}", $"{r.Blockers.Count} blocker(s); see blockers"));
                return ExitCodes.Returned;
            case CloseResults.Conflict:
                stderr.WriteLine(ToolException.Format($"merging '{r.Branch}' into '{r.Into}' conflicts in {string.Join(", ", r.ConflictFiles)}", $"merge '{r.Into}' into the epic and resolve, then close again"));
                return ExitCodes.Returned;
            default:
                return ExitCodes.Ok;
        }
    }

    // One `warning: <w>` stderr line per warning (Progress.Warn prints at every verbosity).
    static void WarnAll(TextWriter stderr, Verbosity verbosity, IReadOnlyList<string> warnings)
    {
        var progress = new Progress(stderr, verbosity);
        foreach (var w in warnings)
        {
            progress.Warn(w);
        }
    }
}
