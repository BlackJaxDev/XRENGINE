namespace XREngine.Rendering;

/// <summary>
/// Stable discovery metadata published by a renderer backend module.
/// </summary>
public sealed record RendererBackendMetadata
{
    public RendererBackendMetadata(
        RendererBackendId id,
        RuntimeGraphicsApiKind graphicsApi,
        string displayName,
        Version version,
        RendererBackendCapabilities capabilities,
        RendererBackendReloadLimitations reloadLimitations,
        string? reloadLimitationDescription = null)
    {
        if (id.Value.Length == 0)
            throw new ArgumentException("A renderer backend module must have a non-empty stable identifier.", nameof(id));
        if (graphicsApi == RuntimeGraphicsApiKind.Unknown)
            throw new ArgumentOutOfRangeException(nameof(graphicsApi), "A renderer backend module must identify a concrete graphics API.");

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(version);

        if (reloadLimitations != RendererBackendReloadLimitations.None &&
            string.IsNullOrWhiteSpace(reloadLimitationDescription))
        {
            throw new ArgumentException(
                "Renderer modules with reload limitations must provide an actionable limitation description.",
                nameof(reloadLimitationDescription));
        }

        Id = id;
        GraphicsApi = graphicsApi;
        DisplayName = displayName;
        Version = version;
        Capabilities = capabilities;
        ReloadLimitations = reloadLimitations;
        ReloadLimitationDescription = reloadLimitationDescription;
    }

    public RendererBackendId Id { get; }

    public RuntimeGraphicsApiKind GraphicsApi { get; }

    public string DisplayName { get; }

    public Version Version { get; }

    public RendererBackendCapabilities Capabilities { get; }

    public RendererBackendReloadLimitations ReloadLimitations { get; }

    public string? ReloadLimitationDescription { get; }

    /// <summary>
    /// Returns whether every requested capability is declared by this module.
    /// </summary>
    public bool Supports(RendererBackendCapabilities requiredCapabilities)
        => (Capabilities & requiredCapabilities) == requiredCapabilities;
}
