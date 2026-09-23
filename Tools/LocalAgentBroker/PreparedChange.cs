using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>Validated proposal state held between swarm patch preflight and application.</summary>
internal sealed record PreparedChange(
    string Path,
    string FullPath,
    AgentContextFileSnapshot Snapshot,
    AgentSwarmCodeChange Change,
    bool IsNewFile);
