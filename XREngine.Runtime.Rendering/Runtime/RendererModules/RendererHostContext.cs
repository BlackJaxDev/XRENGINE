using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering;

/// <summary>
/// Stable target-first context owned by a renderer instance after backend
/// selection. It exposes desktop window services only when the selected target
/// explicitly provides them.
/// </summary>
public readonly record struct RendererHostContext
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

    /// <summary>Creates a context for a desktop target while preserving the legacy constructor contract.</summary>
    public static RendererHostContext CreateDesktop(
        IRuntimeRenderWindowHost window,
        bool linkRendererToDesktopWindow = true,
        long backendGeneration = 0)
        => new(
            new DesktopWindowRenderTarget(
                window ?? throw new ArgumentNullException(nameof(window))),
            linkRendererToDesktopWindow,
            backendGeneration);

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

    /// <summary>Attempts to resolve desktop services without making window state nullable elsewhere.</summary>
    public bool TryGetDesktopWindowHost([NotNullWhen(true)] out IRuntimeRenderWindowHost? window)
    {
        if (Target is IRendererDesktopWindowServices desktop)
        {
            window = desktop.Window;
            return true;
        }

        window = null;
        return false;
    }

    /// <summary>
    /// Gets required desktop services or throws at the mode boundary with an
    /// actionable diagnostic.
    /// </summary>
    public IRuntimeRenderWindowHost RequireDesktopWindowHost()
    {
        if (TryGetDesktopWindowHost(out IRuntimeRenderWindowHost? window))
            return window;

        throw new InvalidOperationException(
            $"Renderer execution mode '{ExecutionMode}' does not provide desktop window services. " +
            $"Guard window-only behavior with {nameof(HasDesktopWindowServices)} or {nameof(TryGetDesktopWindowHost)}.");
    }

    /// <summary>Gets a required concrete desktop host type.</summary>
    public TWindow RequireDesktopWindow<TWindow>()
        where TWindow : class, IRuntimeRenderWindowHost
    {
        IRuntimeRenderWindowHost window = RequireDesktopWindowHost();
        if (window is TWindow typedWindow)
            return typedWindow;

        throw new InvalidOperationException(
            $"Renderer execution mode '{ExecutionMode}' provides desktop host '{window.GetType().FullName}', " +
            $"but this renderer requires '{typeof(TWindow).FullName}'.");
    }

    /// <summary>Builds a target-safe identity that never requires a window title.</summary>
    public string BuildDiagnosticIdentity()
    {
        RenderTargetOutputProperties? output = OutputProperties;
        return output is { } properties
            ? $"{ExecutionMode}:{properties.Width}x{properties.Height}x{properties.Layers}"
            : ExecutionMode.ToString();
    }
}
