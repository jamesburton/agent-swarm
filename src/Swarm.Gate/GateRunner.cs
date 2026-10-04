using System.Diagnostics;
using System.Text;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Gate;

/// <summary>A command to run under a test slot.</summary>
/// <param name="Command">Program and arguments.</param>
/// <param name="WorkingDirectory">Where to run it.</param>
/// <param name="Label">Label recorded in the result.</param>
public sealed record GateRequest(IReadOnlyList<string> Command, string WorkingDirectory, string Label);

/// <summary>A finished gated run.</summary>
/// <param name="Result">Timings, slot and exit code.</param>
/// <param name="Log">Combined stdout and stderr in arrival order.</param>
public sealed record GateRun(GateResult Result, string Log);

/// <summary>Acquires a slot, runs a command, always releases the slot.</summary>
/// <param name="slots">The slot semaphore.</param>
/// <param name="onOutputLine">Optional live output callback.</param>
public sealed class GateRunner(SlotSemaphore slots, Action<string>? onOutputLine = null)
{
    /// <summary>Runs one command under a slot.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels waiting or running (the process tree is killed).</param>
    /// <returns>The run.</returns>
    /// <exception cref="ToolException">Empty command or missing directory (2), cannot start (4), no slot in time (5).</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public GateRun Run(GateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Command.Count == 0)
        {
            throw new ToolException(ExitCodes.Usage, "no command to run", "pass it after --, or set testCommand in .swarm/batch.json");
        }

        if (!Directory.Exists(request.WorkingDirectory))
        {
            throw new ToolException(ExitCodes.Usage, $"working directory '{request.WorkingDirectory}' does not exist");
        }

        var log = new StringBuilder();
        var lease = slots.Acquire(string.Join(' ', request.Command), cancellationToken);
        ProcessResult result;
        long runMs;
        try
        {
            var clock = Stopwatch.StartNew();
            result = ProcessRunner.Run(
                request.Command[0],
                request.Command.Skip(1).ToList(),
                request.WorkingDirectory,
                new ProcessRunOptions { OnLine = line => { log.Append(line).Append('\n'); onOutputLine?.Invoke(line); } },
                cancellationToken);
            runMs = clock.ElapsedMilliseconds;
        }
        finally
        {
            lease.Dispose();
        }

        // The child can exit (often non-zero, from the same signal) before the runner polls the token: a cancelled run
        // is reported as cancelled, never as the command's own failure.
        cancellationToken.ThrowIfCancellationRequested();

        var gate = new GateResult(SwarmJson.SchemaVersion, request.Label, (long)lease.Waited.TotalMilliseconds, runMs, lease.Slot, result.ExitCode, lease.Reclaimed, result.Killed, lease.AcquiredUtc, DateTime.UtcNow);
        return new GateRun(gate, log.ToString());
    }
}
