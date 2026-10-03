namespace Swarm.Core;

/// <summary>Fluent C# front-end that assembles a validated <see cref="SwarmDefinition"/>.</summary>
public sealed class SwarmBuilder
{
    readonly string name;
    readonly string description;
    readonly List<Role> roles = [];
    readonly List<ToolDef> tools = [];
    readonly List<Gate> gates = [];
    IReadOnlyList<Stage> flow = [];

    SwarmBuilder(string name, string description) => (this.name, this.description) = (name, description);

    /// <summary>Starts a new swarm definition.</summary>
    /// <param name="name">Swarm name.</param>
    /// <param name="description">Swarm description.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="SwarmException">Thrown when an argument is null or the name is blank.</exception>
    public static SwarmBuilder Define(string name, string description) =>
        new(Name(name), description ?? throw new SwarmException("description must not be null"));

    /// <summary>Adds the code orchestrator role and its flow.</summary>
    /// <param name="name">Orchestrator role name.</param>
    /// <param name="flow">Stage entries: <c>name*</c>, <c>name</c>, <c>gate:x</c>, <c>tool:x</c>.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="SwarmException">Thrown when the name is blank or a flow entry is invalid.</exception>
    public SwarmBuilder Orchestrator(string name, params string[] flow)
    {
        name = Name(name);
        this.flow = FlowParser.Parse(flow ?? throw new SwarmException("flow must not be null"));
        roles.Add(new Role(name, RoleKind.Code, null, "", [], null, null, null, null, null, ""));
        return this;
    }

    /// <summary>Adds an LLM role.</summary>
    /// <param name="name">Role name.</param>
    /// <param name="cfg">Configures the role.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="SwarmException">Thrown when the name is blank, <paramref name="cfg"/> is null or a field is invalid.</exception>
    public SwarmBuilder Llm(string name, Action<RoleBuilder> cfg)
    {
        name = Name(name);
        if (cfg == null) throw new SwarmException($"configuration delegate for role '{name}' must not be null");
        var rb = new RoleBuilder(name);
        cfg(rb);
        roles.Add(rb.ToRole(this.name));
        return this;
    }

    /// <summary>Adds a dnx tool dependency.</summary>
    /// <param name="name">Tool name.</param>
    /// <param name="package">NuGet package id.</param>
    /// <param name="version">Exact pinned version.</param>
    /// <param name="args">Default arguments.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="SwarmException">Thrown when the name is blank or an argument is null.</exception>
    public SwarmBuilder Tool(string name, string package, string version, params string[] args)
    {
        name = Name(name);
        tools.Add(new ToolDef(name,
            package ?? throw new SwarmException($"package of tool '{name}' must not be null"),
            version ?? throw new SwarmException($"version of tool '{name}' must not be null"),
            [.. args ?? throw new SwarmException($"args of tool '{name}' must not be null")]));
        return this;
    }

    /// <summary>Adds a verification gate.</summary>
    /// <param name="name">Gate name.</param>
    /// <param name="kind">Gate kind.</param>
    /// <param name="tool">Optional backing tool name.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="SwarmException">Thrown when the name is blank or kind is null.</exception>
    public SwarmBuilder Gate(string name, string kind, string? tool = null)
    {
        name = Name(name);
        gates.Add(new Gate(name, kind ?? throw new SwarmException($"kind of gate '{name}' must not be null"), tool));
        return this;
    }

    /// <summary>Builds and validates the definition; may be called repeatedly.</summary>
    /// <returns>A validated definition that shares no mutable lists with the builder or other builds.</returns>
    /// <exception cref="SwarmException">Thrown with the first validation error.</exception>
    public SwarmDefinition Build() => Validator.Validated(new SwarmDefinition(name, description,
        [.. roles.Select(r => r with { Tools = [.. r.Tools] })],
        [.. tools.Select(t => t with { Args = [.. t.Args] })],
        [.. gates], [.. flow]));

    internal static string Name(string? name) =>
        string.IsNullOrWhiteSpace(name) ? throw new SwarmException("name must not be empty") : name.Trim();
}

/// <summary>Configures one LLM role for <see cref="SwarmBuilder.Llm"/>.</summary>
public sealed class RoleBuilder
{
    readonly string role;
    string? model, description, effort, isolation, escalateTo, context;
    string prompt = "";
    IReadOnlyList<string> tools = [];
    int? maxTurns;

    internal RoleBuilder(string role) => this.role = role;

    string NotNull(string? value, string field) => value ?? throw new SwarmException($"{field} of role '{role}' must not be null");

    /// <summary>Sets the model alias or claude-* id.</summary>
    /// <param name="value">The model.</param>
    /// <returns>This builder.</returns>
    public RoleBuilder Model(string value) { model = NotNull(value, "model"); return this; }

    /// <summary>Sets the role description.</summary>
    /// <param name="value">The description.</param>
    /// <returns>This builder.</returns>
    public RoleBuilder Description(string value) { description = NotNull(value, "description"); return this; }

    /// <summary>Sets the tools the role may use (entries are trimmed; empty entries are rejected).</summary>
    /// <param name="value">Tool names.</param>
    /// <returns>This builder.</returns>
    public RoleBuilder Tools(params string[] value)
    {
        if (value is null) throw new SwarmException($"tools of role '{role}' must not be null");
        tools = [.. value.Select(t => string.IsNullOrWhiteSpace(t) ? throw new SwarmException($"empty tool entry in role '{role}'") : t.Trim())];
        return this;
    }

    /// <summary>Sets the turn limit.</summary>
    /// <param name="value">A positive turn count.</param>
    /// <returns>This builder.</returns>
    public RoleBuilder MaxTurns(int value) { maxTurns = value; return this; }

    /// <summary>Sets the effort level.</summary>
    /// <param name="value">The effort.</param>
    /// <returns>This builder.</returns>
    public RoleBuilder Effort(string value) { effort = NotNull(value, "effort"); return this; }

    /// <summary>Sets the isolation mode.</summary>
    /// <param name="value">The isolation.</param>
    /// <returns>This builder.</returns>
    public RoleBuilder Isolation(string value) { isolation = NotNull(value, "isolation"); return this; }

    /// <summary>Sets the LLM role to escalate to.</summary>
    /// <param name="value">The role name.</param>
    /// <returns>This builder.</returns>
    public RoleBuilder EscalateTo(string value) { escalateTo = NotNull(value, "escalate-to"); return this; }

    /// <summary>Sets the context mode.</summary>
    /// <param name="value">The context mode.</param>
    /// <returns>This builder.</returns>
    public RoleBuilder Context(string value) { context = NotNull(value, "context"); return this; }

    /// <summary>Sets the role prompt.</summary>
    /// <param name="value">The prompt text.</param>
    /// <returns>This builder.</returns>
    public RoleBuilder Prompt(string value) { prompt = NotNull(value, "prompt"); return this; }

    internal Role ToRole(string swarm)
    {
        var turns = RoleFields.Check(role, effort, isolation, maxTurns?.ToString());
        return new Role(role, RoleKind.Llm, model, description ?? $"{role} role of {swarm}", tools, turns, effort, isolation, escalateTo, context, prompt.Trim());
    }
}
