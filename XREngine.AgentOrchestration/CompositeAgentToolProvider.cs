namespace XREngine.AgentOrchestration;

/// <summary>
/// Combines independently authorized tool providers without merging policy.
/// </summary>
public sealed class CompositeAgentToolProvider : IAgentToolProvider
{
    private readonly IReadOnlyList<IAgentToolProvider> _providers;
    private IReadOnlyDictionary<string, IAgentToolProvider>? _providersByTool;

    public CompositeAgentToolProvider(params IAgentToolProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Length == 0 || providers.Any(static provider => provider is null))
            throw new ArgumentException("At least one non-null tool provider is required.", nameof(providers));

        _providers = providers;
    }

    public async Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(
        CancellationToken cancellationToken)
    {
        var definitions = new List<AgentToolDefinition>();
        var providersByTool = new Dictionary<string, IAgentToolProvider>(StringComparer.Ordinal);
        foreach (IAgentToolProvider provider in _providers)
        {
            IReadOnlyList<AgentToolDefinition> providerDefinitions =
                await provider.ListToolsAsync(cancellationToken);
            foreach (AgentToolDefinition definition in providerDefinitions)
            {
                if (!providersByTool.TryAdd(definition.Name, provider))
                {
                    throw new AgentToolProviderException(
                        AgentFailureCategory.ToolDiscovery,
                        $"Multiple local tool providers advertised '{definition.Name}'.");
                }

                definitions.Add(definition);
            }
        }

        _providersByTool = providersByTool;
        return definitions;
    }

    public Task<AgentToolResult> ExecuteAsync(
        AgentToolCall call,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, IAgentToolProvider> providersByTool = _providersByTool
            ?? throw new AgentToolProviderException(
                AgentFailureCategory.ToolDiscovery,
                "Tools must be listed before a composite tool can be called.");
        if (!providersByTool.TryGetValue(call.Name, out IAgentToolProvider? provider))
        {
            throw new AgentToolProviderException(
                AgentFailureCategory.ToolDenied,
                $"Tool '{call.Name}' is not available from the composed providers.");
        }

        return provider.ExecuteAsync(call, cancellationToken);
    }
}
