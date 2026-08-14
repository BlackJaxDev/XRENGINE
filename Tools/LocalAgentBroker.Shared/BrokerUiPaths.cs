namespace XREngine.LocalAgentBroker.Shared;

/// <summary>
/// Resolves the checkout-local, ignored storage used by the broker tray companion.
/// </summary>
public sealed class BrokerUiPaths
{
    public BrokerUiPaths(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        RootDirectory = Path.Combine(
            RepositoryRoot,
            "Build",
            "_AgentValidation",
            "00000000-000000-shared",
            "local-agent-broker-ui");
        RunsDirectory = Path.Combine(RootDirectory, "runs");
        SettingsPath = Path.Combine(RootDirectory, "settings.json");
    }

    public string RepositoryRoot { get; }

    public string RootDirectory { get; }

    public string RunsDirectory { get; }

    public string SettingsPath { get; }
}
