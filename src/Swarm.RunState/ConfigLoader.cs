using System.Text.Json;
using System.Text.Json.Serialization;
using Swarm.Git;

namespace Swarm.RunState;

/// <summary>Loads, merges and validates <see cref="SwarmConfig"/>.</summary>
public static class ConfigLoader
{
    /// <summary>Default config location, relative to the main worktree.</summary>
    public const string DefaultRelativePath = ".swarm/batch.json";

    const string Hint = "see docs/batch-tools.md#configuration";

    static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
    };

    /// <summary>Loads config: explicit file, else the main worktree's <c>.swarm/batch.json</c>, else defaults; then flags; then validation.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="explicitPath">Absolute path from <c>--config</c>, or null.</param>
    /// <param name="overrides">Flag values.</param>
    /// <returns>The validated config.</returns>
    /// <exception cref="ToolException">Missing explicit file, invalid JSON or invalid values (exit code 2).</exception>
    public static SwarmConfig Load(RepoPaths repo, string? explicitPath, ConfigOverrides overrides)
    {
        string source;
        SwarmConfig config;
        if (explicitPath is not null)
        {
            source = Path.GetFullPath(explicitPath);
            if (!File.Exists(source))
            {
                throw new ToolException(ExitCodes.Usage, $"{source}: config file not found", Hint);
            }

            config = Parse(File.ReadAllText(source), source);
        }
        else
        {
            var path = Path.Combine(repo.MainWorktreeRoot, ".swarm", "batch.json");
            source = File.Exists(path) ? path : "defaults";
            config = File.Exists(path) ? Parse(File.ReadAllText(path), path) : new SwarmConfig();
        }

        return Validated(overrides.ApplyTo(config), source);
    }

    /// <summary>Parses config JSON strictly (unknown keys and nulls rejected; comments and trailing commas allowed).</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="sourceName">Name used in messages.</param>
    /// <returns>The parsed config (not yet validated).</returns>
    /// <exception cref="ToolException">Invalid JSON (exit code 2).</exception>
    public static SwarmConfig Parse(string json, string sourceName)
    {
        try
        {
            return JsonSerializer.Deserialize<SwarmConfig>(json, Strict)
                ?? throw new ToolException(ExitCodes.Usage, $"{sourceName}: invalid config: must be a JSON object", Hint);
        }
        catch (JsonException e)
        {
            throw new ToolException(ExitCodes.Usage, $"{sourceName}: invalid config: {e.Message}", Hint);
        }
    }

    /// <summary>Lists every validation error.</summary>
    /// <param name="c">The config.</param>
    /// <returns>Errors, empty when valid.</returns>
    public static IReadOnlyList<string> Check(SwarmConfig c)
    {
        var e = new List<string>();
        if (c.SchemaVersion != 1)
        {
            e.Add($"schemaVersion must be 1 (got {c.SchemaVersion})");
        }

        if (c.Slots < 1)
        {
            e.Add($"slots must be >= 1 (got {c.Slots})");
        }

        var b = c.Batch;
        if (!(b.Min >= 1 && b.Min <= b.Start && b.Start <= b.Max && b.Max <= 64))
        {
            e.Add($"batch sizes must satisfy 1 <= min <= start <= max <= 64 (got min {b.Min}, start {b.Start}, max {b.Max})");
        }

        if (c.HeartbeatSec < 1)
        {
            e.Add($"heartbeatSec must be >= 1 (got {c.HeartbeatSec})");
        }
        else if (c.ExpirySec < 3 * c.HeartbeatSec)
        {
            e.Add($"expirySec must be >= 3 x heartbeatSec (got expirySec {c.ExpirySec}, heartbeatSec {c.HeartbeatSec})");
        }

        if (c.PollMs is < 10 or > 60000)
        {
            e.Add($"pollMs must be between 10 and 60000 (got {c.PollMs})");
        }

        if (c.MaxWaitSec < 0)
        {
            e.Add($"maxWaitSec must be >= 0 (got {c.MaxWaitSec})");
        }

        if (c.TestCommand is null || c.TestCommand.Count == 0 || c.TestCommand.Any(string.IsNullOrWhiteSpace))
        {
            e.Add("testCommand must be a non-empty array of non-empty strings");
        }

        if (!SafeName.IsValid(c.Epic))
        {
            e.Add($"epic must be a safe name: {SafeName.Description} (got '{c.Epic}')");
        }

        if (c.EpicBranchTemplate is null || !c.EpicBranchTemplate.Contains("{epic}", StringComparison.Ordinal))
        {
            e.Add($"epicBranchTemplate must contain {{epic}} (got '{c.EpicBranchTemplate}')");
        }

        if (string.IsNullOrWhiteSpace(c.BaseBranch) || c.BaseBranch.StartsWith('-') || c.BaseBranch.Any(char.IsWhiteSpace))
        {
            e.Add($"baseBranch must be a branch name (got '{c.BaseBranch}')");
        }

        if (c.MaxRebaseAttempts is < 0 or > 3)
        {
            e.Add($"maxRebaseAttempts must be between 0 and 3 (got {c.MaxRebaseAttempts})");
        }

        if (c.KeepRuns < 1)
        {
            e.Add($"keepRuns must be >= 1 (got {c.KeepRuns})");
        }

        if (string.IsNullOrWhiteSpace(c.StateDir))
        {
            e.Add("stateDir must not be empty");
        }

        if (c.WorktreeRoot is { } w && !Path.IsPathFullyQualified(w))
        {
            e.Add($"worktreeRoot must be an absolute path (got '{w}')");
        }

        if (c.Lander is not (LanderNames.Squash or LanderNames.FastForward))
        {
            e.Add($"lander must be '{LanderNames.Squash}' or '{LanderNames.FastForward}' (got '{c.Lander}')");
        }

        e.AddRange(SquashConfig.Check(c.Squash));
        e.AddRange(BranchTemplate.Check(c.Worktree, "worktree"));
        e.AddRange(BranchTemplate.Check(c.EpicTool, "epicTool"));

        return e;
    }

    /// <summary>Validates and returns the config.</summary>
    /// <param name="config">The config.</param>
    /// <param name="sourceName">Name used in messages.</param>
    /// <returns><paramref name="config"/>.</returns>
    /// <exception cref="ToolException">The first validation error (exit code 2).</exception>
    public static SwarmConfig Validated(SwarmConfig config, string sourceName)
    {
        var errors = Check(config);
        return errors.Count == 0 ? config : throw new ToolException(ExitCodes.Usage, $"{sourceName}: {errors[0]}", Hint);
    }
}
