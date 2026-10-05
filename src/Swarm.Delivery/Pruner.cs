using System.ComponentModel;
using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>Prune actions.</summary>
public static class PruneActions
{
    /// <summary>Remove the worktree (and its branch when the decision says so).</summary>
    public const string Remove = "remove";

    /// <summary>The directory is gone: drop git's registration.</summary>
    public const string PruneMetadata = "prune-metadata";

    /// <summary>Leave it alone.</summary>
    public const string Keep = "keep";
}

/// <summary>What to do with one worktree.</summary>
/// <param name="Action">A <see cref="PruneActions"/> value.</param>
/// <param name="Reason">One-line reason.</param>
/// <param name="DeleteBranch">True to delete the task branch as well.</param>
public sealed record PruneDecision(string Action, string Reason, bool DeleteBranch);

/// <summary>One line of the prune report.</summary>
/// <param name="Path">Worktree path.</param>
/// <param name="Branch">Task branch.</param>
/// <param name="Ticket">Ticket id.</param>
/// <param name="Action">The decided action.</param>
/// <param name="Reason">Why.</param>
/// <param name="Done">True when the action was carried out (false for keep and for dry runs).</param>
/// <param name="BranchDeleted">True when the branch was deleted.</param>
/// <param name="Error">One-line failure, or null.</param>
public sealed record PruneItem(string Path, string Branch, string? Ticket, string Action, string Reason, bool Done, bool BranchDeleted, string? Error);

/// <summary>stdout of <c>worktree prune</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="DryRun">True when nothing was changed.</param>
/// <param name="Force">True when unmerged, dirty and empty worktrees were removed too.</param>
/// <param name="Items">One item per managed worktree considered.</param>
/// <param name="Removed">Worktrees removed or deregistered.</param>
/// <param name="Kept">Worktrees kept.</param>
/// <param name="Failed">Items whose action failed.</param>
public sealed record PruneReport(int SchemaVersion, bool DryRun, bool Force, IReadOnlyList<PruneItem> Items, int Removed, int Kept, int Failed);

/// <summary>
/// Removes finished task worktrees: never unmerged or dirty work without force, never locked worktrees,
/// never the main checkout, unmanaged worktrees or worktrees whose state could not be read.
/// </summary>
/// <param name="manager">The worktree manager.</param>
public sealed class Pruner(WorktreeManager manager)
{
    /// <summary>Gets or sets a test seam invoked with each entry just before its action is carried out.</summary>
    internal Action<WorktreeEntry>? BeforeApply { get; set; }

    /// <summary>Decides what to do with one managed worktree.</summary>
    /// <param name="entry">The worktree.</param>
    /// <param name="force">Whether unmerged, dirty and empty work may go.</param>
    /// <returns>The decision.</returns>
    public static PruneDecision Decide(WorktreeEntry entry, bool force)
    {
        // Neither of these is ever forced: unknown state is not proof of anything, and unmanaged worktrees are not ours.
        if (entry.Error is not null)
        {
            return new PruneDecision(PruneActions.Keep, "could not assess: " + entry.Error, false);
        }

        if (!entry.Managed)
        {
            return new PruneDecision(PruneActions.Keep, "not created by swarm", false);
        }

        if (entry.Locked)
        {
            return new PruneDecision(PruneActions.Keep, $"locked ({entry.LockReason ?? "no reason"}): run git worktree unlock first", false);
        }

        if (entry.Missing && entry.DirectoryExists)
        {
            // git calls the registration prunable (e.g. the directory's .git file is gone) but the directory may still hold the only copy of work.
            return new PruneDecision(PruneActions.Keep, "registration stale (gitdir missing) but directory exists; inspect it", false);
        }

        if (entry.Missing)
        {
            var merged = entry.MergedVia is not null;
            var reason = merged ? $"directory missing; branch merged ({entry.MergedVia})" : force ? "directory missing; unmerged branch deleted (forced)" : "directory missing; unmerged branch kept";
            return new PruneDecision(PruneActions.PruneMetadata, reason, merged || force);
        }

        var normal = entry switch
        {
            { Dirty: true } => new PruneDecision(PruneActions.Keep, "uncommitted or untracked changes", false),
            { MergedVia: { } via } => new PruneDecision(PruneActions.Remove, $"merged ({via})", true),
            { Empty: true } => new PruneDecision(PruneActions.Keep, "no commits yet", false),
            { BaseExists: false } => new PruneDecision(PruneActions.Keep, $"abandoned: base '{entry.Base}' no longer exists and {entry.AheadOfBase} commit(s) are unmerged", false),
            _ => new PruneDecision(PruneActions.Keep, $"unmerged: {entry.AheadOfBase} commit(s) not on '{entry.Base}'", false),
        };
        return force && normal.Action == PruneActions.Keep ? new PruneDecision(PruneActions.Remove, "forced: " + normal.Reason, true) : normal;
    }

