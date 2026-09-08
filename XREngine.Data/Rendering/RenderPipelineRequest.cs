namespace XREngine.Data.Rendering;

/// <summary>
/// Describes the output ownership and view topology required from a new render pipeline.
/// </summary>
/// <param name="Purpose">The output that will own the pipeline instance.</param>
/// <param name="Stereo">Whether the pipeline must render a layered stereo view.</param>
/// <param name="OutputId">Stable output owner identity. Zero means no output reservation is available.</param>
/// <param name="OffscreenIntent">Optional immutable intent that explicitly selects an advanced offscreen pipeline.</param>
public readonly record struct RenderPipelineRequest(
    ERenderPipelinePurpose Purpose,
    bool Stereo,
    ulong OutputId = 0,
    RenderPipelineOffscreenIntent? OffscreenIntent = null)
{
    public static RenderPipelineRequest DesktopScene(bool stereo = false, ulong outputId = 0)
        => new(ERenderPipelinePurpose.DesktopScene, stereo, outputId);

    public static RenderPipelineRequest OpenXrEye(bool stereo, ulong outputId = 0)
        => new(ERenderPipelinePurpose.OpenXrEye, stereo, outputId);

    public static RenderPipelineRequest OffscreenCapture(bool stereo = false, ulong outputId = 0)
        => new(ERenderPipelinePurpose.OffscreenCapture, stereo, outputId);

    /// <summary>
    /// Creates an explicitly advanced offscreen request. Unlike the legacy capture request,
    /// this cannot silently select the default pipeline.
    /// </summary>
    public static RenderPipelineRequest AdvancedOffscreenCapture(
        RenderPipelineOffscreenIntent intent,
        bool stereo = false,
        ulong outputId = 0)
        => new(ERenderPipelinePurpose.OffscreenCapture, stereo, outputId, intent);
}
