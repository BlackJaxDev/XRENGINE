namespace XREngine.Data.Rendering;

/// <summary>
/// Describes the output ownership and view topology required from a new render pipeline.
/// </summary>
/// <param name="Purpose">The output that will own the pipeline instance.</param>
/// <param name="Stereo">Whether the pipeline must render a layered stereo view.</param>
public readonly record struct RenderPipelineRequest(
    ERenderPipelinePurpose Purpose,
    bool Stereo)
{
    public static RenderPipelineRequest DesktopScene(bool stereo = false)
        => new(ERenderPipelinePurpose.DesktopScene, stereo);

    public static RenderPipelineRequest OpenXrEye(bool stereo)
        => new(ERenderPipelinePurpose.OpenXrEye, stereo);

    public static RenderPipelineRequest OffscreenCapture(bool stereo = false)
        => new(ERenderPipelinePurpose.OffscreenCapture, stereo);
}
