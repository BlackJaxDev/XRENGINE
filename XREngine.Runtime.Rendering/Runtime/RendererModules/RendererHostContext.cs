namespace XREngine.Rendering;

/// <summary>
/// Stable target-first context owned by a renderer instance after backend
/// selection. It exposes desktop window services only when the selected target
/// explicitly provides them.
/// </summary>
public readonly partial record struct RendererHostContext
{
    public RendererHostContext(
        IRendererPresentationTarget target,
        bool linkRendererToDesktopWindow = false,
        long backendGeneration = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        if (linkRendererToDesktopWindow && target is not IRendererDesktopWindowServices)
        {
            throw new ArgumentException(
                $"Renderer target '{target.ExecutionMode}' cannot link to a desktop window because it does not provide {nameof(IRendererDesktopWindowServices)}.",
                nameof(linkRendererToDesktopWindow));
        }

        Target = target;
        LinkRendererToDesktopWindow = linkRendererToDesktopWindow;
        BackendGeneration = backendGeneration;
    }

    /// <summary>Gets the presentation target selected for this renderer.</summary>
    public IRendererPresentationTarget Target { get; }

    /// <summary>Gets whether the renderer should participate in desktop-window ownership hooks.</summary>
    public bool LinkRendererToDesktopWindow { get; }

    /// <summary>Gets the backend module generation that owns renderer API wrappers.</summary>
    public long BackendGeneration { get; }

    /// <summary>Gets the execution mode without requiring a concrete target cast.</summary>
    public RenderExecutionMode ExecutionMode => Target.ExecutionMode;

    /// <summary>Gets fixed output properties when the target owns a fixed output.</summary>
    public RenderTargetOutputProperties? OutputProperties => Target.OutputProperties;

    /// <summary>Gets whether this target explicitly provides desktop window services.</summary>
    public bool HasDesktopWindowServices => Target is IRendererDesktopWindowServices;

    /// <summary>Builds a target-safe identity that never requires a window title.</summary>
    public string BuildDiagnosticIdentity()
    {
        RenderTargetOutputProperties? output = OutputProperties;
        return output is { } properties
            ? $"{ExecutionMode}:{properties.Width}x{properties.Height}x{properties.Layers}"
            : ExecutionMode.ToString();
    }
}
