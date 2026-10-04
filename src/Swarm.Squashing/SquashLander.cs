using System.Text;
using Swarm.Batching;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Squashing;

/// <summary>One squashed commit, or a skipped empty one, made by <see cref="SquashLander"/>.</summary>
/// <param name="TaskIds">Tasks folded into it, in order.</param>
/// <param name="Ticket">Its ticket.</param>
/// <param name="Commit">The new epic commit, or null when nothing was new.</param>
/// <param name="Empty">True when the tasks added nothing to the epic (no commit was made).</param>
/// <param name="Subject">The commit subject (empty when no commit was made).</param>
public sealed record SquashCommit(IReadOnlyList<string> TaskIds, string Ticket, string? Commit, bool Empty, string Subject);

/// <summary>What <see cref="SquashLander.Execute"/> did.</summary>
/// <param name="Result">The lander-contract result.</param>
/// <param name="Commits">The squashed (or empty) commits of the landed tasks, in order.</param>
public sealed record SquashOutcome(LandResult Result, IReadOnlyList<SquashCommit> Commits);

/// <summary>
/// Lands each green task as ONE trailer-stamped commit. Each commit's tree is the tree the tested chain had after
/// that task's merge, so content is never merged again, nothing is checked out, and when nothing fails the landed
/// tree equals the tested tree by construction (checked by <see cref="TreeGuard"/> before the epic moves). Stack
/// members with the same ticket share one commit; a stack lands whole or not at all; the epic moves once, by
/// compare-and-swap; task branches are never touched.
/// </summary>
/// <param name="config">Squash settings.</param>
/// <param name="baseBranch">The epic's base branch, which bounds the history scanned for already-landed sources (null = whole epic history).</param>
/// <param name="ticketOverride">Ticket for every task (the CLI's <c>--ticket</c>), or null to derive it.</param>
public sealed class SquashLander(SquashConfig config, string? baseBranch = null, string? ticketOverride = null) : ILander
{
    /// <summary>Name of the temporary message file in the worktree's git dir.</summary>
    public const string MessageFileName = "SWARM_SQUASH_MSG";

    // Total characters of excluded shas per git call; leaves ample room under Windows' 32,767-character command line.
    const int MaxExclusionArgChars = 24_000;

    static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // Reflog subjects `git checkout -B <copy> refs/heads/<worker>` writes (git's branch.c), newest first in `reflog show`.
    static readonly string[] CopyReflogPrefixes = ["branch: Created from refs/heads/", "branch: Reset to refs/heads/"];

    /// <inheritdoc/>
    public string Name => LanderNames.Squash;

    /// <summary>Gets a test seam that replaces the rebuilt tip just before the tree check (proves the check guards the move).</summary>
    internal Func<string, string>? RebuiltTipOverride { get; init; }

    /// <inheritdoc/>
    /// <exception cref="ToolException">Contract violation, tree mismatch or epic moved (exit code 4).</exception>
    public LandResult Land(LandRequest request) => Execute(request).Result;

