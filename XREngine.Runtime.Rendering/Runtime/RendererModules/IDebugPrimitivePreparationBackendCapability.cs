namespace XREngine.Rendering;

/// <summary>Prepares debug draws against the active render target without publishing them.</summary>
public interface IDebugPrimitivePreparationBackendCapability
{
    /// <summary>Gets whether the active pass has a supported framebuffer target.</summary>
    bool CanPrepareDebugPrimitives { get; }

    /// <summary>
    /// Captures the supplied draws in the active pass and prepares their graphics
    /// pipelines. The action must emit at most two point or line mesh requests.
    /// </summary>
    EDebugPrimitivePreparationStatus TryPrepareDebugPrimitives(
        Action emitDraws, int expectedRequestCount, bool idle, out string reason);
}
