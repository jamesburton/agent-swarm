using Swarm.Git;
using Swarm.RunState;

namespace Swarm.Delivery;

/// <summary>Maps epic branches to the batch tool's epic id.</summary>
public static class EpicNaming
{
    /// <summary>Finds the <c>--epic</c> value for which batch's <c>epicBranchTemplate</c> yields this branch.</summary>
    /// <param name="config">Config (its <see cref="SwarmConfig.EpicBranchTemplate"/> is used).</param>
    /// <param name="epicBranch">Epic branch.</param>
    /// <returns>The batch epic id, or null when the template cannot express the branch with a safe name.</returns>
    public static string? BatchEpicId(SwarmConfig config, string epicBranch)
    {
        var t = config.EpicBranchTemplate;
        var at = t.IndexOf("{epic}", StringComparison.Ordinal);
        var (prefix, suffix) = (t[..at], t[(at + "{epic}".Length)..]);
        if (epicBranch.Length <= prefix.Length + suffix.Length
            || !epicBranch.StartsWith(prefix, StringComparison.Ordinal)
            || !epicBranch.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = epicBranch[prefix.Length..^suffix.Length];
        return SafeName.IsValid(id) && (config with { Epic = id }).EpicBranch == epicBranch ? id : null;
    }
}

/// <summary>stdout of <c>epic open</c>.</summary>
/// <param name="SchemaVersion">Always <see cref="SwarmJson.SchemaVersion"/>.</param>
/// <param name="Created">False when the epic already existed (idempotent repeat).</param>
/// <param name="Id">Epic id.</param>
/// <param name="Slug">Slug.</param>
/// <param name="Branch">Epic branch.</param>
/// <param name="BaseBranch">Branch it was created from.</param>
/// <param name="BaseCommit">Commit it was created at.</param>
/// <param name="BatchEpic">Value for <c>batch run --epic</c>, or null.</param>
/// <param name="Warnings">One-line warnings.</param>
public sealed record EpicOpenResult(int SchemaVersion, bool Created, string Id, string Slug, string Branch, string BaseBranch, string BaseCommit, string? BatchEpic, IReadOnlyList<string> Warnings);

/// <summary>Opens epics: creates the branch (no checkout) and its record.</summary>
/// <param name="repo">The repository.</param>
/// <param name="config">Validated config.</param>
public sealed class EpicOpener(RepoPaths repo, SwarmConfig config)
{
    readonly GitRunner git = new(repo.MainWorktreeRoot);

    /// <summary>Opens an epic.</summary>
    /// <param name="id">Epic id.</param>
    /// <param name="slug">Slug.</param>
    /// <param name="from">Base branch, or null for config <c>baseBranch</c>.</param>
    /// <param name="kind">Value for <c>{kind}</c>, or null.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ToolException">Naming or path (2); conflicting epic, existing branch or missing base (3).</exception>
    public EpicOpenResult Open(string id, string slug, string? from, string? kind)
    {
        var branch = BranchTemplate.Render(config.EpicTool, id, slug, kind);
        if (git.Try("check-ref-format", "--branch", branch).ExitCode != 0)
        {
            throw new ToolException(ExitCodes.Usage, $"branch name '{branch}' is not valid for git");
        }

        var store = new EpicStore(new StateLayout(StatePaths.Resolve(repo, config.StateDir)));
        var exists = git.RefExists(GitRunner.HeadsRef(branch));
        if (store.Find(id) is { } record)
        {
            return record.Branch == branch && record.State == EpicStates.Open && exists
                ? Result(record, created: false)
                : throw new ToolException(ExitCodes.BadInput, $"epic '{id}' already exists as '{record.Branch}' ({record.State})", "use another id");
        }

        if (exists)
        {
            throw new ToolException(ExitCodes.BadInput, $"branch '{branch}' already exists", "use another slug, or delete the branch");
        }

        var baseBranch = from ?? config.BaseBranch;
        if (!git.RefExists(GitRunner.HeadsRef(baseBranch)))
        {
            throw new ToolException(ExitCodes.BadInput, $"base branch '{baseBranch}' not found");
        }

        var baseCommit = git.RevParse(GitRunner.HeadsRef(baseBranch));
        git.Run("branch", branch, baseCommit);
        var created = new EpicRecord(SwarmJson.SchemaVersion, id, slug, branch, baseBranch, baseCommit, DateTime.UtcNow, EpicStates.Open, null, null, null);
        store.Save(created);
        return Result(created, created: true);
    }

    EpicOpenResult Result(EpicRecord r, bool created)
    {
        var batchEpic = EpicNaming.BatchEpicId(config, r.Branch);
        IReadOnlyList<string> warnings = batchEpic is null
            ? [$"batch cannot address '{r.Branch}' with epicBranchTemplate '{config.EpicBranchTemplate}'; change epicBranchTemplate (e.g. to the epic branch prefix + {{epic}})"]
            : [];
        return new EpicOpenResult(SwarmJson.SchemaVersion, created, r.Id, r.Slug, r.Branch, r.BaseBranch, r.BaseCommit, batchEpic, warnings);
    }
}
