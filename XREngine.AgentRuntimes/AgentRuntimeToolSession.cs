using System.Text;
using System.Text.Json;
using LocalAgentBroker.Orchestration;

namespace XREngine.AgentRuntimes;

/// <summary>
/// Applies host authorization and evidence rules before and after each native
/// runtime tool call. The runtime never receives the unrestricted tool provider.
/// </summary>
internal sealed class AgentRuntimeToolSession(
    AgentRunRequest request,
    IAgentToolProvider provider,
    IAgentRunObserver observer)
{
    private readonly Dictionary<string, AgentToolDefinition> _tools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
    private readonly List<AgentToolEvidence> _evidence = [];

    public IReadOnlyCollection<AgentToolDefinition> Tools => _tools.Values;
    public IReadOnlyList<AgentToolEvidence> Evidence => _evidence;
    public int CallCount => _callIds.Count;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (AgentToolDefinition tool in await provider.ListToolsAsync(cancellationToken))
        {
            bool repositoryTool = request.RepositoryAccess.Enabled && tool.IsReadOnly
                && tool.Name is "repository_read_text" or "repository_search";
            if (request.ToolPolicy.DeniedTools.Contains(tool.Name, StringComparer.Ordinal)
                || (!repositoryTool && request.ToolPolicy.AllowedTools.Count > 0
                    && !request.ToolPolicy.AllowedTools.Contains(tool.Name, StringComparer.Ordinal))
                || (!tool.IsReadOnly && !request.ToolPolicy.AllowMutation)
                || (tool.IsDestructive && !request.ToolPolicy.AllowDestructive))
                continue;
            if (!_tools.TryAdd(tool.Name, tool))
                throw new AgentToolProviderException(AgentFailureCategory.ToolDiscovery, "Host tools have duplicate names.");
        }
        if (request.RequireToolUse && _tools.Count == 0)
            throw new AgentToolProviderException(AgentFailureCategory.ToolDiscovery, "The request requires tools, but no authorized tools are available.");
    }

    public async Task<AgentToolResult> ExecuteAsync(AgentToolCall call, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(call.Name, out AgentToolDefinition? definition))
            throw new AgentToolProviderException(AgentFailureCategory.ToolDenied, "The runtime requested a tool outside the host allowlist.");
        if (_callIds.Count >= request.Budget.MaxToolCalls)
            throw new AgentToolProviderException(AgentFailureCategory.BudgetExceeded, "The run exhausted its host tool-call budget.");
        if (string.IsNullOrWhiteSpace(call.CallId) || !_callIds.Add(call.CallId))
            throw new AgentToolProviderException(AgentFailureCategory.ToolDenied, "The runtime returned an empty or repeated tool-call identifier.");
        if (Encoding.UTF8.GetByteCount(call.ArgumentsJson) > request.Budget.MaxToolResultBytes)
            throw new AgentToolProviderException(AgentFailureCategory.BudgetExceeded, "The tool arguments exceed the host message budget.");
        using (JsonDocument arguments = JsonDocument.Parse(call.ArgumentsJson))
        {
            if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                throw new AgentToolProviderException(AgentFailureCategory.ToolDenied, "Tool arguments must be a JSON object.");
        }
        await observer.OnEventAsync(new AgentRunEvent
        {
            Kind = AgentRunEventKind.ToolStarted, CallId = call.CallId, ToolName = call.Name, Message = call.ArgumentsJson,
        }, cancellationToken);

        AgentToolResult result = await provider.ExecuteAsync(call, cancellationToken);
        long bytes = (long)Encoding.UTF8.GetByteCount(result.Content)
            + Encoding.UTF8.GetByteCount(result.ImageDataUri ?? string.Empty);
        bool oversized = bytes > request.Budget.MaxToolResultBytes;
        if (oversized)
            result = new AgentToolResult { IsError = true, Content = "The tool result exceeds the host result-size budget." };
        var evidence = new AgentToolEvidence
        {
            CallId = call.CallId,
            ToolName = call.Name,
            ArgumentsSummary = Summarize(call.ArgumentsJson, 1024),
            ResultSummary = Summarize(result.Content, 4096),
            IsError = result.IsError,
            IsMutation = !definition.IsReadOnly,
            IsVisualEvidence = result.ImageDataUri is not null || result.ImagePath is not null,
            EvidencePath = result.ImagePath,
        };
        _evidence.Add(evidence);
        await observer.OnEventAsync(new AgentRunEvent
        {
            Kind = AgentRunEventKind.ToolCompleted, CallId = call.CallId, ToolName = call.Name,
            ToolResult = result, ToolEvidence = evidence,
        }, cancellationToken);
        if (oversized)
            throw new AgentToolProviderException(AgentFailureCategory.ToolOutputTooLarge, "The tool result exceeds the host result-size budget.");
        return result;
    }

    public void ValidateCompletion()
    {
        if (request.RequireToolUse && _evidence.Count == 0)
            throw new AgentToolProviderException(AgentFailureCategory.ToolError, "The runtime completed without the required host tool call.");
        if (!request.ToolPolicy.RequireMutationEvidence)
            return;
        int lastMutation = _evidence.FindLastIndex(static entry => entry.IsMutation);
        if (lastMutation < 0)
            return;
        for (int index = lastMutation + 1; index < _evidence.Count; index++)
        {
            AgentToolEvidence entry = _evidence[index];
            if (!entry.IsError && !entry.IsMutation && IsVerification(entry.ToolName))
                return;
        }
        throw new AgentToolProviderException(AgentFailureCategory.MutationEvidenceMissing, "A mutation completed without a later read-back or capture tool call.");
    }

    private static bool IsVerification(string name)
        => name.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("list_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("read_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("find_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("capture_", StringComparison.OrdinalIgnoreCase)
            || name.Contains("inspect", StringComparison.OrdinalIgnoreCase)
            || name.Contains("validate", StringComparison.OrdinalIgnoreCase)
            || name.Contains("query", StringComparison.OrdinalIgnoreCase)
            || name.Contains("screenshot", StringComparison.OrdinalIgnoreCase);

    private static string Summarize(string value, int maximum)
        => value.Length <= maximum ? value : value[..maximum] + "…";
}
