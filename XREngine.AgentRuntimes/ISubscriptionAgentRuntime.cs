using XREngine.AgentOrchestration;

namespace XREngine.AgentRuntimes;

/// <summary>Runs the installed client and reads its account and model capabilities.</summary>
public interface ISubscriptionAgentRuntime : IAgentRunExecutor
{
    Task<SubscriptionConnectionInfo> ProbeAsync(CancellationToken cancellationToken = default);
    Task<SubscriptionConnectionInfo> SignInAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default);
}
