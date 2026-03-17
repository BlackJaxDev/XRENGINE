using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Pushes complete blend state: enables blending with the specified equation and factors
    /// for RGB and alpha channels independently. Automatically pops via
    /// <see cref="VPRC_PopBlendState"/> when used with the <c>using</c> pattern.
    /// </summary>
    public class VPRC_PushBlendState : ViewportStateRenderCommand<VPRC_PopBlendState>
    {
        /// <summary>Blend equation for the RGB channels.</summary>
        public EBlendEquationMode RgbEquation { get; set; } = EBlendEquationMode.FuncAdd;

        /// <summary>Blend equation for the alpha channel.</summary>
        public EBlendEquationMode AlphaEquation { get; set; } = EBlendEquationMode.FuncAdd;

        /// <summary>Source factor for the RGB channels.</summary>
        public EBlendingFactor SrcRGB { get; set; } = EBlendingFactor.SrcAlpha;

        /// <summary>Destination factor for the RGB channels.</summary>
        public EBlendingFactor DstRGB { get; set; } = EBlendingFactor.OneMinusSrcAlpha;

        /// <summary>Source factor for the alpha channel.</summary>
        public EBlendingFactor SrcAlpha { get; set; } = EBlendingFactor.One;

        /// <summary>Destination factor for the alpha channel.</summary>
        public EBlendingFactor DstAlpha { get; set; } = EBlendingFactor.OneMinusSrcAlpha;

        protected override void Execute()
        {
            Engine.Rendering.State.EnableBlend(true);
            Engine.Rendering.State.BlendEquationSeparate(RgbEquation, AlphaEquation);
            Engine.Rendering.State.BlendFuncSeparate(SrcRGB, DstRGB, SrcAlpha, DstAlpha);
        }
    }
}
