using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Renders forward opaque and masked geometry into the shared depth+normal targets.
    /// Uses per-material fragment variants when available so the pre-pass preserves each shader's
    /// own normal evaluation path, with a generic override material left as fallback.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_ForwardDepthNormalPrePass : ViewportRenderCommand
    {
        private IReadOnlyList<int> _renderPasses = [];
        private bool _gpuDispatch;
        private int _resolvedRenderGraphPassIndex = int.MinValue;

        public void SetOptions(IReadOnlyList<int> renderPasses, bool gpuDispatch)
        {
            _renderPasses = renderPasses;
            _gpuDispatch = gpuDispatch;
        }

        protected override void Execute()
        {
            if (_renderPasses.Count == 0)
                return;

            XRMaterial? material =
                (ParentPipeline as IRenderPipelinePassMaterialProvider)?.
                    GetDepthNormalPrePassMaterial();
            if (material is null)
                return;

            var rs = ActivePipelineInstance.RenderState;

            using var overrideTicket = rs.PushOverrideMaterial(material);
            using var variantTicket = rs.PushUseDepthNormalMaterialVariants();
            using var pipelineTicket = rs.PushForceShaderPipelines();
            using var generatedVertexTicket = rs.PushForceGeneratedVertexProgram();

            var commands = ActivePipelineInstance.ActiveMeshRenderCommands;
            if (commands is null)
                return;

            using var renderGraphPassScope = _resolvedRenderGraphPassIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(_resolvedRenderGraphPassIndex)
                : null;

            // Resolve the active mesh submission strategy once per execution so the prepass
            // uses the same culling/draw path the lit pass will use later this frame. The
            // legacy _gpuDispatch flag is only the user's requested dispatch preference; a
            // forced CpuDirect strategy or backend/profile downgrade must keep this prepass on
            // CPU too, otherwise AO/depth can be produced by GPU draws while color is CPU.
            //
            // CRITICAL: When the main mesh pass runs on GPU indirect, the prepass MUST also run
            // on GPU indirect. The GPU indirect path generates its own vertex shader that fetches
            // per-draw world matrices from the culled-commands buffer (gl_BaseInstance-indexed)
            // while the CPU path uses per-object uniform matrices. Floating-point MVP composition
            // differences between the two paths cause depth-test mismatch (regular striping /
            // missing coverage) on the lit pass. ResolveEffectiveGpuMaterial honors the pushed
            // override material AND per-material DepthNormalPrePassVariant when
            // UseDepthNormalMaterialVariants is set; the same generated vertex shader is reused
            // (cached per variant material hash) so depth values match exactly.
            //
            // Materials that cannot live in the GPU indirect path (transient editor gizmos,
            // dynamically-created materials without bindless registration) MUST set
            // RenderOptions.ExcludeFromGpuIndirect = true so the filtered CPU fallback picks
            // them up; otherwise they will fault the GPU side of this dispatch.
            EMeshSubmissionStrategy strategy =
                RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy(_gpuDispatch);
            EMeshSubmissionStrategy prepassStrategy = ResolveDepthNormalSubmissionStrategy(strategy);
            bool useGpuRenderPath = prepassStrategy != EMeshSubmissionStrategy.CpuDirect;
            foreach (int pass in _renderPasses)
            {
                if (useGpuRenderPath)
                {
                    // Auxiliary geometry passes must not execute callback commands: debug callbacks
                    // populate the late overlay and otherwise run once per auxiliary replay.
                    if (!prepassStrategy.IsGpuZeroReadbackStrategy())
                    {
                        commands.RenderCPUFiltered(
                            pass,
                            static command => command is IRenderCommandMesh mesh && IsGpuPathCpuFallbackMesh(mesh));
                    }
                    commands.RenderGPU(pass, prepassStrategy, _resolvedRenderGraphPassIndex);
                }
                else
                {
                    commands.RenderCPUMeshOnly(pass);
                }
            }

            ActivePipelineInstance.MarkForwardContactPrePassAvailable();
        }

        private static bool IsGpuPathCpuFallbackMesh(IRenderCommandMesh meshCommand)
        {
            var material = meshCommand.MaterialOverride ?? meshCommand.Mesh?.Material;
            return meshCommand.ForceCpuRendering || material?.RenderOptions?.ExcludeFromGpuIndirect == true;
        }

        private static EMeshSubmissionStrategy ResolveDepthNormalSubmissionStrategy(EMeshSubmissionStrategy strategy)
        {
            if (!strategy.IsAnyMeshletStrategy())
                return strategy;

            return strategy == EMeshSubmissionStrategy.GpuMeshletInstrumented
                ? EMeshSubmissionStrategy.GpuIndirectInstrumented
                : EMeshSubmissionStrategy.GpuIndirectZeroReadback;
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (_renderPasses.Count == 0 || context.CurrentRenderTarget is not { } target)
                return;

            var builder = context.GetOrCreateSyntheticPass(
                $"ForwardDepthNormalPrePass_{target.Name}",
                ERenderGraphPassStage.Graphics);
            _resolvedRenderGraphPassIndex = builder.PassIndex;
            builder
                .UseEngineDescriptors()
                .UseMaterialDescriptors()
                .UseColorAttachment(
                    MakeFboColorResource(target.Name),
                    target.ColorAccess,
                    target.ConsumeColorLoadOp(),
                    target.GetColorStoreOp());
            UseRenderTargetDepthStencilAttachments(
                builder,
                target,
                target.ConsumeDepthLoadOp(),
                target.ConsumeStencilLoadOp());
        }
    }
}
