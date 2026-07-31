namespace XREngine.Rendering;

/// <summary>
/// Describes the output contract requested from a renderer backend before the backend is created.
/// It intentionally contains no native-window, input, or compositor lifecycle members.
/// </summary>
public interface IRendererPresentationTarget
{
    /// <summary>The presentation mode being requested.</summary>
    RenderExecutionMode ExecutionMode { get; }

    /// <summary>The module capability needed to create this target.</summary>
    RendererBackendCapabilities RequiredBackendCapabilities { get; }

    /// <summary>
    /// Fixed output properties, or <see langword="null"/> when a live presentation system owns
    /// the extent and format (for example a desktop window).
    /// </summary>
    RenderTargetOutputProperties? OutputProperties { get; }

    /// <summary>Validates the target before backend selection or native resource creation.</summary>
    void Validate();
}
