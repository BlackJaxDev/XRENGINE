using System.Text.Json;
using XREngine.Rendering.Profiling;
using XREngine.Runtime.Automation.Mcp;

namespace XREngine.Runtime.Automation.Profiling;

/// <summary>Runtime render-profile tools shared by RenderBench and editor hosts.</summary>
public sealed class RenderProfileMcpToolBundle : IMcpToolBundle
{
    private const McpCapability ProfileCapability = McpCapability.ProfilerSession;
    private static readonly object s_emptySchema = ObjectSchema(new Dictionary<string, object>());

    public IEnumerable<McpToolDefinition> GetTools()
    {
        yield return Tool("list_render_profile_targets", "Lists supported and unsupported render profiling targets.", s_emptySchema, ListTargetsAsync);
        yield return Tool("load_render_profile_recipe", "Loads and validates a versioned JSON or JSONC render profile recipe.",
            ObjectSchema(new Dictionary<string, object> { ["recipe_json"] = StringSchema("Complete JSON or JSONC recipe text.") }, ["recipe_json"]),
            LoadRecipeAsync, McpPermissionLevel.Mutating);
        yield return Tool("prepare_render_profile", "Begins asynchronous preparation and stabilization of a loaded recipe.",
            SessionSchema("recipe_id", "Loaded recipe identifier."), PrepareAsync, McpPermissionLevel.Mutating);
        yield return Tool("wait_render_profile_ready", "Waits outside the measured interval for preparation and stabilization.",
            ObjectSchema(new Dictionary<string, object>
            {
                ["session_id"] = StringSchema("Profile session identifier."),
                ["timeout_seconds"] = IntegerSchema("Maximum wait duration.", 1),
            }, ["session_id"]), WaitReadyAsync);
        yield return Tool("arm_render_profile", "Arms a prepared profile at an exact engine/render frame boundary.",
            ObjectSchema(new Dictionary<string, object>
            {
                ["session_id"] = StringSchema("Profile session identifier."),
                ["frame_id"] = IntegerSchema("Exact first measured engine/render frame identifier.", 0),
            }, ["session_id"]), ArmAsync, McpPermissionLevel.Mutating);
        yield return Tool("start_render_profile", "Starts an armed profile after the RPC response and suspends MCP for capture and GPU drainage.",
            SessionSchema("session_id", "Profile session identifier."), StartAsync, McpPermissionLevel.Mutating);
        yield return Tool("stop_render_profile", "Stops a capture at the next frame boundary or cancels a profile which has not started.",
            SessionSchema("session_id", "Profile session identifier."), StopAsync, McpPermissionLevel.Mutating);
        yield return Tool("cancel_render_profile", "Cancels preparation, an armed session, capture, or drainage and restores known renderer state.",
            SessionSchema("session_id", "Profile session identifier."), CancelAsync, McpPermissionLevel.Mutating);
        yield return Tool("get_render_profile_status", "Gets buffered profile state without touching the renderer or render workers.",
            SessionSchema("session_id", "Profile session identifier."), GetStatusAsync);
        yield return Tool("get_render_profile_result", "Returns a completed profile result and artifact paths.",
            SessionSchema("session_id", "Profile session identifier."), GetResultAsync);
        yield return Tool("run_render_profile_matrix", "Creates a bounded asynchronous matrix over the recipe worker counts.",
            SessionSchema("recipe_id", "Loaded recipe identifier."), RunMatrixAsync, McpPermissionLevel.Mutating);
        yield return Tool("get_render_profile_matrix_status", "Gets buffered state for an asynchronous profile matrix.",
            SessionSchema("job_id", "Matrix job identifier."), GetMatrixStatusAsync);
        yield return Tool("cancel_render_profile_matrix", "Cancels a running or queued profile matrix.",
            SessionSchema("job_id", "Matrix job identifier."), CancelMatrixAsync, McpPermissionLevel.Mutating);
    }

    private static Task<McpToolResponse> ListTargetsAsync(McpToolContext context, JsonElement _, CancellationToken __)
        => SuccessAsync("Listed render profile targets.", context.GetRequiredService<RenderProfileControlService>().ListTargets());

    private static Task<McpToolResponse> LoadRecipeAsync(McpToolContext context, JsonElement arguments, CancellationToken _)
    {
        string json = GetRequiredString(arguments, "recipe_json");
        RenderProfileRecipeDescriptor descriptor = context.GetRequiredService<RenderProfileControlService>().LoadRecipe(json);
        return SuccessAsync("Loaded and validated render profile recipe.", descriptor);
    }

    private static Task<McpToolResponse> PrepareAsync(McpToolContext context, JsonElement arguments, CancellationToken _)
    {
        RenderProfileControlService service = context.GetRequiredService<RenderProfileControlService>();
        string sessionId = service.Prepare(GetRequiredString(arguments, "recipe_id"));
        return SuccessAsync("Render profile preparation started.", new { session_id = sessionId, status = service.GetStatus(sessionId) });
    }

