using System.CommandLine;
using System.Globalization;
using Swarm.Gate;
using Swarm.Git;
using Swarm.RunState;
using Swarm.RunState.Cli;

namespace Swarm.TestGate.Cli;

/// <summary>The <c>testgate</c> tool: a machine-wide counting semaphore for expensive test runs.</summary>
public static class Program
{
    /// <summary>Process entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error, Directory.GetCurrentDirectory());

    /// <summary>Runs the tool (testable entry point).</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="stdout">Receives exactly one JSON line per command.</param>
    /// <param name="stderr">Receives progress, child output and the one-line error.</param>
    /// <param name="currentDirectory">Directory treated as the current directory.</param>
    /// <returns>The exit code.</returns>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory)
    {
        var common = new CommonOptions();

        var cwd = new Option<string?>("--cwd") { Description = "Directory to run the command in (default: current directory)" };
        var label = new Option<string?>("--label") { Description = "Label recorded in the result (default: testgate)" };
        var command = new Argument<string[]>("command") { Arity = ArgumentArity.ZeroOrMore, Description = "Command after --; default: config testCommand" };
        var run = new Command("run", "Wait for a free test slot, run a command, release the slot; prints one JSON line") { cwd, label, command };
        common.AddTo(run);
        run.SetAction(p => RunGate(p, common, cwd, label, command, stdout, stderr, currentDirectory));

        var status = new Command("status", "List slot holders and heartbeat ages as one JSON line");
        common.AddTo(status);
        status.SetAction(p => Status(p, common, stdout, stderr, currentDirectory));

        var force = new Option<bool>("--force") { Description = "Confirm deleting stale or dead-holder lock files" };
        var reclaim = new Command("reclaim", "Delete stale or dead-holder slot locks (never a live local holder)") { force };
        common.AddTo(reclaim);
        reclaim.SetAction(p => Reclaim(p, common, force, stdout, currentDirectory));

        var root = new RootCommand("testgate - run expensive commands under a machine-wide slot limit") { run, status, reclaim };
        return CliHost.Invoke(root, args, stdout, stderr);
    }

    static SlotSemaphore Slots(ToolContext ctx) => new(ctx.State.SlotsDir, SlotOptions.From(ctx.Config));

    static int RunGate(ParseResult p, CommonOptions common, Option<string?> cwd, Option<string?> label, Argument<string[]> command, TextWriter stdout, TextWriter stderr, string currentDirectory)
    {
        var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
        var cmd = p.GetValue(command) is { Length: > 0 } given ? given : ctx.Config.TestCommand.ToArray();

        // System.CommandLine binds an unknown "--flag" to the command argument; only tokens after "--" are a command.
        if (cmd.Length > 0 && cmd[0].StartsWith('-'))
        {
            throw new ToolException(ExitCodes.Usage, $"unknown option '{cmd[0]}'", "put the command after --");
        }

        var workDir = Path.GetFullPath(Path.Combine(currentDirectory, p.GetValue(cwd) ?? "."));
        var progress = new Progress(stderr, ctx.Verbosity);
        using var ctrlC = new CtrlCScope();
        var gate = new GateRunner(Slots(ctx), progress.Info).Run(new GateRequest(cmd, workDir, p.GetValue(label) ?? "testgate"), ctrlC.Token);
        new EventLog(ctx.State.GateEventsFile, "testgate").Write(EventTypes.Gate, gate.Result);
        stdout.WriteLine(SwarmJson.Line(gate.Result));
        return gate.Result.ExitCode == 0 ? ExitCodes.Ok : ExitCodes.Returned;
    }

    static int Status(ParseResult p, CommonOptions common, TextWriter stdout, TextWriter stderr, string currentDirectory)
    {
        var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
        var slots = Slots(ctx);
        var holders = slots.Status();
        var progress = new Progress(stderr, ctx.Verbosity);
        foreach (var h in holders)
        {
            var pid = h.Info?.Pid.ToString(CultureInfo.InvariantCulture) ?? "?";
            progress.Info($"slot {h.Slot}: pid {pid} on {h.Info?.Host ?? "?"}, heartbeat {h.HeartbeatAgeSec} s ago{(h.Stale ? " (STALE)" : "")}: {h.Info?.Command ?? "(unreadable)"}");
        }

        stdout.WriteLine(SwarmJson.Line(new GateStatus(SwarmJson.SchemaVersion, slots.LockDir, ctx.Config.Slots, holders)));
        return ExitCodes.Ok;
    }

    static int Reclaim(ParseResult p, CommonOptions common, Option<bool> force, TextWriter stdout, string currentDirectory)
    {
        if (!p.GetValue(force))
        {
            throw new ToolException(ExitCodes.Usage, "reclaim deletes lock files", "pass --force to confirm");
        }

        var ctx = common.Resolve(p, currentDirectory, ConfigOverrides.None);
        stdout.WriteLine(SwarmJson.Line(Slots(ctx).Reclaim()));
        return ExitCodes.Ok;
    }
}
