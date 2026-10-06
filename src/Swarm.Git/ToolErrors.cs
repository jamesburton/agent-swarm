namespace Swarm.Git;

/// <summary>Turns any failure into one stderr line and an exit code.</summary>
public static class ToolErrors
{
    /// <summary>Runs a command body and maps failures to exit codes.</summary>
    /// <param name="body">The command body; returns the exit code on success.</param>
    /// <param name="stderr">Where the single error line goes.</param>
    /// <returns>The body's exit code, or the code for the failure.</returns>
    public static int Handle(Func<int> body, TextWriter stderr)
    {
        try
        {
            return body();
        }
        catch (ToolException e)
        {
            stderr.WriteLine(e.ErrorLine);
            return e.ExitCode;
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine(ToolException.Format("cancelled"));
            return ExitCodes.Environment;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine(ToolException.Format(e.Message, "file system error"));
            return ExitCodes.Environment;
        }
        catch (Exception e)
        {
            stderr.WriteLine(ToolException.Format($"unexpected failure: {e.GetType().Name}: {e.Message}"));
            return ExitCodes.Environment;
        }
    }
}
