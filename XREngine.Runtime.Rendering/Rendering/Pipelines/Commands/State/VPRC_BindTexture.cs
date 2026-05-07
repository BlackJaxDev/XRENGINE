namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_BindTexture : ViewportStateRenderCommand<VPRC_PopTextureBinding>
    {
        public string TextureName { get; set; } = string.Empty;
        public string? SamplerName { get; set; }
        public int TextureUnit { get; set; }

        protected override void Execute()
        {
            ActivePipelineInstance.RenderState.PushTextureBinding(new XRRenderPipelineInstance.RenderingState.ScopedTextureBinding
            {
                TextureName = TextureName,
                SamplerName = string.IsNullOrWhiteSpace(SamplerName) ? TextureName : SamplerName,
                TextureUnit = TextureUnit
            });
        }
    }
}
