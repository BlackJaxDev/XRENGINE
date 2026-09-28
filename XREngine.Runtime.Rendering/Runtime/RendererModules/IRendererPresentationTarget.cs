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
    /// Fixed or currently known output properties, or <see langword="null"/> when a live
    /// presentation system has not supplied a drawable extent and format.
    /// </summary>
    RenderTargetOutputProperties? OutputProperties { get; }

    /// <summary>Validates the target before backend selection or native resource creation.</summary>
    void Validate();
}
