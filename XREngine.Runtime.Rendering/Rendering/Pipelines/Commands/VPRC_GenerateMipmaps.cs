namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    [RenderCommandInfo(ERenderCommandCategory.Transfer, DoesGpuWork = true)]
    public class VPRC_GenerateMipmaps : ViewportRenderCommand
    {
        [RenderResourceName(ERenderResourceNameKind.Texture)]
        public string? TextureName { get; set; }

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(TextureName)
                ? base.GpuProfilingName
                : $"{base.GpuProfilingName}[{TextureName}]";

        protected override void Execute()
        {
            if (TextureName is null)
                return;

            ActivePipelineInstance.GetTexture<XRTexture>(TextureName)?.GenerateMipmapsGPU();
        }
    }
}
