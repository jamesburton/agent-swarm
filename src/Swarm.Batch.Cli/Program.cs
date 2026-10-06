using System.CommandLine;
using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;
using Swarm.RunState.Cli;
using Swarm.Squashing;

namespace Swarm.Batch.Cli;

/// <summary>The <c>batch</c> tool: batched integration testing with bisect, landing green tasks on the epic branch.</summary>
public static class Program
{
    /// <summary>Process entry point: the lander comes from config (<c>lander</c>, default <c>squash</c>).</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error, Directory.GetCurrentDirectory(), Landers.Create);

    /// <summary>Runs the tool with a fixed lander (testable entry point, Plan A).</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="stdout">Receives exactly one JSON summary line.</param>
    /// <param name="stderr">Receives progress and the one-line error.</param>
    /// <param name="currentDirectory">Directory treated as the current directory.</param>
    /// <param name="lander">How green tasks land on the epic.</param>
    /// <returns>The exit code.</returns>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory, ILander lander) =>
        Run(args, stdout, stderr, currentDirectory, _ => lander);

    /// <summary>Runs the tool, choosing the lander from the resolved config.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="stdout">Receives exactly one JSON summary line.</param>
    /// <param name="stderr">Receives progress and the one-line error.</param>
    /// <param name="currentDirectory">Directory treated as the current directory.</param>
    /// <param name="landerFor">Creates the lander from the validated config.</param>
    /// <returns>The exit code.</returns>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory, Func<SwarmConfig, ILander> landerFor)
    {
        var common = new CommonOptions();
        var tasks = new Argument<string>("tasks") { Description = "Tasks file: JSON array of { id, branch, dependsOn? } in queue order" };
        var epic = new Option<string?>("--epic") { Description = "Epic id (overrides config epic)" };
        var runId = new Option<string?>("--run-id") { Description = "Run id (default: <utc timestamp>-<epic>)" };
        var start = new Option<int?>("--start") { Description = "First batch size" };
        var min = new Option<int?>("--min") { Description = "Smallest batch size" };
        var max = new Option<int?>("--max") { Description = "Largest batch size" };
        var mode = new Option<string>("--mode") { Description = "batched (default) or serial (one task per suite, the baseline)", DefaultValueFactory = _ => BatchModes.Batched };
        mode.AcceptOnlyFromAmong(BatchModes.Batched, BatchModes.Serial);
        var noPrebatch = new Option<bool>("--experimental-no-prebatch") { Description = "Do not keep same-file tasks in separate batches" };
        var fixedSize = new Option<int?>("--experimental-fixed") { Description = "Fixed batch size (start = min = max)" };

        var run = new Command("run", "Merge task branches into the integration worktree, run the full suite once per batch, bisect red batches, land green tasks")
        {
            tasks, epic, runId, start, min, max, mode, noPrebatch, fixedSize,
        };
        common.AddTo(run);
        run.SetAction(p =>
        {
            if (p.GetValue(fixedSize) is < 1 or > 64)
            {
                throw new ToolException(ExitCodes.Usage, "--experimental-fixed must be between 1 and 64");
            }

            var ctx = common.Resolve(p, currentDirectory, new ConfigOverrides { Epic = p.GetValue(epic), Start = p.GetValue(start), Min = p.GetValue(min), Max = p.GetValue(max) });
            var options = new BatchRunOptions
            {
                TasksFile = Path.GetFullPath(Path.Combine(currentDirectory, p.GetValue(tasks)!)),
                Config = ctx.Config,
                RunId = p.GetValue(runId),
                Mode = p.GetValue(mode)!,
                Prebatch = p.GetValue(noPrebatch) ? false : null,
                FixedSize = p.GetValue(fixedSize),
            };
            var lander = landerFor(ctx.Config);
            using var ctrlC = new CtrlCScope();
            var summary = new BatchEngine(ctx.Repo, options, lander, new Progress(stderr, ctx.Verbosity)).Run(ctrlC.Token);
            stdout.WriteLine(SwarmJson.Line(summary));
            if (summary.ExitCode > ExitCodes.Returned && summary.Note is { } note)
            {
                stderr.WriteLine("error: " + note);
            }

            return summary.ExitCode;
        });

        var root = new RootCommand("batch - adaptive batched integration testing with bisect for agent swarms") { run };
        return CliHost.Invoke(root, args, stdout, stderr);
    }
}
