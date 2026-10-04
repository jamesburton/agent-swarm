using System.CommandLine;
using Swarm.Git;

namespace Swarm.RunState.Cli;

/// <summary>Runs a System.CommandLine root with swarm conventions: one-line errors, exit 2 for usage.</summary>
public static class CliHost
{
    /// <summary>Parses and invokes.</summary>
    /// <param name="root">The root command.</param>
    /// <param name="args">Arguments.</param>
    /// <param name="stdout">Standard output (help, version, JSON).</param>
    /// <param name="stderr">Standard error.</param>
    /// <returns>The exit code.</returns>
    public static int Invoke(RootCommand root, string[] args, TextWriter stdout, TextWriter stderr)
    {
        // Response files off: an argument such as "@x" must reach the gated command untouched.
        var parse = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
        if (parse.Errors.Count > 0)
        {
            // "Unrecognized command or argument" names the offender; "Required command was not provided" would hide it.
            var error = parse.Errors.FirstOrDefault(e => e.Message.StartsWith("Unrecognized", StringComparison.Ordinal)) ?? parse.Errors[0];
            stderr.WriteLine(ToolException.Format(error.Message, "see --help"));
            return ExitCodes.Usage;
        }

        return ToolErrors.Handle(
            () => parse.Invoke(new InvocationConfiguration { Output = stdout, Error = stderr, EnableDefaultExceptionHandler = false }),
            stderr);
    }
}

/// <summary>
/// Cancels a token on the first Ctrl+C instead of killing the process, so slots are released and child trees killed;
/// a second Ctrl+C terminates the process.
/// </summary>
public sealed class CtrlCScope : IDisposable
{
    readonly CancellationTokenSource source = new();
    readonly ConsoleCancelEventHandler handler;

    /// <summary>Initializes a new instance of the <see cref="CtrlCScope"/> class.</summary>
    public CtrlCScope()
    {
        handler = (_, e) => e.Cancel = Signal();
        Console.CancelKeyPress += handler;
    }

    /// <summary>Gets the token cancelled by Ctrl+C.</summary>
    public CancellationToken Token => source.Token;

    /// <summary>
    /// Handles one Ctrl+C (the console handler calls this). The first press cancels the token and keeps the process
    /// alive so it can release slots and kill child trees; a second press is left to the default handling, which
    /// terminates the process (a way out of a step that does not observe the token).
    /// </summary>
    /// <returns>True when the process should keep running.</returns>
    public bool Signal()
    {
        if (source.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ctrl+C raced Dispose on the signal thread: the scope is over, nothing is left to cancel.
        }

        return true;
    }

    /// <summary>Unhooks the handler.</summary>
    public void Dispose()
    {
        Console.CancelKeyPress -= handler;
        source.Dispose();
    }
}
