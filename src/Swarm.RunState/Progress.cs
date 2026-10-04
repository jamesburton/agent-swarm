using Swarm.Git;

namespace Swarm.RunState;

/// <summary>How much progress text goes to stderr.</summary>
public enum Verbosity
{
    /// <summary>Errors and warnings only.</summary>
    Quiet,

    /// <summary>One line per step.</summary>
    Normal,

    /// <summary>Also command output.</summary>
    Detail,
}

/// <summary>Human progress on stderr (stdout is reserved for the JSON result).</summary>
/// <param name="stderr">Destination.</param>
/// <param name="verbosity">Level.</param>
public sealed class Progress(TextWriter stderr, Verbosity verbosity)
{
    /// <summary>Writes a step line at normal verbosity.</summary>
    /// <param name="line">The line.</param>
    public void Info(string line)
    {
        if (verbosity >= Verbosity.Normal)
        {
            stderr.WriteLine(line);
        }
    }

    /// <summary>Writes a detail line (command output) at detail verbosity.</summary>
    /// <param name="line">The line.</param>
    public void Detail(string line)
    {
        if (verbosity >= Verbosity.Detail)
        {
            stderr.WriteLine(line);
        }
    }

    /// <summary>Writes a one-line warning at any verbosity.</summary>
    /// <param name="line">The warning.</param>
    public void Warn(string line) => stderr.WriteLine("warning: " + TextLines.OneLine(line));
}
