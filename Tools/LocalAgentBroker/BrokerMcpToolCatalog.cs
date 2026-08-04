using System.Text.Json.Nodes;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Fixed, intentionally small MCP tool catalog for local agent orchestration.
/// </summary>
internal static class BrokerMcpToolCatalog
{
    public static IReadOnlyList<McpToolSpec> Tools { get; } =
    [
        new McpToolSpec
        {
            Name = "recommend_agent_route",
            Description = "Recommend Luna, Terra, or Sol under the XRENGINE routing policy. This never launches or switches a model.",
            IsReadOnly = true,
            InputSchema = ObjectSchema(
                required: ["objective"],
                ("objective", StringSchema("Task objective to classify.")),
                ("constraints", StringArraySchema("Relevant task constraints."))),
        },
        new McpToolSpec
        {
            Name = "start_agent_run",
            Description = "Start one explicitly modeled OpenAI Responses API worker against a named local editor MCP session. Returns a run ID immediately. Optional background mode temporarily stores provider response state for polling.",
            InputSchema = StartRunSchema(),
        },
        new McpToolSpec
        {
            Name = "get_agent_run",
            Description = "Get incremental text, evidence, usage, exact model, provider-attempt diagnostics, and terminal result for one run.",
            IsReadOnly = true,
            InputSchema = ObjectSchema(
                required: ["run_id"],
                ("run_id", StringSchema("Run ID returned by start_agent_run."))),
        },
        new McpToolSpec
        {
            Name = "cancel_agent_run",
            Description = "Cooperatively cancel one queued or running worker and any pending editor tool call.",
            InputSchema = ObjectSchema(
                required: ["run_id"],
                ("run_id", StringSchema("Run ID returned by start_agent_run."))),
        },
        new McpToolSpec
        {
            Name = "list_agent_runs",
            Description = "List bounded metadata for active and recently retained runs.",
            IsReadOnly = true,
            InputSchema = ObjectSchema(
                required: [],
                ("limit", new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 1,
                    ["maximum"] = 100,
                    ["default"] = 20,
                })),
        },
    ];

    private static JsonObject StartRunSchema()
        => ObjectSchema(
            required: ["objective", "requested_model", "editor_session"],
            ("objective", StringSchema("Concrete delegated objective.")),
            ("success_criteria", StringArraySchema("Observable completion criteria.")),
            ("constraints", StringArraySchema("Hard constraints and approval boundaries.")),
            ("requested_model", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    AgentModelCatalog.Luna,
                    AgentModelCatalog.Terra,
                    AgentModelCatalog.Sol),
            }),
            ("reasoning_effort", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("none", "low", "medium", "high", "xhigh", "max"),
                ["default"] = "medium",
            }),
            ("use_background_mode", new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = false,
                ["description"] = "Opt in to asynchronous Responses API execution and polling. Provider response data is temporarily stored for polling and is not Zero Data Retention compatible.",
            }),
            ("editor_session", StringSchema("Exact named session created by Manage-McpEditorSession.ps1.")),
            ("evidence_packet", new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["relevant_files_and_symbols"] = StringArraySchema("Relevant files and symbols."),
                    ["current_diff"] = StringSchema("Compact current-diff summary."),
                    ["commands_and_results"] = StringArraySchema("Commands and observed results."),
                    ["failed_hypotheses"] = StringArraySchema("Already ruled-out hypotheses."),
                    ["unresolved_questions"] = StringArraySchema("Open questions."),
                    ["next_decision"] = StringSchema("The next decision delegated to the worker."),
                },
            }),
            ("tool_policy", new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["allow_mutation"] = BooleanSchema(false),
                    ["allow_destructive"] = BooleanSchema(false),
                    ["require_mutation_evidence"] = BooleanSchema(true),
                    ["allowed_tools"] = StringArraySchema("Explicit tool allowlist. Required for mutation."),
                    ["denied_tools"] = StringArraySchema("Broker-side deny list."),
                },
            }),
            ("budget", new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["max_turns"] = IntegerSchema(1, 32, 10),
                    ["max_tool_calls"] = IntegerSchema(0, 256, 24),
                    ["max_output_tokens"] = IntegerSchema(16, 128_000, 8_192),
                    ["max_tool_result_bytes"] = IntegerSchema(1_024, 4_194_304, 262_144),
                    ["max_elapsed_seconds"] = IntegerSchema(1, 3_600, 300),
                    ["max_retries"] = IntegerSchema(0, 5, 2),
                    ["max_concurrency"] = IntegerSchema(1, 8, 1),
                },
            }),
            ("additional_instructions", StringSchema("Optional task-specific instructions.")));

    private static JsonObject ObjectSchema(
        string[] required,
        params (string Name, JsonObject Schema)[] properties)
    {
        var propertyObject = new JsonObject();
        foreach ((string name, JsonObject schema) in properties)
            propertyObject[name] = schema;
        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = propertyObject,
            ["required"] = new JsonArray(required.Select(static value => JsonValue.Create(value)).ToArray()),
        };
    }

    private static JsonObject StringSchema(string description)
        => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject StringArraySchema(string description)
        => new()
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new JsonObject { ["type"] = "string" },
            ["default"] = new JsonArray(),
        };

    private static JsonObject BooleanSchema(bool defaultValue)
        => new() { ["type"] = "boolean", ["default"] = defaultValue };

    private static JsonObject IntegerSchema(int minimum, int maximum, int defaultValue)
        => new()
        {
            ["type"] = "integer",
            ["minimum"] = minimum,
            ["maximum"] = maximum,
            ["default"] = defaultValue,
        };
}
