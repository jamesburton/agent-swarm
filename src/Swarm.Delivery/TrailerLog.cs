using Swarm.Git;

namespace Swarm.Delivery;

/// <summary>A commit and its swarm trailers.</summary>
/// <param name="Sha">Commit sha.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="Tickets"><c>Ticket:</c> values.</param>
/// <param name="Epics"><c>Epic:</c> values.</param>
/// <param name="Batches"><c>Batch:</c> values (per run; <c>0</c> = manual <c>squash run</c>).</param>
/// <param name="Runs"><c>Swarm-Run:</c> values.</param>
public sealed record TrailerCommit(string Sha, string Subject, IReadOnlyList<string> Tickets, IReadOnlyList<string> Epics, IReadOnlyList<string> Batches, IReadOnlyList<string> Runs);

/// <summary>Reads <c>Ticket:</c>/<c>Epic:</c>/<c>Batch:</c>/<c>Swarm-Run:</c> trailers from git history.</summary>
public static class TrailerLog
{
    const char Field = '\x1f';
    const char Record = '\x1e';

    /// <summary>Reads non-merge commits in <c>from..to</c>, oldest first.</summary>
    /// <param name="git">Runner in any worktree.</param>
    /// <param name="fromRef">Excluded side (the active branch).</param>
    /// <param name="toRef">Included side (the epic branch).</param>
    /// <returns>The commits.</returns>
    public static IReadOnlyList<TrailerCommit> Read(GitRunner git, string fromRef, string toRef) =>
        Parse(git.Run("log", "--reverse", "--no-merges", "--format=%H%x1f%s%x1f%(trailers:only,unfold)%x1e", $"{fromRef}..{toRef}"));

    /// <summary>Parses the output of <see cref="Read"/>'s log format.</summary>
    /// <param name="logOutput">Records separated by U+001E, fields by U+001F.</param>
    /// <returns>The commits.</returns>
    public static IReadOnlyList<TrailerCommit> Parse(string logOutput)
    {
        var commits = new List<TrailerCommit>();
        foreach (var raw in logOutput.Split(Record))
        {
            // Only CR/LF are trimmed: the field separator must survive (empty trailer blocks end in it).
            var fields = raw.Trim('\r', '\n').Split(Field);
            if (fields.Length < 2 || fields[0].Length == 0)
            {
                continue;
            }

            var trailers = (fields.Length > 2 ? fields[2] : "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split(':', 2))
                .Where(kv => kv.Length == 2 && kv[1].Trim().Length > 0)
                .Select(kv => (Key: kv[0].Trim(), Value: kv[1].Trim()))
                .ToList();
            IReadOnlyList<string> Values(string key) =>
                trailers.Where(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)).Select(t => t.Value).ToList();
            commits.Add(new TrailerCommit(fields[0].Trim(), fields[1], Values("Ticket"), Values("Epic"), Values("Batch"), Values("Swarm-Run")));
        }

        return commits;
    }
}
