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
            Description = "Start one bounded OpenAI Responses API worker with exact model, reasoning, text-verbosity, and output-token controls. Omit editor_session for reasoning-only work, or name a local editor MCP session for controlled editor tools. Returns a run ID immediately. Optional background mode temporarily stores provider response state for polling.",
            InputSchema = StartRunSchema(),
        },
        new McpToolSpec
        {
            Name = "get_agent_run",
            Description = "Get incremental text, evidence, usage, requested response controls, exact model, provider-attempt diagnostics, observed time, elapsed time, progress stage, and terminal result for one run.",
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
            Description = "List bounded metadata, including observed time, elapsed time, and latest progress stage, for active and recently retained runs.",
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
            required: ["objective", "requested_model"],
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
            ("text_verbosity", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("low", "medium", "high"),
                ["default"] = "medium",
                ["description"] = "Responses API visible-text verbosity. max_output_tokens remains the hard combined visible-output and reasoning-token budget.",
            }),
            ("use_background_mode", new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = false,
                ["description"] = "Opt in to asynchronous Responses API execution and polling. Provider response data is temporarily stored for polling and is not Zero Data Retention compatible.",
            }),
            ("editor_session", StringSchema("Optional exact session created by Manage-McpEditorSession.ps1. Omit for a reasoning-only run with no local tools.")),
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
                    ["max_turns"] = IntegerSchema(1, 32, 3),
                    ["max_tool_calls"] = IntegerSchema(0, 256, 8),
                    ["max_output_tokens"] = IntegerSchema(16, 128_000, 4_096),
                    ["max_tool_result_bytes"] = IntegerSchema(1_024, 4_194_304, 262_144),
                    ["max_elapsed_seconds"] = IntegerSchema(1, 3_600, 120),
                    ["max_retries"] = IntegerSchema(0, 5, 1),
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
