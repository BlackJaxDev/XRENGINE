using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Pushes complete stencil state: enables stencil testing with the specified function,
    /// reference value, mask, and stencil operations. Automatically pops via
    /// <see cref="VPRC_PopStencilState"/> when used with the <c>using</c> pattern.
    /// </summary>
    public class VPRC_PushStencilState : ViewportStateRenderCommand<VPRC_PopStencilState>
    {
        /// <summary>Stencil comparison function.</summary>
        public EComparison Function { get; set; } = EComparison.Always;

        /// <summary>Reference value for the stencil test.</summary>
        public int Reference { get; set; }

        /// <summary>Mask ANDed with both the reference and stored stencil value before comparison.</summary>
        public uint ReadMask { get; set; } = 0xFF;

        /// <summary>Bit mask controlling which stencil bits can be written.</summary>
        public uint WriteMask { get; set; } = 0xFF;

        /// <summary>Action when both stencil and depth tests fail.</summary>
        public EStencilOp StencilFail { get; set; } = EStencilOp.Keep;

        /// <summary>Action when stencil passes but depth fails.</summary>
        public EStencilOp DepthFail { get; set; } = EStencilOp.Keep;

        /// <summary>Action when both tests pass.</summary>
        public EStencilOp BothPass { get; set; } = EStencilOp.Keep;

        protected override void Execute()
        {
            Engine.Rendering.State.EnableStencilTest(true);
            Engine.Rendering.State.StencilMask(WriteMask);
            Engine.Rendering.State.StencilFunc(Function, Reference, ReadMask);
            Engine.Rendering.State.StencilOp(StencilFail, DepthFail, BothPass);
        }
    }
}
