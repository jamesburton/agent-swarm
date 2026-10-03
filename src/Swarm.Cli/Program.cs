using System.Reflection;
using Swarm.Core;
using Swarm.Formats;
using Swarm.Render;

namespace Swarm.Cli;

/// <summary>The <c>swarm</c> command-line tool.</summary>
public static class Program
{
    const string Usage = """
        swarm - validate a swarm definition and render Claude Code agents and workflows

        Usage:
          swarm validate <file>
          swarm render <file> --out <dir> [--force]
          swarm --version
          swarm --help

        <file> is a .md, .yaml or .yml swarm definition (at most 1 MB).
        validate checks everything render checks, without writing anything.
        render refuses to overwrite a file that lacks the swarm:generated marker
        (a hand-written file) unless --force is given.
        Exit codes: 0 ok, 1 invalid definition, 2 usage or I/O error.
        """;

    const long MaxInputBytes = 1024 * 1024;

    const string RestartNote = "Note: generated agent files are only visible to a Claude Code session started after they exist; restart or open a new session.";

    sealed class UsageException(string message) : Exception(message);

    /// <summary>Process entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The exit code.</returns>
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>Runs the tool.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="stdout">Standard output.</param>
    /// <param name="stderr">Standard error.</param>
    /// <returns>0 ok, 1 invalid definition, 2 usage or I/O error.</returns>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            return Dispatch(args, stdout);
        }
        catch (SwarmException e)
        {
            return Fail(stderr, 1, e.Message);
        }
        catch (UsageException e)
        {
            return Fail(stderr, 2, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or System.Security.SecurityException)
        {
            return Fail(stderr, 2, e.Message);
        }
        catch (Exception e)
        {
            return Fail(stderr, 2, $"unexpected failure: {e.GetType().Name}: {e.Message}");
        }
    }

    static int Fail(TextWriter stderr, int code, string message)
    {
        stderr.WriteLine($"error: {message.ReplaceLineEndings(" ").Trim()}");
        return code;
    }

    static int Dispatch(string[] args, TextWriter stdout)
    {
        if (args.Length == 0)
        {
            throw new UsageException("missing command; expected 'validate' or 'render' (see --help)");
        }

        if (args.Any(a => a is "--help" or "-h"))
        {
            stdout.WriteLine(Usage);
            return 0;
        }

        if (args.Contains("--version"))
        {
            stdout.WriteLine(Version());
            return 0;
        }

        if (args.Contains("--"))
        {
            throw new UsageException("a bare '--' is not supported (see --help)");
        }

        switch (args[0])
        {
            case "validate":
                return Validate(Parse(args, withOut: false), stdout);
            case "render":
                return RenderCommand(Parse(args, withOut: true), stdout);
            default:
                throw new UsageException($"unknown command '{args[0]}' (see --help)");
        }
    }

    static string Version()
    {
        var v = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
        var plus = v.IndexOf('+');
        return plus < 0 ? v : v[..plus];
    }

    static (string File, string? Out, bool Force) Parse(string[] args, bool withOut)
    {
        string? file = null;
        string? outDir = null;
        var force = false;
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (withOut && a == "--out")
            {
                if (outDir is not null)
                {
                    throw new UsageException("--out was given more than once");
                }

                outDir = i + 1 < args.Length && !args[i + 1].StartsWith('-')
                    ? args[++i]
                    : throw new UsageException("--out requires a directory");
                if (string.IsNullOrWhiteSpace(outDir))
                {
                    throw new UsageException("--out must not be empty");
                }
            }
            else if (withOut && a == "--force")
            {
                force = !force ? true : throw new UsageException("--force was given more than once");
            }
            else if (a.StartsWith('-'))
            {
                throw new UsageException($"unknown option '{a}' (see --help)");
            }
            else
            {
                file = file is null ? a : throw new UsageException($"unexpected extra argument '{a}'");
            }
        }

        if (file is null)
        {
            throw new UsageException($"missing <file> argument for '{args[0]}'");
        }

        if (withOut && outDir is null)
        {
            throw new UsageException("missing required option --out <dir>");
        }

        return (file, outDir, force);
    }

    static SwarmDefinition Load(string file)
    {
        Func<string, SwarmDefinition> parse = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".md" => MarkdownFrontEnd.Parse,
            ".yaml" or ".yml" => YamlFrontEnd.Parse,
            _ => throw new UsageException($"unsupported file extension '{Path.GetExtension(file)}'; expected .md, .yaml or .yml"),
        };
        if (!File.Exists(file))
        {
            throw new UsageException($"file not found: {file}");
        }

        if (new FileInfo(file).Length > MaxInputBytes)
        {
            throw new UsageException($"file is larger than 1 MB: {file}");
        }

        return parse(File.ReadAllText(file));
    }

    static int Validate((string File, string? Out, bool Force) a, TextWriter stdout)
    {
        // Rendering in memory (and discarding the result) makes "ok" mean "render will succeed".
        SwarmRenderer.Render(Load(a.File));
        stdout.WriteLine("ok");
        return 0;
    }

    static int RenderCommand((string File, string? Out, bool Force) a, TextWriter stdout)
    {
        var def = Load(a.File);

        // Render everything in memory first so any failure writes nothing.
        var files = SwarmRenderer.Render(def);
        if (File.Exists(a.Out!))
        {
            throw new UsageException($"--out is a file, not a directory: {a.Out}");
        }

        foreach (var f in OutputPaths.WriteAll(a.Out!, files, a.Force))
        {
            stdout.WriteLine($"{(f.Overwrote ? "overwrote" : "wrote")} {f.Path}");
        }

        stdout.WriteLine(RestartNote);
        return 0;
    }
}
