using System.CommandLine;
using Swarm.Git;
using Swarm.RunState;
using Swarm.RunState.Cli;
using Swarm.Squashing;

namespace Swarm.Squash.Cli;

/// <summary>The <c>squash</c> tool: lands one task branch on its epic as one trailer-stamped commit.</summary>
public static class Program
{
    /// <summary>Process entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error, Directory.GetCurrentDirectory());

    /// <summary>Runs the tool (testable entry point).</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="stdout">Receives exactly one JSON line.</param>
    /// <param name="stderr">Receives progress and the one-line error.</param>
    /// <param name="currentDirectory">Directory treated as the current directory.</param>
    /// <returns>The exit code.</returns>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory)
    {
        var common = new CommonOptions();
        var task = new Option<string>("--task") { Description = "Task id (a safe name; recorded in the Task: trailer)", Required = true };
        var branch = new Option<string>("--branch") { Description = "Task branch to squash (never modified)", Required = true };
        var epic = new Option<string?>("--epic") { Description = "Epic id (default: config epic); the branch name comes from epicBranchTemplate" };
        var ticket = new Option<string?>("--ticket") { Description = "Ticket for the Ticket: trailer (default: derived with squash.ticketPattern)" };
        var runId = new Option<string?>("--run-id") { Description = "Run id for the Swarm-Run: trailer (default: squash-<utc timestamp>-<epic>)" };
        var run = new Command("run", "Squash one task branch onto its epic branch as one trailer-stamped commit; prints one JSON line")
        {
            task, branch, epic, ticket, runId,
        };
        common.AddTo(run);
        run.SetAction(p =>
        {
            var ctx = common.Resolve(p, currentDirectory, new ConfigOverrides { Epic = p.GetValue(epic) });
            var request = new SquashRunRequest(p.GetValue(task)!, p.GetValue(branch)!, p.GetValue(ticket), p.GetValue(runId));
            var result = new SquashRunner(ctx, new Progress(stderr, ctx.Verbosity)).Run(request);
            stdout.WriteLine(SwarmJson.Line(result));
            return result.ExitCode;
        });

        var root = new RootCommand("squash - land a task branch on its epic as one trailer-stamped commit") { run };
        return CliHost.Invoke(root, args, stdout, stderr);
    }
}
