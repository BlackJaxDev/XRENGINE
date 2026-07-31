namespace XREngine.LocalAgentBroker;

/// <summary>
/// Validated named editor-session endpoint resolved from the repository session root.
/// </summary>
internal sealed record ResolvedEditorSession
{
    public required string Name { get; init; }

    public required Uri Endpoint { get; init; }

    public required string ManifestPath { get; init; }
}