    /// <summary>Lands the tasks and reports the squashed commits.</summary>
    /// <param name="request">The request (see the <see cref="ILander"/> contract).</param>
    /// <returns>
    /// The contract result and the commits made. With <see cref="SquashConfig.RequireTicket"/>, a task that needs a
    /// commit but has no ticket lands nothing at all: it is the failure and every other task is not attempted.
    /// </returns>
    /// <exception cref="ToolException">The tested commit is not epic tip + one merge per task, the rebuilt tree differs, or the epic moved (exit code 4); nothing is landed then.</exception>
    public SquashOutcome Execute(LandRequest request)
    {
        var links = TestedChain.Read(request).ToDictionary(l => l.Task.Id, StringComparer.Ordinal);
        var units = TaskUnits.Build(request.Tasks.Select(t => new TaskSpec(t.Id, t.Branch, t.DependsOn)).ToList());
        EnsureRequestOrder(request, units);
        if (MissingTicket(request, links) is { } missing)
        {
            // Only the whole chain was tested: landing the tasks before a ticket-less one would put an untested state
            // on the epic. Nothing lands; the other tasks are reported not attempted and are retested without it.
            var others = request.Tasks.Select(t => t.Id).Where(id => !string.Equals(id, missing.TaskId, StringComparison.Ordinal)).ToList();
            return new SquashOutcome(new LandResult(request.EpicTipBefore, [], missing, others), []);
        }

        var context =new MessageContext(request.Epic, request.Batch, request.RunId);
        var landedSources = Chunk(LandedSources(request.Repo, request.EpicTipBefore));
        var messageFile = Path.Combine(request.Worktree.Run("rev-parse", "--absolute-git-dir"), MessageFileName);
        var tip = request.EpicTipBefore;
        var tree = request.Repo.Run("rev-parse", tip + "^{tree}");
        var landed = new List<LandedTask>();
        var commits = new List<SquashCommit>();
        var notAttempted = new List<string>();
        LandFailure? failure = null;
        try
        {
            foreach (var unit in units)
            {
                if (failure is not null)
                {
                    notAttempted.AddRange(unit.Ids);
                    continue;
                }

                var draft = LandUnit(request, unit, links, landedSources, context, messageFile, tip, tree);
                if (draft.Failure is { } f)
                {
                    // A stack lands whole or not at all: its other members are reported as not attempted.
                    failure = f;
                    notAttempted.AddRange(unit.Ids.Where(id => id != f.TaskId));
                    continue;
                }

                (tip, tree) = (draft.Tip, draft.Tree);
                landed.AddRange(draft.Landed);
                commits.AddRange(draft.Commits);
            }
        }
        finally
        {
            SharedFile.Retry(() => File.Delete(messageFile));
        }

        // Unreferenced commits are all that exists until here; nothing moves unless the tree is the tested one: the
        // whole tested commit when nothing failed, else the chain state after the last landed task.
        var expected = failure is null ? request.TestedCommit
            : landed.Count == 0 ? request.EpicTipBefore
            : links[landed[^1].TaskId].After;
        tip = RebuiltTipOverride?.Invoke(tip) ?? tip;
        TreeGuard.Ensure(request.Repo, tip, expected, $"squash lander, batch {request.Batch}");
        if (!string.Equals(tip, request.EpicTipBefore, StringComparison.OrdinalIgnoreCase))
        {
            EpicRef.Move(request, tip, Name);
        }

        return new SquashOutcome(new LandResult(tip, landed, failure, notAttempted), commits);
    }

    static string TicketBranch(LandRequest request, LandTask task)
    {
        // A task that conflicted lands through batch's copy rebased/<epic>/<task>, whose name has lost the ticket;
        // the copy's reflog still names the worker branch it was created from.
        if (!string.Equals(task.Branch, $"rebased/{request.Epic}/{task.Id}", StringComparison.Ordinal))
        {
            return task.Branch;
        }

        var reflog = request.Repo.Try("reflog", "show", "--format=%gs", GitRunner.HeadsRef(task.Branch));
        foreach (var line in TextLines.Split(reflog.StdOut))
        {
            if (CopyReflogPrefixes.FirstOrDefault(p => line.StartsWith(p, StringComparison.Ordinal)) is { } prefix)
            {
                return line[prefix.Length..];
            }
        }

        return task.Branch;
    }

    // A link's new commits, minus everything reachable from an already-landed source. The exclusions are passed in
    // argv-bounded chunks (GitRunner has no stdin, so no --stdin): a commit is excluded when it is reachable from ANY
    // excluded sha, i.e. it survives only if every chunk's run returns it. Order is that of the first run.
    static IReadOnlyList<SourceCommit> SourceCommits(GitRunner repo, ChainLink link, IReadOnlyList<IReadOnlyList<string>> landedSourceChunks)
    {
        if (link.Source is null)
        {
            return [];
        }

        var range = $"{link.Before}..{link.Source}";
        if (landedSourceChunks.Count == 0)
        {
            return LogCommits(repo, range, []);
        }

        var commits = LogCommits(repo, range, landedSourceChunks[0]);
        foreach (var chunk in landedSourceChunks.Skip(1))
        {
            if (commits.Count == 0)
            {
                break;
            }

            var kept = LogCommits(repo, range, chunk).Select(c => c.Sha).ToHashSet(StringComparer.OrdinalIgnoreCase);
            commits = commits.Where(c => kept.Contains(c.Sha)).ToList();
        }

        return commits;
    }

