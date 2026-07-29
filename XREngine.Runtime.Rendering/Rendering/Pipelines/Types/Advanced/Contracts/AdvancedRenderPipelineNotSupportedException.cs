namespace XREngine.Rendering;

/// <summary>
/// Thrown before pipeline construction when advanced rendering is explicitly required
/// and the active backend cannot satisfy its capability contract.
/// </summary>
public sealed class AdvancedRenderPipelineNotSupportedException : InvalidOperationException
{
    public AdvancedRenderPipelineNotSupportedException(
        AdvancedRenderPipelineSelectionResult selectionResult)
        : base(selectionResult.Diagnostic)
    {
        SelectionResult = selectionResult;
    }

    public AdvancedRenderPipelineSelectionResult SelectionResult { get; }
}
