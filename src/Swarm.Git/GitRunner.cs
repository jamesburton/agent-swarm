namespace Swarm.Git;

/// <summary>Runs git with fixed, platform-safe configuration and one-line failures.</summary>
public sealed class GitRunner
{
    static readonly IReadOnlyDictionary<string, string?> Env = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_EDITOR"] = "true",
        ["GIT_MERGE_AUTOEDIT"] = "no",
    };

    static readonly string[] AutoCrlfOff = ["-c", "core.autocrlf=false"];
    static readonly string[] CommonConfig = ["-c", "core.longpaths=true", "-c", "core.quotepath=false", "-c", "advice.detachedHead=false"];

    readonly IReadOnlyList<string> extraConfig;
    readonly IReadOnlyDictionary<string, string?> environment;
    readonly bool repoLineEndings;

    /// <summary>Initializes a new instance of the <see cref="GitRunner"/> class.</summary>
    /// <param name="workingDirectory">Directory git runs in.</param>
    public GitRunner(string workingDirectory)
        : this(workingDirectory, [], Env, false)
    {
    }

    GitRunner(string workingDirectory, IReadOnlyList<string> extraConfig, IReadOnlyDictionary<string, string?> environment, bool repoLineEndings)
    {
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        this.extraConfig = extraConfig;
        this.environment = environment;
        this.repoLineEndings = repoLineEndings;
    }

    /// <summary>Gets the configuration passed to every git call (no CRLF conversion, long paths, unquoted names).</summary>
    public static IReadOnlyList<string> BaseConfig { get; } = [.. AutoCrlfOff, .. CommonConfig];

    /// <summary>
    /// Gets the committer identity used for tool-made commits (integration merges, rebased copies). Signing is off:
    /// the user's <c>commit.gpgSign</c> would make every tool commit fail (or wait on a pinentry prompt) as an
    /// identity the user's key does not belong to.
    /// </summary>
    public static IReadOnlyList<string> ToolIdentity { get; } =
        ["-c", "user.name=swarm-batch", "-c", "user.email=swarm-batch@example.invalid", "-c", "commit.gpgSign=false"];

    /// <summary>Gets the working directory.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Returns a runner that commits with <see cref="ToolIdentity"/> (the environment is kept).</summary>
    /// <returns>The new runner.</returns>
    public GitRunner WithIdentity() => new(WorkingDirectory, ToolIdentity, environment, repoLineEndings);

    /// <summary>
    /// Returns a runner that keeps the repository's own line-ending settings: it omits <c>-c core.autocrlf=false</c> from
    /// <see cref="BaseConfig"/> (the rest of it, the identity and the environment are kept, and <see cref="At"/> keeps it).
    /// Use it only where git judges or updates a working tree the user owns (dirty checks, a fast-forward of the user's
    /// checkout, a non-forced <c>worktree remove</c>): with the user's <c>core.autocrlf=true</c>, a clean file git checked
    /// out with CRLF reads as modified under <c>core.autocrlf=false</c>. Tool-owned worktrees keep the fixed setting.
    /// </summary>
    /// <returns>The new runner.</returns>
    public GitRunner WithRepoLineEndings() => new(WorkingDirectory, extraConfig, environment, true);

    /// <summary>Returns a runner with the same configuration and environment in another directory.</summary>
    /// <param name="workingDirectory">The directory.</param>
    /// <returns>The new runner.</returns>
    public GitRunner At(string workingDirectory) => new(workingDirectory, extraConfig, environment, repoLineEndings);

    /// <summary>Returns a runner that also sets, or with a null value removes, environment variables for git.</summary>
    /// <param name="variables">Variables to set (null value = remove).</param>
    /// <returns>The new runner.</returns>
    public GitRunner WithEnvironment(IReadOnlyDictionary<string, string?> variables)
    {
        var merged = new Dictionary<string, string?>(environment, StringComparer.Ordinal);
        foreach (var (key, value) in variables)
        {
            merged[key] = value;
        }

        return new(WorkingDirectory, extraConfig, merged, repoLineEndings);
    }

    /// <summary>Runs git and returns the raw result.</summary>
    /// <param name="args">Git arguments.</param>
    /// <returns>The result, whatever the exit code.</returns>
    public ProcessResult Try(params string[] args) =>
        ProcessRunner.Run(
            "git", [.. repoLineEndings ? CommonConfig : BaseConfig, .. extraConfig, .. args], WorkingDirectory, new ProcessRunOptions { EnvironmentVariables = environment });

    /// <summary>Runs git and requires success.</summary>
    /// <param name="args">Git arguments.</param>
    /// <returns>Trimmed stdout.</returns>
    /// <exception cref="ToolException">Git failed (exit code 4).</exception>
    public string Run(params string[] args)
    {
        var r = Try(args);
        if (r.ExitCode != 0)
        {
            throw new ToolException(ExitCodes.Environment, $"git {string.Join(' ', args)} failed in {WorkingDirectory}: {TextLines.OneLine(r.StdErr.Length > 0 ? r.StdErr : r.StdOut)}");
        }

        return r.StdOut.Trim();
    }

    /// <summary>Runs git and splits stdout into lines.</summary>
    /// <param name="args">Git arguments.</param>
    /// <returns>Non-empty trimmed lines.</returns>
    public IReadOnlyList<string> Lines(params string[] args) => TextLines.Split(Run(args));

    /// <summary>Checks that a ref names a commit.</summary>
    /// <param name="fullRef">For example <c>refs/heads/task/T1</c>.</param>
    /// <returns>True when it exists.</returns>
    public bool RefExists(string fullRef) => Try("rev-parse", "--verify", "--quiet", fullRef + "^{commit}").ExitCode == 0;

    /// <summary>Resolves a revision to a full commit sha.</summary>
    /// <param name="rev">The revision.</param>
    /// <returns>The sha.</returns>
    public string RevParse(string rev) => Run("rev-parse", "--verify", rev + "^{commit}");

    /// <summary>Builds the full ref of a local branch.</summary>
    /// <param name="branch">Branch name, for example <c>epic/E1</c>.</param>
    /// <returns><c>refs/heads/</c> plus the name.</returns>
    public static string HeadsRef(string branch) => "refs/heads/" + branch;
}