    static List<SourceCommit> LogCommits(GitRunner repo, string range, IReadOnlyList<string> exclude)
    {
        // --ignore-missing: a recorded source may have been garbage-collected since it landed.
        var args = new List<string> { "log", "--reverse", "--no-merges", "--ignore-missing", "--format=%H%x1f%an%x1f%ae%x1f%s", range };
        if (exclude.Count > 0)
        {
            args.Add("--not");
            args.AddRange(exclude);
        }

        return repo.Lines([.. args])
            .Select(line => line.Split('\u001f'))
            .Where(p => p.Length == 4)
            .Select(p => new SourceCommit(p[0], p[1], p[2], p[3]))
            .ToList();
    }

    // Splits the exclusion shas so one git call's argv stays well under Windows' 32,767-character command line.
    static IReadOnlyList<IReadOnlyList<string>> Chunk(IReadOnlyList<string> shas)
    {
        var chunks = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        var length = 0;
        foreach (var sha in shas)
        {
            if (current.Count > 0 && length + sha.Length + 1 > MaxExclusionArgChars)
            {
                chunks.Add(current);
                (current, length) = ([], 0);
            }

            current.Add(sha);
            length += sha.Length + 1;
        }

        if (current.Count > 0)
        {
            chunks.Add(current);
        }

        return chunks;
    }

    // The ILander contract: stacks are contiguous and in request order. The tested chain is in request order and the
    // commits are built per unit, so any other shape would credit one task with another's content.
    static void EnsureRequestOrder(LandRequest request, IReadOnlyList<TaskUnit> units)
    {
        var flattened = units.SelectMany(u => u.Ids).ToList();
        for (var i = 0; i < request.Tasks.Count; i++)
        {
            if (!string.Equals(flattened[i], request.Tasks[i].Id, StringComparison.Ordinal))
            {
                throw new ToolException(
                    ExitCodes.Environment,
                    $"squash lander, batch {request.Batch}: task '{flattened[i]}' is out of order: its stack is not contiguous in request order (position {i + 1} is '{request.Tasks[i].Id}'); nothing landed",
                    "lander contract: ILander requires contiguous stacks in request order");
            }
        }
    }

    static bool IsSha(string s) => s.Length is 40 or 64 && s.All(Uri.IsHexDigit);

    // The message pairs Tasks[i] with SourceTips[i] (a null tip is a no-op merge and gets no Source-Commit trailer);
    // a misaligned group would stamp the wrong source on a task, so it is a lander bug, never a landing.
    static void EnsureAligned(SquashGroup group)
    {
        if (group.SourceTips.Count != group.Tasks.Count)
        {
            throw new ToolException(
                ExitCodes.Environment,
                $"squash lander: group '{group.Ticket}' has {group.Tasks.Count} tasks but {group.SourceTips.Count} source tips; the epic was not moved",
                "this is a lander bug: keep the run folder and report it");
        }
    }

