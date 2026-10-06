using System.Globalization;
using Swarm.Batching;
using Swarm.Git;
using Swarm.Squashing;

namespace Swarm.Tools.Tests.Support;

public static class SquashFixture
{
    public static TaskSpec T(string id, string? branch = null, params string[] dependsOn) => new(id, branch ?? "task/" + id, dependsOn);

    public static IntegrationWorktree IntegrationFor(TempRepo repo)
    {
        var wt = new IntegrationWorktree(new GitRunner(repo.Root), Path.Combine(repo.WorktreeRoot, "int-E1"));
        wt.Ensure("epic/E1");
        return wt;
    }

    // The real batch shape: epic tip + IntegrationWorktree.Integrate (merge --no-ff per task).
    public static LandRequest Tested(TempRepo repo, IntegrationWorktree wt, params TaskSpec[] tasks)
    {
        var git = new GitRunner(repo.Root);
        var tip = git.RevParse("refs/heads/epic/E1");
        var integration = wt.Integrate(tip, TaskUnits.Build(tasks));
        if (integration.Conflicts.Count > 0)
        {
            throw new InvalidOperationException("fixture tasks conflict: " + integration.Conflicts[0].Offender.Id);
        }

        return new LandRequest(git, wt.Git, "E1", "epic/E1", tip, integration.Head, tasks.Select(t => new LandTask(t.Id, t.Branch, t.DependsOn)).ToList(), 3, "run-1");
    }

    // Same chain shape for one task branched from the epic tip, built with commit-tree: no checkout, so file-system
    // quirks (case-insensitive names, CRLF) cannot change the tested tree.
    public static LandRequest TestedSingle(TempRepo repo, TaskSpec task)
    {
        var git = new GitRunner(repo.Root);
        var tip = git.RevParse("refs/heads/epic/E1");
        var source = git.RevParse(task.BranchRef);
        var landTask = new LandTask(task.Id, task.Branch, task.DependsOn);
        var tested = repo.Git("commit-tree", repo.Sha(source + "^{tree}"), "-p", tip, "-p", source, "-m", TestedChain.MergeSubject(landTask));
        return new LandRequest(git, git, "E1", "epic/E1", tip, tested, [landTask], 3, "run-1");
    }

    public static string CommitAs(TempRepo repo, string branch, string name, string email, string message, params (string Path, string Content)[] files)
    {
        repo.Git("checkout", "-q", branch);
        try
        {
            foreach (var (path, content) in files)
            {
                repo.Write(path, content);
            }

            repo.Git("add", "-A");
            repo.Git("-c", $"user.name={name}", "-c", $"user.email={email}", "commit", "-q", "--allow-empty", "-m", message);
            return repo.Sha("HEAD");
        }
        finally
        {
            repo.Git("checkout", "-q", "main");
        }
    }

    public static string Trailers(TempRepo repo, string rev, string key) =>
        repo.Git("log", "-1", $"--format=%(trailers:key={key},valueonly,separator=%x2C)", rev);

    public static int Count(TempRepo repo, string range, params string[] extra) =>
        int.Parse(repo.Git(["rev-list", "--count", .. extra, range]), CultureInfo.InvariantCulture);
}
