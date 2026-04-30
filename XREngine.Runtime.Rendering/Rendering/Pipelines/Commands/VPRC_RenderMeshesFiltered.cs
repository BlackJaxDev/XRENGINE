using XREngine.Rendering.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Renders only mesh commands in a specific render pass that satisfy a user-supplied predicate.
    /// Use this to build custom passes that render a subset of the scene — e.g., only objects
    /// on a specific layer, with a specific tag, or matching a material type.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_RenderMeshesFiltered : ViewportRenderCommand
    {
        /// <summary>
        /// The render pass index whose commands should be filtered and rendered.
        /// </summary>
        public int RenderPass { get; set; }

        /// <summary>
        /// Predicate applied to each <see cref="RenderCommand"/> in the pass.
        /// Only commands for which this returns <c>true</c> are rendered.
        /// When <c>null</c>, all commands in the pass are rendered (same as unfiltered).
        /// </summary>
        public Predicate<RenderCommand>? Filter { get; set; }

        public override bool NeedsCollecVisible => true;

        protected override void Execute()
        {
            using var passScope = Engine.Rendering.State.PushRenderGraphPassIndex(RenderPass);
            var instance = ActivePipelineInstance;

            if (Filter is null)
                instance.MeshRenderCommands.RenderCPU(RenderPass);
            else
                instance.MeshRenderCommands.RenderCPUFiltered(RenderPass, Filter);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            string passName = $"RenderMeshesFiltered_{RenderPass}";
            var builder = context.Metadata.ForPass(RenderPass, passName, ERenderGraphPassStage.Graphics);
            builder
                .UseEngineDescriptors()
                .UseMaterialDescriptors();

            if (context.CurrentRenderTarget is { } target)
            {
                builder.WithName($"{passName}_{target.Name}");

                builder.UseColorAttachment(
                    MakeFboColorResource(target.Name),
                    target.ColorAccess,
                    target.ConsumeColorLoadOp(),
                    target.GetColorStoreOp());

                builder.UseDepthAttachment(
                    MakeFboDepthResource(target.Name),
                    target.DepthAccess,
                    target.ConsumeDepthLoadOp(),
                    target.GetDepthStoreOp());
            }
        }
    }
}
