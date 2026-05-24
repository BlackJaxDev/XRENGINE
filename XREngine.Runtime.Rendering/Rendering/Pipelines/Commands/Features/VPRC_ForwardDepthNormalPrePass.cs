using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering.Pipelines.Commands;

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

        public void SetOptions(IReadOnlyList<int> renderPasses, bool gpuDispatch)
        {
            _renderPasses = renderPasses;
            _gpuDispatch = gpuDispatch;
        }

        protected override void Execute()
        {
            if (_renderPasses.Count == 0)
                return;

            XRMaterial? material = ParentPipeline switch
            {
                DefaultRenderPipeline p => p.GetDepthNormalPrePassMaterial(),
                DefaultRenderPipeline2 p2 => p2.GetDepthNormalPrePassMaterial(),
                _ => null,
            };
            if (material is null)
                return;

            var rs = ActivePipelineInstance.RenderState;

            using var overrideTicket = rs.PushOverrideMaterial(material);
            using var variantTicket = rs.PushUseDepthNormalMaterialVariants();
            using var pipelineTicket = rs.PushForceShaderPipelines();
            using var generatedVertexTicket = rs.PushForceGeneratedVertexProgram();

            var commands = ActivePipelineInstance.MeshRenderCommands;
            if (commands is null)
                return;

            var camera = rs.SceneCamera;
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
            // RenderOptions.ExcludeFromGpuIndirect = true so RenderCPUNonMeshAndExcluded picks
            // them up; otherwise they will fault the GPU side of this dispatch.
            EMeshSubmissionStrategy strategy = _gpuDispatch
                ? RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy(true)
                : EMeshSubmissionStrategy.CpuDirect;
            EMeshSubmissionStrategy prepassStrategy = ResolveDepthNormalSubmissionStrategy(strategy);
            bool useGpuRenderPath = prepassStrategy != EMeshSubmissionStrategy.CpuDirect;
            foreach (int pass in _renderPasses)
            {
                if (useGpuRenderPath)
                {
                    // Mirror VPRC_RenderMeshesPassTraditional.RenderGPU exactly: CPU first for
                    // commands the GPU path cannot own, then GPU dispatch for mesh commands.
                    // The override/variant material tickets above keep those CPU fallback draws
                    // in the depth-normal material path as well.
                    commands.RenderCPUNonMeshAndExcluded(pass);
                    commands.RenderGPU(pass, prepassStrategy);
                }
                else
                {
                    commands.RenderCPU(pass, false, camera);
                }
            }
        }

        private static EMeshSubmissionStrategy ResolveDepthNormalSubmissionStrategy(EMeshSubmissionStrategy strategy)
        {
            if (!strategy.IsAnyMeshletStrategy())
                return strategy;

            return strategy == EMeshSubmissionStrategy.GpuMeshletInstrumented
                ? EMeshSubmissionStrategy.GpuIndirectInstrumented
                : EMeshSubmissionStrategy.GpuIndirectZeroReadback;
        }
    }
}