    // Source commits of tickets already squashed onto the epic. A branch stacked on an already-landed branch still
    // contains that branch's original commits; excluding them keeps the author and the commit list to the new work.
    IReadOnlyList<string> LandedSources(GitRunner repo, string epicTip)
    {
        // --grep only skips commits without the trailer line (cheap bound when baseBranch is null); the set is unchanged.
        var args = new List<string> { "log", "--first-parent", "--regexp-ignore-case", "--grep=^Source-Commit:", "--format=%(trailers:key=Source-Commit,valueonly)", epicTip };
        if (baseBranch is not null && repo.RefExists(GitRunner.HeadsRef(baseBranch)))
        {
            args.Add("--not");
            args.Add(GitRunner.HeadsRef(baseBranch));
        }

        return repo.Lines([.. args]).Where(IsSha).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    UnitDraft LandUnit(LandRequest request, TaskUnit unit, IReadOnlyDictionary<string, ChainLink> links, IReadOnlyList<IReadOnlyList<string>> landedSources, MessageContext context, string messageFile, string tip, string tree)
    {
        var landed = new List<LandedTask>();
        var commits = new List<SquashCommit>();
        foreach (var (ticket, members) in Groups(request, unit, links))
        {
            var ids = members.Select(m => m.Task.Id).ToList();
            var last = members[^1];
            if (string.Equals(last.Tree, tree, StringComparison.Ordinal))
            {
                // The epic already holds this content: the tasks count as landed where they are; never an empty commit.
                // Checked before the ticket: no commit is made, so a ticket-less no-op needs no ticket (Ruling B3-final).
                landed.AddRange(ids.Select(id => new LandedTask(id, tip)));
                commits.Add(new SquashCommit(ids, ticket ?? "", null, true, ""));
                continue;
            }

            if (ticket is null)
            {
                // Unreachable after MissingTicket; kept so a ticket-less group can never become a commit.
                var t = members[0].Task;
                return new UnitDraft(tip, tree, [], [], new LandFailure(t.Id, [], NoTicketReason(t)));
            }

            // Source tips stay per task and unchanged (null for a no-op merge); the branch tip is never substituted
            // because the branch may have moved since it was integrated.
            var group = new SquashGroup(
                ticket,
                members.Select(m => m.Task).ToList(),
                members.Select(m => m.Source).ToList(),
                members.SelectMany(m => SourceCommits(request.Repo, m, landedSources)).ToList());
            EnsureAligned(group);
            File.WriteAllText(messageFile, SquashMessage.Build(config, group, context), Utf8);
            var commit = Committer(request.Repo, group).Run("commit-tree", last.Tree, "-p", tip, "-F", messageFile);
            landed.AddRange(ids.Select(id => new LandedTask(id, commit)));
            commits.Add(new SquashCommit(ids, ticket, commit, false, SquashMessage.Subject(config, group, context)));
            (tip, tree) = (commit, last.Tree);
        }

        return new UnitDraft(tip, tree, landed, commits, null);
    }

    // With squash.requireTicket, the first task in request order that would need a commit (its merge changed the tested
    // tree) but has no ticket. A no-op task needs none: no commit is made for it.
    LandFailure? MissingTicket(LandRequest request, IReadOnlyDictionary<string, ChainLink> links)
    {
        foreach (var task in request.Tasks)
        {
            var link = links[task.Id];
            if (TicketResolver.Resolve(config, task.Id, TicketBranch(request, task), ticketOverride) is null
                && link.Source is not null
                && !string.Equals(link.Tree, request.Repo.Run("rev-parse", link.Before + "^{tree}"), StringComparison.Ordinal))
            {
                return new LandFailure(task.Id, [], NoTicketReason(task));
            }
        }

        return null;
    }

    string NoTicketReason(LandTask task) =>
        $"no ticket for task '{task.Id}' (branch '{task.Branch}'): squash.ticketPattern '{config.TicketPattern}' matches neither and squash.requireTicket is true; nothing landed from this batch";

    // Consecutive members of one unit with the same ticket form one group; a member without a ticket is its own group.
    IEnumerable<(string? Ticket, List<ChainLink> Members)> Groups(LandRequest request, TaskUnit unit, IReadOnlyDictionary<string, ChainLink> links)
    {
        (string? Ticket, List<ChainLink> Members)? current = null;
        foreach (var member in unit.Members)
        {
            var link = links[member.Id];
            var ticket = TicketResolver.Resolve(config, link.Task.Id, TicketBranch(request, link.Task), ticketOverride);
            if (current is { } open && ticket is not null && string.Equals(open.Ticket, ticket, StringComparison.OrdinalIgnoreCase))
            {
                open.Members.Add(link);
                continue;
            }

            if (current is { } done)
            {
                yield return done;
            }

            current = (ticket, new List<ChainLink> { link });
        }

        if (current is { } final)
        {
            yield return final;
        }
    }

    GitRunner Committer(GitRunner repo, SquashGroup group)
    {
        // Committer is always the tool; identity variables inherited from the caller's shell are removed so they
        // cannot override it, and the author is set (or left to the tool identity) here.
        var author = SquashMessage.Author(config, group);
        return repo.WithIdentity().WithEnvironment(new Dictionary<string, string?>
        {
            ["GIT_AUTHOR_NAME"] = author?.AuthorName,
            ["GIT_AUTHOR_EMAIL"] = author?.AuthorEmail,
            ["GIT_AUTHOR_DATE"] = null,
            ["GIT_COMMITTER_NAME"] = null,
            ["GIT_COMMITTER_EMAIL"] = null,
            ["GIT_COMMITTER_DATE"] = null,
        });
    }

    sealed record UnitDraft(string Tip, string Tree, IReadOnlyList<LandedTask> Landed, IReadOnlyList<SquashCommit> Commits, LandFailure? Failure);
}
