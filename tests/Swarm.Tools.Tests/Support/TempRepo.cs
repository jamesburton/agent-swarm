using System.Text.Json;
using Swarm.Git;

namespace Swarm.Tools.Tests.Support;

/// <summary>A task entry for <see cref="TempRepo.WriteTasks"/>.</summary>
/// <param name="Id">Task id.</param>
/// <param name="Branch">Task branch name.</param>
/// <param name="DependsOn">Ids of tasks this one depends on.</param>
public sealed record TaskLine(string Id, string Branch, string[]? DependsOn = null);

/// <summary>A throwaway git repo in its own sandbox (sandbox/repo, sandbox/state, sandbox/wt).</summary>
public sealed class TempRepo : IDisposable
{
    TempRepo(string sandbox)
    {
        Sandbox = sandbox;
        var dir = Path.Combine(sandbox, "repo");
        Directory.CreateDirectory(dir);
        RunGit(dir, "init", "-q", "-b", "main");
        RunGit(dir, "config", "user.name", "test");
        RunGit(dir, "config", "user.email", "test@example.invalid");
        RunGit(dir, "config", "core.autocrlf", "false");
        Root = Path.GetFullPath(RunGit(dir, "rev-parse", "--show-toplevel"));
        Commit("init", ("README.md", "readme\n"));
    }

    /// <summary>Gets the sandbox directory holding the repo, state and worktree directories.</summary>
    public string Sandbox { get; }

    /// <summary>Gets the repository top-level directory.</summary>
    public string Root { get; }

    /// <summary>Gets the directory intended for run state.</summary>
    public string StateDir => Path.Combine(Sandbox, "state");

    /// <summary>Gets the directory intended for worktrees.</summary>
    public string WorktreeRoot => Path.Combine(Sandbox, "wt");

    /// <summary>Creates a repo with a <c>main</c> branch and an initial commit.</summary>
    /// <returns>The new repo.</returns>
    public static TempRepo Create() => new(TestPaths.NewSandbox());

    /// <summary>Runs git in <paramref name="dir"/> and returns trimmed stdout.</summary>
    /// <param name="dir">Working directory.</param>
    /// <param name="args">Git arguments.</param>
    /// <returns>Trimmed standard output.</returns>
    /// <exception cref="ToolException">Git exited non-zero.</exception>
    public static string RunGit(string dir, params string[] args) =>
        new GitRunner(dir).Run(args);

    /// <summary>Runs git in the repo root.</summary>
    /// <param name="args">Git arguments.</param>
    /// <returns>Trimmed standard output.</returns>
    public string Git(params string[] args) => RunGit(Root, args);

    /// <summary>Writes a file (LF line endings) under the repo root.</summary>
    /// <param name="relativePath">Path relative to the root.</param>
    /// <param name="content">File content.</param>
    public void Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content.ReplaceLineEndings("\n"));
    }

    /// <summary>Writes the files, stages everything and commits on the current branch.</summary>
    /// <param name="message">Commit message.</param>
    /// <param name="files">Files to write first.</param>
    /// <returns>The new HEAD sha.</returns>
    public string Commit(string message, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            Write(path, content);
        }

        Git("add", "-A");
        Git("commit", "-q", "--allow-empty", "-m", message);
        return Sha("HEAD");
    }

    /// <summary>Creates a branch from <paramref name="from"/> with one commit, then returns to <c>main</c>.</summary>
    /// <param name="name">New branch name.</param>
    /// <param name="from">Start point.</param>
    /// <param name="files">Files committed on the branch.</param>
    /// <returns>The branch tip sha.</returns>
    public string Branch(string name, string from, params (string Path, string Content)[] files)
    {
        Git("checkout", "-q", "-b", name, from);
        try
        {
            return Commit($"{name}: change", files);
        }
        finally
        {
            Git("checkout", "-q", "main");
        }
    }

    /// <summary>Creates an epic branch.</summary>
    /// <param name="from">Start point.</param>
    /// <param name="name">Epic branch name.</param>
    public void Epic(string from = "main", string name = "epic/E1") => Git("branch", name, from);

    /// <summary>Leaves a stale lock file that makes every <c>update-ref</c> of <paramref name="branch"/> fail.</summary>
    /// <param name="branch">Branch name.</param>
    /// <returns>The lock file path (per-ref with the files backend, the table list with reftable).</returns>
    public string LockRef(string branch)
    {
        var gitDir = Git("rev-parse", "--absolute-git-dir");
        var path = Git("rev-parse", "--show-ref-format") == "reftable"
            ? Path.Combine(gitDir, "reftable", "tables.list.lock")
            : Path.Combine(gitDir, "refs", "heads", branch + ".lock");
        File.WriteAllText(path, "");
        return path;
    }

    /// <summary>Resolves a revision to its sha.</summary>
    /// <param name="rev">Any git revision.</param>
    /// <returns>The full sha.</returns>
    public string Sha(string rev) => Git("rev-parse", rev);

    /// <summary>Tests whether <paramref name="path"/> exists in <paramref name="rev"/>.</summary>
    /// <param name="rev">Any git revision.</param>
    /// <param name="path">Repo-relative path.</param>
    /// <returns>True when the path exists in that revision.</returns>
    public bool HasFile(string rev, string path) =>
        new GitRunner(Root).Try("cat-file", "-e", $"{rev}:{path}").ExitCode == 0;

    /// <summary>Writes <c>tasks.json</c> (camelCase) in the sandbox.</summary>
    /// <param name="tasks">Task entries.</param>
    /// <returns>The path of the written file.</returns>
    public string WriteTasks(params TaskLine[] tasks)
    {
        var path = Path.Combine(Sandbox, "tasks.json");
        File.WriteAllText(path, JsonSerializer.Serialize(tasks.Select(t => new { id = t.Id, branch = t.Branch, dependsOn = t.DependsOn ?? Array.Empty<string>() })));
        return path;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            FileTree.DeleteTree(Sandbox);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An orphaned FakeSuite child may still hold its cwd; the temp root is disposable.
        }
    }
}
