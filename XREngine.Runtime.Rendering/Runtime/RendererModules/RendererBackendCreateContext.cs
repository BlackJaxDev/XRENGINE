namespace XREngine.Rendering;

/// <summary>
/// Stable input passed to all renderer backend factories.
/// </summary>
public readonly partial record struct RendererBackendCreateContext
{
    /// <summary>
    /// Creates a context for a target which deliberately has no window host.
    /// </summary>
    public RendererBackendCreateContext(
        IRendererPresentationTarget target,
        bool linkRendererToWindow = false,
        long moduleGeneration = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        Target = target;
        LinkRendererToWindow = linkRendererToWindow;
        ModuleGeneration = moduleGeneration;
    }

    public IRendererPresentationTarget Target { get; init; }

    public bool LinkRendererToWindow { get; init; }

    public long ModuleGeneration { get; init; }

    /// <summary>Compatibility alias for code written during the target-contract transition.</summary>
    public IRendererPresentationTarget EffectiveTarget => Target;

    /// <summary>
    /// Freezes the factory input into the stable context owned by the created
    /// renderer instance.
    /// </summary>
    public RendererHostContext ToRendererHostContext()
        => new(Target, LinkRendererToWindow, ModuleGeneration);
}
