namespace XREngine.Rendering.Pipelines.Overlays;

/// <summary>The result of one patch.</summary>
/// <param name="OverlayIndex">The index of the overlay in <see cref="RenderPipeline.Overlays"/>.</param>
/// <param name="Patch">The patch: a <see cref="RenderCommandPatch"/>, a <see cref="RenderResourcePatch"/>, or a <see cref="PostProcessSchemaPatch"/>.</param>
/// <param name="Status">The result.</param>
/// <param name="Message">A text for the user, or an empty string.</param>
public sealed record RenderPipelinePatchResult(int OverlayIndex, object Patch, ERenderPipelinePatchStatus Status, string Message)
{
    /// <summary>Gets whether the patch blocks its overlay chain.</summary>
    public bool IsError => Status is ERenderPipelinePatchStatus.AnchorMissing or ERenderPipelinePatchStatus.Failed;
}
