namespace XREngine.Rendering;

/// <summary>
/// Implemented by pipelines that have more than one view setup, such as a both-eye setup and a separate-eye setup.
/// Tools read the setup from this interface instead of checking pipeline types.
/// </summary>
public interface IRenderPipelineViewSetupSource
{
    /// <summary>
    /// Returns the view setup that <paramref name="instance"/> uses now. When <paramref name="instance"/> is null,
    /// returns the setup that the pipeline asset requests.
    /// </summary>
    RenderPipelineViewSetup GetViewSetup(XRRenderPipelineInstance? instance);
}
