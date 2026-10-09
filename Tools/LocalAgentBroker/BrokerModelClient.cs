using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Routes each exact model to its provider with a separate credential reader.
/// </summary>
internal sealed class BrokerModelClient(HttpClient httpClient, BrokerConfiguration configuration) : IAgentModelClient
{
    private readonly OpenAiResponsesModelClient _openAi = new(httpClient, configuration.ReadApiKey);
    private readonly AnthropicMessagesModelClient _anthropic = new(
        httpClient, configuration.ReadAnthropicApiKey, workspaceIdProvider: configuration.ReadAnthropicWorkspaceId);

    public Task<AgentModelTurnResult> CreateResponseAsync(
        AgentModelTurnRequest request,
        IAgentRunObserver observer,
        CancellationToken cancellationToken)
    {
        if (!AgentModelCatalog.IsApproved(request.Run.RequestedModel))
            throw new ArgumentException("The requested model is not approved.", nameof(request));

        IAgentModelClient provider = AgentModelCatalog.IsAnthropic(request.Run.RequestedModel) ? _anthropic : _openAi;
        return provider.CreateResponseAsync(request, observer, cancellationToken);
    }
}