    /// <summary>Prunes managed worktrees.</summary>
    /// <param name="baseBranch">Only worktrees on this base, or null for all.</param>
    /// <param name="dryRun">Report only: nothing is changed.</param>
    /// <param name="force">See <see cref="Decide"/>.</param>
    /// <returns>The report; failed items carry an error and do not stop the run.</returns>
    public PruneReport Prune(string? baseBranch, bool dryRun, bool force)
    {
        var items = new List<PruneItem>();
        foreach (var e in manager.List(baseBranch).Where(e => e.Managed))
        {
            // A task branch checked out in the main checkout is listed as managed; the main checkout itself is never removed.
            var d = WorktreeList.SamePath(e.Path, manager.Git.WorkingDirectory)
                ? new PruneDecision(PruneActions.Keep, "main checkout: never pruned", false)
                : Decide(e, force);
            var item = new PruneItem(e.Path, e.Branch, e.Ticket, d.Action, d.Reason, false, false, null);
            if (!dryRun && d.Action != PruneActions.Keep)
            {
                try
                {
                    item = Apply(e, d, force, item);
                }
                catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or Win32Exception)
                {
                    // One failure never stops the rest or loses the report of items already processed.
                    item = item with { Error = TextLines.OneLine(ex.Message) };
                }
            }

            items.Add(item);
        }

        return new PruneReport(
            SwarmJson.SchemaVersion, dryRun, force, items,
            items.Count(i => i.Done), items.Count(i => i.Action == PruneActions.Keep), items.Count(i => i.Error is not null));
    }

    PruneItem Apply(WorktreeEntry e, PruneDecision d, bool force, PruneItem item)
    {
        BeforeApply?.Invoke(e);

        // `worktree remove` (not the repo-wide `worktree prune`) also deregisters a missing directory, and touches only this worktree.
        // A single --force discards dirty files but still refuses a locked worktree; without it git re-checks for changes made since listing.
        var r = force && d.Action == PruneActions.Remove
            ? manager.Git.Try("worktree", "remove", "--force", e.Path)
            : manager.Git.Try("worktree", "remove", e.Path);
        if (r.ExitCode != 0)
        {
            // The branch is kept whenever the worktree could not be removed.
            return item with { Error = RemoveFailure(e.Path, d.Action, r) };
        }

        item = item with { Done = true };
        return d.DeleteBranch ? DeleteBranch(e, item) : item;
    }

    // Reports what is actually left after a failed remove. The facts come first and git's message last, so truncation to one line can only shorten git's message.
    // Manual deletion is advised only in the held-file case (C6): a worktree judged disposable (Remove) that git has already deregistered.
    // A directory that is still registered, or whose registration state is unknown, or that was expected to be missing, may hold the only copy of work.
    string RemoveFailure(string path, string action, ProcessResult r)
    {
        bool? registered;
        try
        {
            registered = WorktreeList.Read(manager.Git).Any(w => WorktreeList.SamePath(w.Path, path));
        }
        catch (ToolException)
        {
            registered = null;
        }

        var notes = new List<string> { "remove failed" };
        if (!Directory.Exists(path))
        {
            notes.Add(registered switch
            {
                true => "worktree still registered",
                false => "worktree no longer registered",
                null => "worktree registration unknown, check git worktree list",
            });
        }
        else
        {
            const string Inspect = "inspect it (it may hold uncommitted work)";
            notes.Add(registered switch
            {
                false when action == PruneActions.Remove => $"'{path}' left behind and no longer registered (a file may be in use), delete it manually",
                false => $"'{path}' still exists but is no longer registered; {Inspect}",
                true => $"'{path}' still exists and is still registered; {Inspect}",
                null => $"'{path}' still exists and its registration is unknown (check git worktree list); {Inspect}",
            });
        }

        notes.Add("branch kept");
        notes.Add("git: " + TextLines.OneLine(r.StdErr.Length > 0 ? r.StdErr : r.StdOut));
        return TextLines.OneLine(string.Join("; ", notes));
    }

    // Deletes the branch only at the tip that was assessed (a commit made since the merge check keeps it) and never while it is checked out.
    // `branch -D` would delete whatever the tip is now and `branch -d` refuses squash-landed branches, so update-ref with the old value is used.
    PruneItem DeleteBranch(WorktreeEntry e, PruneItem item)
    {
        try
        {
            if (e.Head is null)
            {
                return item with { Error = "branch kept: git did not report its tip" };
            }

            if (WorktreeList.CheckedOut(manager.Git, e.Branch) is { } other)
            {
                return item with { Error = $"branch kept: checked out at '{other.Path}'" };
            }

            var b = manager.Git.Try("update-ref", "-d", GitRunner.HeadsRef(e.Branch), e.Head);
            if (b.ExitCode != 0)
            {
                return item with { Error = "branch kept: " + TextLines.OneLine(b.StdErr.Length > 0 ? b.StdErr : b.StdOut) };
            }

            // update-ref leaves branch.<b>.* (incl. the swarm metadata) behind; branch -D would have removed the section.
            item = item with { BranchDeleted = true };
            var c = manager.Git.Try("config", "--remove-section", $"branch.{e.Branch}");
            return c.ExitCode == 0
                ? item
                : item with { Error = $"branch deleted but its config (branch.{e.Branch}.*) was not removed: " + TextLines.OneLine(c.StdErr) };
        }
        catch (ToolException ex)
        {
            return item with { Error = "branch delete failed: " + TextLines.OneLine(ex.Message) };
        }
    }
}