    private static async Task<McpToolResponse> WaitReadyAsync(McpToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        string sessionId = GetRequiredString(arguments, "session_id");
        int seconds = GetOptionalInt32(arguments, "timeout_seconds") ?? 30;
        RenderProfileStatus status = await context.GetRequiredService<RenderProfileControlService>()
            .WaitReadyAsync(sessionId, TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        return new McpToolResponse("Render profile is ready to arm.", status);
    }

    private static Task<McpToolResponse> ArmAsync(McpToolContext context, JsonElement arguments, CancellationToken _)
    {
        RenderProfileStatus status = context.GetRequiredService<RenderProfileControlService>().Arm(
            GetRequiredString(arguments, "session_id"),
            GetOptionalInt64(arguments, "frame_id"));
        return SuccessAsync("Render profile armed at the requested frame boundary.", status);
    }

    private static Task<McpToolResponse> StartAsync(McpToolContext context, JsonElement arguments, CancellationToken _)
    {
        string sessionId = GetRequiredString(arguments, "session_id");
        RenderProfileControlService service = context.GetRequiredService<RenderProfileControlService>();
        RenderProfileStartOperation operation = service.CreateStartOperation(sessionId);
        return Task.FromResult(new McpToolResponse(
            "Render profile accepted. MCP will resume after capture and delayed query drainage.",
            new { session_id = sessionId, accepted = true },
            AfterResponse: operation.StartAfterResponse,
            SuspendTransportUntil: operation.Completion));
    }

    private static async Task<McpToolResponse> StopAsync(McpToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        string sessionId = GetRequiredString(arguments, "session_id");
        RenderProfileControlService service = context.GetRequiredService<RenderProfileControlService>();
        RenderProfileStatus status = service.GetStatus(sessionId);
        if (status.State == RenderProfileState.Capturing)
            status = service.Stop(sessionId);
        else if (status.State is not (RenderProfileState.Completed or RenderProfileState.Failed or RenderProfileState.Cancelled))
        {
            await service.CancelAsync(sessionId, cancellationToken).ConfigureAwait(false);
            status = service.GetStatus(sessionId);
        }
        return new McpToolResponse("Render profile stop request applied.", status);
    }

    private static async Task<McpToolResponse> CancelAsync(McpToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        string sessionId = GetRequiredString(arguments, "session_id");
        RenderProfileControlService service = context.GetRequiredService<RenderProfileControlService>();
        await service.CancelAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new McpToolResponse("Render profile cancelled.", service.GetStatus(sessionId));
    }

    private static Task<McpToolResponse> GetStatusAsync(McpToolContext context, JsonElement arguments, CancellationToken _)
        => SuccessAsync("Retrieved render profile status.", context.GetRequiredService<RenderProfileControlService>()
            .GetStatus(GetRequiredString(arguments, "session_id")));

    private static Task<McpToolResponse> GetResultAsync(McpToolContext context, JsonElement arguments, CancellationToken _)
        => SuccessAsync("Retrieved completed render profile result.", context.GetRequiredService<RenderProfileControlService>()
            .GetResult(GetRequiredString(arguments, "session_id")));

    private static Task<McpToolResponse> RunMatrixAsync(McpToolContext context, JsonElement arguments, CancellationToken _)
    {
        (RenderProfileMatrixStatus status, RenderProfileStartOperation operation) = context
            .GetRequiredService<RenderProfileControlService>()
            .CreateMatrix(GetRequiredString(arguments, "recipe_id"));
        return Task.FromResult(new McpToolResponse(
            "Render profile matrix accepted. MCP is suspended only for each measured child interval.",
            status,
            AfterResponse: operation.StartAfterResponse));
    }

    private static Task<McpToolResponse> GetMatrixStatusAsync(McpToolContext context, JsonElement arguments, CancellationToken _)
        => SuccessAsync("Retrieved render profile matrix status.", context.GetRequiredService<RenderProfileControlService>()
            .GetMatrixStatus(GetRequiredString(arguments, "job_id")));

    private static async Task<McpToolResponse> CancelMatrixAsync(McpToolContext context, JsonElement arguments, CancellationToken _)
    {
        string jobId = GetRequiredString(arguments, "job_id");
        RenderProfileControlService service = context.GetRequiredService<RenderProfileControlService>();
        await service.CancelMatrixAsync(jobId).ConfigureAwait(false);
        return new McpToolResponse("Render profile matrix cancelled.", service.GetMatrixStatus(jobId));
    }

    private static McpToolDefinition Tool(
        string name,
        string description,
        object schema,
        Func<McpToolContext, JsonElement, CancellationToken, Task<McpToolResponse>> handler,
        McpPermissionLevel permission = McpPermissionLevel.ReadOnly)
        => new(name, description, schema, handler, ProfileCapability, permission);

    private static object SessionSchema(string name, string description)
        => ObjectSchema(new Dictionary<string, object> { [name] = StringSchema(description) }, [name]);

    private static object ObjectSchema(Dictionary<string, object> properties, string[]? required = null)
        => new { type = "object", properties, required = required ?? [], additionalProperties = false };

    private static object StringSchema(string description) => new { type = "string", description };
    private static object IntegerSchema(string description, long minimum) => new { type = "integer", minimum, description };

    private static string GetRequiredString(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new ArgumentException($"'{name}' must be a non-empty string.");

    private static int? GetOptionalInt32(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : null;

    private static long? GetOptionalInt64(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result)
            ? result
            : null;

    private static Task<McpToolResponse> SuccessAsync(string message, object value)
        => Task.FromResult(new McpToolResponse(message, value));
}
