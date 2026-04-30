namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_GenerateMipmaps : ViewportRenderCommand
    {
        public string? TextureName { get; set; }

        protected override void Execute()
        {
            if (TextureName is null)
                return;

            ActivePipelineInstance.GetTexture<XRTexture>(TextureName)?.GenerateMipmapsGPU();
        }
    }
}
