using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering;

public readonly partial record struct RendererHostContext
{
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

}
