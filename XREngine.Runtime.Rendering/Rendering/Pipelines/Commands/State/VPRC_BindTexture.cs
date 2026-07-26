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
            ActivePipelineInstance.RenderState.PushTextureBindingState(
                new XRRenderPipelineInstance.RenderingState.ScopedTextureBinding(
                    TextureName,
                    string.IsNullOrWhiteSpace(SamplerName) ? TextureName : SamplerName,
                    TextureUnit));
        }
    }
}
