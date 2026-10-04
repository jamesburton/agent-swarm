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

/// <summary>Cancels a token on Ctrl+C instead of killing the process, so slots are released and child trees killed.</summary>
public sealed class CtrlCScope : IDisposable
{
    readonly CancellationTokenSource source = new();
    readonly ConsoleCancelEventHandler handler;

    /// <summary>Initializes a new instance of the <see cref="CtrlCScope"/> class.</summary>
    public CtrlCScope()
    {
        handler = (_, e) =>
        {
            e.Cancel = true;
            source.Cancel();
        };
        Console.CancelKeyPress += handler;
    }

    /// <summary>Gets the token cancelled by Ctrl+C.</summary>
    public CancellationToken Token => source.Token;

    /// <summary>Unhooks the handler.</summary>
    public void Dispose()
    {
        Console.CancelKeyPress -= handler;
        source.Dispose();
    }
}
