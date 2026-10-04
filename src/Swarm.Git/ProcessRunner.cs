using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Swarm.Git;

/// <summary>Result of a finished (or killed) process.</summary>
/// <param name="ExitCode">The exit code, or -1 when the process was killed.</param>
/// <param name="StdOut">Captured stdout, LF-separated.</param>
/// <param name="StdErr">Captured stderr, LF-separated.</param>
/// <param name="Killed">True when the process tree was killed by a timeout.</param>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool Killed);

/// <summary>Options for <see cref="ProcessRunner.Run"/>.</summary>
public sealed record ProcessRunOptions
{
    /// <summary>Gets the run time limit; the whole process tree is killed when exceeded.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Gets a callback for every stdout and stderr line (called under one lock, keep it cheap).</summary>
    public Action<string>? OnLine { get; init; }

    /// <summary>Gets extra environment variables (null value removes the variable).</summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; init; }

    /// <summary>Gets how long to wait for output EOF after exit (a grandchild may still hold the pipes).</summary>
    public TimeSpan OutputGrace { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Runs child processes with closed stdin, captured output, and whole-tree kill.</summary>
public static class ProcessRunner
{
    /// <summary>Resolves a command name to a path. On Windows, bare names are searched on PATH with PATHEXT, so <c>npm</c> finds <c>npm.cmd</c>.</summary>
    /// <param name="fileName">A bare name, a relative path or an absolute path.</param>
    /// <param name="workingDirectory">Base for relative paths (default: current directory).</param>
    /// <param name="pathEnv">PATH override (tests).</param>
    /// <param name="pathExt">PATHEXT override (tests).</param>
    /// <returns>The resolved path, or <paramref name="fileName"/> unchanged when nothing was found.</returns>
    public static string Resolve(string fileName, string? workingDirectory = null, string? pathEnv = null, string? pathExt = null)
    {
        if (Path.IsPathFullyQualified(fileName))
        {
            return fileName;
        }

        if (fileName.Contains('/') || fileName.Contains('\\'))
        {
            return Path.GetFullPath(Path.Combine(workingDirectory ?? Directory.GetCurrentDirectory(), fileName));
        }

        if (!OperatingSystem.IsWindows())
        {
            return fileName;
        }

        var extensions = Path.HasExtension(fileName)
            ? new[] { "" }
            : (pathExt ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in (pathEnv ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, fileName + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return fileName;
    }

    /// <summary>Runs a process to completion.</summary>
    /// <param name="fileName">Command (resolved with <see cref="Resolve"/>).</param>
    /// <param name="args">Arguments, passed without shell interpretation.</param>
    /// <param name="workingDirectory">Working directory.</param>
    /// <param name="options">Timeout, line callback, environment, output grace.</param>
    /// <param name="cancellationToken">Cancels the run: the whole tree is killed first.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ToolException">The process cannot be started (exit code 4).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static ProcessResult Run(string fileName, IReadOnlyList<string> args, string workingDirectory, ProcessRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ProcessRunOptions();
        var psi = new ProcessStartInfo(Resolve(fileName, workingDirectory))
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var (key, value) in options.EnvironmentVariables ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                psi.Environment.Remove(key);
            }
            else
            {
                psi.Environment[key] = value;
            }
        }

        var gate = new object();
        var closed = false;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Collect(string? line, StringBuilder sink, TaskCompletionSource done)
        {
            if (line is null)
            {
                done.TrySetResult();
                return;
            }

            lock (gate)
            {
                // Late lines from an orphan still holding the pipes must not reach the caller after Run has returned.
                if (closed)
                {
                    return;
                }

                sink.Append(line).Append('\n');
                options.OnLine?.Invoke(line);
            }
        }

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Collect(e.Data, stdout, outDone);
        process.ErrorDataReceived += (_, e) => Collect(e.Data, stderr, errDone);
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new ToolException(ExitCodes.Environment, $"cannot start '{fileName}': {e.Message}", "check the command exists on PATH");
        }

        try
        {
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var clock = Stopwatch.StartNew();
            var killed = false;

            // Poll instead of WaitForExit(): the parameterless overload also waits for pipe EOF, which a
            // grandchild (e.g. an MSBuild node) can hold open forever.
            while (!process.WaitForExit(100))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    KillTree(process);
                    process.WaitForExit(5000);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (options.Timeout is { } limit && clock.Elapsed > limit)
                {
                    KillTree(process);
                    killed = true;
                    process.WaitForExit(5000);
                    break;
                }
            }

            Task.WaitAll([outDone.Task, errDone.Task], options.OutputGrace);
            lock (gate)
            {
                return new ProcessResult(killed ? -1 : process.ExitCode, stdout.ToString(), stderr.ToString(), killed);
            }
        }
        finally
        {
            lock (gate)
            {
                closed = true;
            }
        }
    }

    static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Already exited.
        }
    }
}
