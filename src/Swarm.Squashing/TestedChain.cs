using Swarm.Batching;
using Swarm.Git;

namespace Swarm.Squashing;

/// <summary>One task's slice of the tested integration chain.</summary>
/// <param name="Task">The task.</param>
/// <param name="Before">The integration state the task was merged onto.</param>
/// <param name="After">The task's merge commit, or <paramref name="Before"/> when the merge was a no-op ("Already up to date").</param>
/// <param name="Tree">Tree of <paramref name="After"/>: exactly what was tested for this prefix of the batch.</param>
/// <param name="Source">The tested branch commit (second parent of the merge), or null for a no-op merge.</param>
public sealed record ChainLink(LandTask Task, string Before, string After, string Tree, string? Source);

/// <summary>Reads <see cref="LandRequest.TestedCommit"/> as built by <see cref="IntegrationWorktree.Integrate"/>: epic tip + one merge per task.</summary>
public static class TestedChain
{
    /// <summary>Subject prefix of the merges <see cref="IntegrationWorktree.Integrate"/> writes.</summary>
    public const string MergeSubjectPrefix = "batch: merge ";

    /// <summary>The subject of a task's integration merge: <c>batch: merge &lt;id&gt; (&lt;branch&gt;)</c>.</summary>
    /// <param name="task">The task.</param>
    /// <returns>The subject.</returns>
    public static string MergeSubject(LandTask task) => $"{MergeSubjectPrefix}{task.Id} ({task.Branch})";

    /// <summary>Maps every task of the request to its link in the first-parent chain.</summary>
    /// <param name="request">The land request.</param>
    /// <returns>One link per task, in task order.</returns>
    /// <exception cref="ToolException">The tested commit is not epic tip + one merge per task (exit code 4).</exception>
    public static IReadOnlyList<ChainLink> Read(LandRequest request)
    {
        var git = request.Repo;
        var commits = git.Lines("log", "--first-parent", "--reverse", "--format=%H%x1f%T%x1f%P%x1f%s", $"{request.EpicTipBefore}..{request.TestedCommit}")
            .Select(line => line.Split('\u001f'))
            .ToList();
        var state = request.EpicTipBefore;
        var tree = git.Run("rev-parse", state + "^{tree}");
        var next = 0;
        var links = new List<ChainLink>();
        foreach (var task in request.Tasks)
        {
            if (next < commits.Count && commits[next].Length == 4 && commits[next][3] == MergeSubject(task))
            {
                var c = commits[next++];
                var parents = c[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parents.Length != 2 || !Same(parents[0], state))
                {
                    throw Violation(request, $"merge of task '{task.Id}' ({c[0]}) is not a two-parent merge onto {state}");
                }

                links.Add(new ChainLink(task, state, c[0], c[1], parents[1]));
                (state, tree) = (c[0], c[1]);
            }
            else
            {
                // "Already up to date": the branch added nothing, so the integration made no merge commit for it.
                links.Add(new ChainLink(task, state, state, tree, null));
            }
        }

        if (next < commits.Count)
        {
            throw Violation(request, $"commit {commits[next][0]} is not the merge of the next task");
        }

        if (!Same(state, request.TestedCommit))
        {
            throw Violation(request, "the chain does not end at the tested commit");
        }

        return links;
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static ToolException Violation(LandRequest request, string detail) => new(
        ExitCodes.Environment,
        $"tested commit {request.TestedCommit} of batch {request.Batch} is not the epic tip plus one merge per task: {detail}",
        "lander contract: TestedCommit must come from IntegrationWorktree.Integrate");
}
