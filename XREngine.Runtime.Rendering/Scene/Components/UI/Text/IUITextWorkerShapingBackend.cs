namespace XREngine.Rendering.UI;

/// <summary>Shapes frozen requests concurrently without retained mutable request state.</summary>
public interface IUITextWorkerShapingBackend : IUITextShapingBackend
{
    /// <summary>Upper bound on temporary storage while shaping this request.</summary>
    long MaxScratchBytes(UITextShapingRequest request);
}
