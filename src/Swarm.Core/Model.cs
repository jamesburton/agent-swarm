namespace Swarm.Core;

/// <summary>Whether a role is deterministic code (the orchestrator) or an LLM agent.</summary>
public enum RoleKind { Code, Llm }

/// <summary>A swarm role.</summary>
/// <param name="Name">Unique role name.</param>
/// <param name="Kind">Code or LLM role.</param>
/// <param name="Model">Model alias or claude-* id (LLM roles).</param>
/// <param name="Description">Short description.</param>
/// <param name="Tools">Tool names the role may use.</param>
/// <param name="MaxTurns">Optional turn limit.</param>
/// <param name="Effort">Optional effort level.</param>
/// <param name="Isolation">Optional isolation mode.</param>
/// <param name="EscalateTo">Optional LLM role to escalate to.</param>
/// <param name="Context">Optional context mode.</param>
/// <param name="Prompt">Role prompt text.</param>
public record Role(string Name, RoleKind Kind, string? Model, string Description, IReadOnlyList<string> Tools,
    int? MaxTurns, string? Effort, string? Isolation, string? EscalateTo, string? Context, string Prompt);

/// <summary>A dnx tool dependency.</summary>
/// <param name="Name">Unique tool name.</param>
/// <param name="Package">NuGet package id.</param>
/// <param name="Version">Exact pinned version.</param>
/// <param name="Args">Default arguments.</param>
public record ToolDef(string Name, string Package, string Version, IReadOnlyList<string> Args);

/// <summary>A verification gate.</summary>
/// <param name="Name">Unique gate name.</param>
/// <param name="Kind">Gate kind.</param>
/// <param name="Tool">Optional backing tool name.</param>
public record Gate(string Name, string Kind, string? Tool);

/// <summary>The kind of a flow stage.</summary>
public enum StageType { Fanout, Role, Gate, Tool }

/// <summary>A flow stage.</summary>
/// <param name="Type">Stage type.</param>
/// <param name="Target">Name of the role, gate or tool it runs.</param>
public record Stage(StageType Type, string Target);

/// <summary>The canonical swarm definition.</summary>
/// <param name="Name">Swarm name.</param>
/// <param name="Description">Swarm description.</param>
/// <param name="Roles">Roles.</param>
/// <param name="Tools">Tools.</param>
/// <param name="Gates">Gates.</param>
/// <param name="Flow">Ordered flow stages.</param>
public record SwarmDefinition(string Name, string Description, IReadOnlyList<Role> Roles, IReadOnlyList<ToolDef> Tools,
    IReadOnlyList<Gate> Gates, IReadOnlyList<Stage> Flow);
