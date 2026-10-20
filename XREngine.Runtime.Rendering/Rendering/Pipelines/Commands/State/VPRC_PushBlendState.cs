using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Pushes complete blend state: enables blending with the specified equation and factors
    /// for RGB and alpha channels independently. Automatically pops via
    /// <see cref="VPRC_PopBlendState"/> when used with the <c>using</c> pattern.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_PushBlendState : ViewportStateRenderCommand<VPRC_PopBlendState>
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("raster-state");

        /// <summary>Blend equation for the RGB channels.</summary>
        [System.ComponentModel.DefaultValue(EBlendEquationMode.FuncAdd)]
        public EBlendEquationMode RgbEquation { get; set; } = EBlendEquationMode.FuncAdd;

        /// <summary>Blend equation for the alpha channel.</summary>
        [System.ComponentModel.DefaultValue(EBlendEquationMode.FuncAdd)]
        public EBlendEquationMode AlphaEquation { get; set; } = EBlendEquationMode.FuncAdd;

        /// <summary>Source factor for the RGB channels.</summary>
        [System.ComponentModel.DefaultValue(EBlendingFactor.SrcAlpha)]
        public EBlendingFactor SrcRGB { get; set; } = EBlendingFactor.SrcAlpha;

        /// <summary>Destination factor for the RGB channels.</summary>
        [System.ComponentModel.DefaultValue(EBlendingFactor.OneMinusSrcAlpha)]
        public EBlendingFactor DstRGB { get; set; } = EBlendingFactor.OneMinusSrcAlpha;

        /// <summary>Source factor for the alpha channel.</summary>
        [System.ComponentModel.DefaultValue(EBlendingFactor.One)]
        public EBlendingFactor SrcAlpha { get; set; } = EBlendingFactor.One;

        /// <summary>Destination factor for the alpha channel.</summary>
        [System.ComponentModel.DefaultValue(EBlendingFactor.OneMinusSrcAlpha)]
        public EBlendingFactor DstAlpha { get; set; } = EBlendingFactor.OneMinusSrcAlpha;

        protected override void Execute()
        {
            RuntimeEngine.Rendering.State.EnableBlend(true);
            RuntimeEngine.Rendering.State.BlendEquationSeparate(RgbEquation, AlphaEquation);
            RuntimeEngine.Rendering.State.BlendFuncSeparate(SrcRGB, DstRGB, SrcAlpha, DstAlpha);
        }
    }
}
