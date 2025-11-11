namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Renders an FBO quad to another FBO.
    /// Useful for transforming every pixel of previous FBO.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="destination"></param>
    public class VPRC_RenderQuadToFBO : ViewportRenderCommand
    {
        public string? SourceQuadFBOName { get; set; }
        public string? DestinationFBOName { get; set; } = null;

        public void SetTargets(string sourceQuadFBOName, string? destinationFBOName = null)
        {
            SourceQuadFBOName = sourceQuadFBOName;
            DestinationFBOName = destinationFBOName;
        }

        protected override void Execute()
        {
            if (SourceQuadFBOName is null)
                return;

            XRQuadFrameBuffer? sourceFBO = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(SourceQuadFBOName);
            if (sourceFBO is null)
                return;

            sourceFBO.Render(DestinationFBOName is null ? null : ActivePipelineInstance.GetFBO<XRFrameBuffer>(DestinationFBOName));
        }
    }
}
