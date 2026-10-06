using System.Text.RegularExpressions;
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>Branch naming settings shared by the <c>worktree</c> and <c>epicTool</c> config sections.</summary>
public interface IBranchNaming
{
    /// <summary>Gets the template; placeholders <c>{id}</c>, <c>{slug}</c> and optional <c>{kind}</c>.</summary>
    string BranchTemplate { get; }

    /// <summary>Gets the kind used for <c>{kind}</c> when none is given (e.g. <c>feature</c>).</summary>
    string? DefaultKind { get; }

    /// <summary>Gets the allowed branch prefixes (each ends with '/'); null or empty allows any.</summary>
    IReadOnlyList<string>? AllowedPrefixes { get; }
}

// Separate record types (not one shared type) so a partial JSON section keeps its own default template:
// System.Text.Json builds a new instance from the type's defaults, not from the property initializer.

/// <summary>The <c>worktree</c> config section (task branches).</summary>
public sealed record WorktreeSection : IBranchNaming
{
    /// <inheritdoc/>
    public string BranchTemplate { get; init; } = "task/{id}-{slug}";

    /// <inheritdoc/>
    public string? DefaultKind { get; init; }

    /// <inheritdoc/>
    public IReadOnlyList<string>? AllowedPrefixes { get; init; }
}

/// <summary>The <c>epicTool</c> config section (epic branches). Not named <c>epic</c>: that key is the batch epic id.</summary>
public sealed record EpicSection : IBranchNaming
{
    /// <inheritdoc/>
    public string BranchTemplate { get; init; } = "epic/{id}-{slug}";

    /// <inheritdoc/>
    public string? DefaultKind { get; init; }

    /// <inheritdoc/>
    public IReadOnlyList<string>? AllowedPrefixes { get; init; }
}

/// <summary>Renders and validates branch names from an <see cref="IBranchNaming"/> section.</summary>
public static partial class BranchTemplate
{
    /// <summary>Longest accepted slug.</summary>
    public const int MaxSlugLength = 40;

    /// <summary>Hint attached to prefix errors.</summary>
    public const string CiPrefixHint = "example-org pipelines trigger only on the full words feature/ and bugfix/; feat/, fix/ and other short forms silently break CI";

    static readonly string[] Known = ["{id}", "{slug}", "{kind}"];

    /// <summary>Checks a slug: lowercase letters and digits in hyphen-separated words, at most <see cref="MaxSlugLength"/> chars.</summary>
    /// <param name="slug">The candidate.</param>
    /// <returns>True when valid.</returns>
    public static bool IsValidSlug(string? slug) => slug is { Length: > 0 and <= MaxSlugLength } && SlugPattern().IsMatch(slug);

    /// <summary>Checks a branch against the allowed prefixes (ordinal, case-sensitive).</summary>
    /// <param name="branch">Branch name.</param>
    /// <param name="allowed">Allowed prefixes; null or empty allows any.</param>
    /// <returns>True when allowed.</returns>
    public static bool HasAllowedPrefix(string branch, IReadOnlyList<string>? allowed) =>
        allowed is null || allowed.Count == 0 || allowed.Any(p => branch.StartsWith(p, StringComparison.Ordinal));

    /// <summary>Lists configuration errors for one section.</summary>
    /// <param name="naming">The section.</param>
    /// <param name="section">Section name used in messages (<c>worktree</c> or <c>epicTool</c>).</param>
    /// <returns>Errors, empty when valid.</returns>
    public static IReadOnlyList<string> Check(IBranchNaming naming, string section)
    {
        var e = new List<string>();
        var t = naming.BranchTemplate;
        if (string.IsNullOrWhiteSpace(t) || !t.Contains("{id}", StringComparison.Ordinal) || !t.Contains("{slug}", StringComparison.Ordinal))
        {
            e.Add($"{section}.branchTemplate must contain {{id}} and {{slug}} (got '{t}')");
            return e;
        }

        foreach (Match m in Placeholder().Matches(t))
        {
            if (!Known.Contains(m.Value, StringComparer.Ordinal))
            {
                e.Add($"{section}.branchTemplate has unknown placeholder '{m.Value}' (allowed: {{id}}, {{slug}}, {{kind}})");
            }
        }

        if (naming.DefaultKind is { } k && !KindPattern().IsMatch(k))
        {
            e.Add($"{section}.defaultKind must be lowercase letters (got '{k}')");
        }
        else if (t.Contains("{kind}", StringComparison.Ordinal) && naming.DefaultKind is null)
        {
            e.Add($"{section}.defaultKind is required when branchTemplate uses {{kind}}");
        }

        foreach (var p in naming.AllowedPrefixes ?? [])
        {
            if (string.IsNullOrWhiteSpace(p) || !p.EndsWith('/') || p.Any(char.IsWhiteSpace))
            {
                e.Add($"{section}.allowedPrefixes entries must end with '/' (got '{p}')");
            }
        }

        if (e.Count == 0)
        {
            var sample = Fill(t, "1", "x", naming.DefaultKind);
            if (!HasAllowedPrefix(sample, naming.AllowedPrefixes))
            {
                e.Add($"{section}.branchTemplate renders '{sample}', which does not start with an allowed prefix ({string.Join(", ", naming.AllowedPrefixes!)}); {CiPrefixHint}");
            }
        }

        return e;
    }

    /// <summary>Renders a branch name.</summary>
    /// <param name="naming">The section (already validated by the config loader).</param>
    /// <param name="id">Ticket or epic id (a safe name).</param>
    /// <param name="slug">Slug (see <see cref="IsValidSlug"/>).</param>
    /// <param name="kind">Kind for <c>{kind}</c>, or null for the default.</param>
    /// <returns>The branch name.</returns>
    /// <exception cref="ToolException">Invalid id, slug or kind, or a disallowed prefix (exit code 2).</exception>
    public static string Render(IBranchNaming naming, string id, string slug, string? kind)
    {
        if (!SafeName.IsValid(id))
        {
            throw new ToolException(ExitCodes.Usage, $"id '{id}' is not a safe name ({SafeName.Description})");
        }

        if (!IsValidSlug(slug))
        {
            throw new ToolException(ExitCodes.Usage, $"slug '{slug}' must be lowercase letters and digits in hyphen-separated words, at most {MaxSlugLength} chars", "e.g. login-form");
        }

        if (kind is not null)
        {
            if (!naming.BranchTemplate.Contains("{kind}", StringComparison.Ordinal))
            {
                throw new ToolException(ExitCodes.Usage, $"--kind given but branchTemplate '{naming.BranchTemplate}' has no {{kind}}");
            }

            if (!KindPattern().IsMatch(kind))
            {
                throw new ToolException(ExitCodes.Usage, $"kind '{kind}' must be lowercase letters");
            }
        }

        var branch = Fill(naming.BranchTemplate, id, slug, kind ?? naming.DefaultKind);
        return HasAllowedPrefix(branch, naming.AllowedPrefixes)
            ? branch
            : throw new ToolException(ExitCodes.Usage, $"branch '{branch}' does not start with an allowed prefix ({string.Join(", ", naming.AllowedPrefixes!)})", CiPrefixHint);
    }

    static string Fill(string template, string id, string slug, string? kind) =>
        template.Replace("{id}", id, StringComparison.Ordinal).Replace("{slug}", slug, StringComparison.Ordinal).Replace("{kind}", kind ?? "", StringComparison.Ordinal);

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^[a-z]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex KindPattern();

    [GeneratedRegex(@"\{[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
