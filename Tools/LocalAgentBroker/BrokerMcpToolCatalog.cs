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
            Description = "Start one bounded OpenAI Responses API worker with exact model, response controls, optional snapshotted repository context, opt-in read-only repository tools, and optional controlled editor tools. Returns a run ID immediately. Optional background mode temporarily stores provider response state for polling.",
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
                ["description"] = "Responses API visible-text verbosity. This is independent of the optional combined visible-output and reasoning-token limit.",
            }),
            ("use_background_mode", new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = false,
                ["description"] = "Opt in to asynchronous Responses API execution and polling. Provider response data is temporarily stored for polling and is not Zero Data Retention compatible.",
            }),
            ("require_tool_use", new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = false,
                ["description"] = "Require the first provider turn to call an available tool. Rejected when neither repository nor editor tools are configured.",
            }),
            ("editor_session", StringSchema("Optional exact session created by Manage-McpEditorSession.ps1. Omit when no editor tools are needed; repository tools are configured independently.")),
            ("context_files", new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Repository-relative UTF-8 text files snapshotted before the run is queued. Their contents are sent to the OpenAI API as untrusted context.",
                ["maxItems"] = 64,
                ["default"] = new JsonArray(),
                ["items"] = ObjectSchema(
                    required: ["path"],
                    ("path", StringSchema("Repository-relative text-file path. Absolute paths, traversal, reparse points, generated output, and sensitive file types are rejected.")),
                    ("start_line", IntegerSchema(1, int.MaxValue, defaultValue: null)),
                    ("end_line", IntegerSchema(1, int.MaxValue, defaultValue: null)),
                    ("expected_sha256", new JsonObject
                    {
                        ["type"] = "string",
                        ["pattern"] = "^[0-9A-Fa-f]{64}$",
                        ["description"] = "Optional SHA-256 of the complete raw file; a mismatch rejects the run.",
                    })),
            }),
            ("repository_access", new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["description"] = "Opt-in read-only repository_search and repository_read_text tools. Content returned by these tools is sent to the OpenAI API.",
                ["properties"] = new JsonObject
                {
                    ["enabled"] = BooleanSchema(false),
                    ["allowed_roots"] = StringArraySchema("Explicit repository-relative directories visible to repository tools. Use '.' only to authorize the whole eligible source tree."),
                },
            }),
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
                    ["max_output_tokens"] = IntegerSchema(
                        0,
                        128_000,
                        defaultValue: 0,
                        description: "Optional combined visible-output and reasoning-token limit. Zero or omission disables the broker limit and omits max_output_tokens from the provider request; the model/provider maximum still applies."),
                    ["max_tool_result_bytes"] = IntegerSchema(1_024, 4_194_304, 262_144),
                    ["max_context_files"] = IntegerSchema(0, 64, 16),
                    ["max_context_file_bytes"] = IntegerSchema(
                        1_024,
                        1_048_576,
                        262_144,
                        "Maximum raw size of one snapshotted context file."),
                    ["max_context_bytes"] = IntegerSchema(
                        1_024,
                        4_194_304,
                        1_048_576,
                        "Maximum aggregate raw size of all snapshotted context files."),
                    ["max_context_rendered_bytes"] = IntegerSchema(
                        1_024,
                        8_388_608,
                        2_097_152,
                        "Maximum UTF-8 size after context content and metadata are JSON-escaped into provider input blocks."),
                    ["max_elapsed_seconds"] = IntegerSchema(
                        0,
                        3_600,
                        defaultValue: 0,
                        description: "Optional whole-run elapsed-time limit. Zero or omission disables the broker timeout; explicit positive values remain hard limits."),
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

    private static JsonObject IntegerSchema(
        int minimum,
        int maximum,
        int? defaultValue,
        string? description = null)
    {
        JsonObject schema = new()
        {
            ["type"] = "integer",
            ["minimum"] = minimum,
            ["maximum"] = maximum,
        };
        if (defaultValue.HasValue)
            schema["default"] = defaultValue.Value;
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return schema;
    }
}
