using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanCommandRuntime
    {
        private const string NoReadinessContextReason =
            "the view drew no non-shadow pass that uses the pass of this material in this frame, so no pipeline variant is known";
        private const string MeshTaskPipelinePendingReason =
            "the mesh-task pipeline of the view is not admitted yet";

        private readonly List<VulkanDrawReadinessRequest> _drawReadinessScratch = [];
        private readonly HashSet<ulong> _drawReadinessContextScratch = [];

        /// <summary>
        /// Prewarms and measures the graphics pipelines of each active draw readiness request
        /// (<see cref="VulkanDrawReadinessRegistry"/>) in the pass contexts of this frame plan, before command recording.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The required contexts of a request are the non-shadow pipeline requirements of the manifest whose operation
        /// belongs to the view of the request (its render pipeline instance), whose stereo flag matches, and whose pass is
        /// the render pass of the material. For each one:
        /// </para>
        /// <list type="bullet">
        /// <item><description>A CPU mesh draw is a template: the renderer of the request draws with the material of the
        /// request (or with the pass override material when the pass uses one), the draw state of the material over the draw
        /// state of the template, and the target and pass metadata of the template. The pipeline step of
        /// <see cref="VkMeshRenderer.TryPrewarmGraphicsPipelinesForRecording"/> compiles it (asynchronously) and records
        /// it, also when no view draws the renderer yet.</description></item>
        /// <item><description>A GPU-driven indirect draw of the same material is the real draw of that route, so its own
        /// renderer and draw are checked.</description></item>
        /// <item><description>A mesh-task dispatch uses the shared material-table program, so the context is ready when the
        /// normal admission admitted that requirement.</description></item>
        /// </list>
        /// <para>
        /// A frame plan that has no operation of the view publishes nothing, so another window or output does not change the
        /// result. With no active request, this costs one volatile read for each admission.
        /// </para>
        /// </remarks>
        /// <param name="recordingState">The recording state of the frame plan.</param>
        /// <param name="manifest">The pipeline variant manifest of the frame plan.</param>
        /// <param name="pendingRequirements">The requirement indices that the normal admission did not admit, or null.</param>
        /// <param name="admissionComplete">True when the normal admission checked every requirement.</param>
        private void PrewarmDrawReadinessRequests(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            VulkanPipelineVariantManifest manifest,
            HashSet<int>? pendingRequirements,
            bool admissionComplete)
        {
            VulkanDrawReadinessRegistry registry = ResourceRuntime.DrawReadiness;
            if (!registry.HasActiveRequests)
                return;

            registry.CopyActive(_drawReadinessScratch, Environment.TickCount64);
            for (int i = 0; i < _drawReadinessScratch.Count; i++)
            {
                VulkanDrawReadinessRequest request = _drawReadinessScratch[i];
                if (!ReferenceEquals(request.Renderer.BackendContext.Resources, ResourceRuntime))
                    continue;

                if (!TryEvaluateDrawReadinessRequest(
                        ref recordingState,
                        manifest,
                        pendingRequirements,
                        admissionComplete,
                        request,
                        out ERenderDrawReadiness state,
                        out string reason))
                    continue;

                registry.Publish(request, state, reason);
            }

            _drawReadinessScratch.Clear();
        }

        private bool TryEvaluateDrawReadinessRequest(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            VulkanPipelineVariantManifest manifest,
            HashSet<int>? pendingRequirements,
            bool admissionComplete,
            VulkanDrawReadinessRequest request,
            out ERenderDrawReadiness state,
            out string reason)
        {
            VulkanDrawReadinessTally tally = default;
            bool viewSeen = false;
            int materialPass = request.Material.RenderPass;
            _drawReadinessContextScratch.Clear();

            var requirements = manifest.Requirements;
            for (int r = 0; r < requirements.Count; r++)
            {
                VulkanPipelineVariantRequirement requirement = requirements[r];
                int opIndex = requirement.OpIndex;
                if ((uint)opIndex >= (uint)recordingState.Ops.Length || requirement.Stereo != request.Stereo)
                    continue;

                FrameOpContext context = recordingState.Ops.GetContext(opIndex);
                if (request.View is { } view && !ReferenceEquals(context.PipelineInstance, view))
                    continue;

                viewSeen = true;

                // A shadow pass draws for a light, not for the view, so it does not decide the readiness of the view.
                if (requirement.Shadow)
                    continue;

                ref readonly FrameOperationHeader header = ref recordingState.Ops.GetHeader(opIndex);
                if (header.PassIndex != materialPass)
                    continue;

                switch (header.OpCode)
                {
                    case EVulkanPrimaryPlanNodeKind.MeshDraw:
                        PrewarmTemplateDraw(
                            ref recordingState,
                            request,
                            opIndex,
                            header.PassIndex,
                            in context,
                            recordingState.Ops.GetMeshDraw(opIndex).Draw,
                            requirement.MaterialOverride,
                            ref tally);
                        break;
                    case EVulkanPrimaryPlanNodeKind.IndirectDraw:
                        CheckIndirectDraw(
                            ref recordingState,
                            request,
                            opIndex,
                            header.PassIndex,
                            in context,
                            recordingState.Ops.GetIndirectDraw(opIndex).Draw,
                            requirement.MaterialOverride,
                            ref tally);
                        break;
                    case EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount:
                        if (_drawReadinessContextScratch.Add(ComputeReadinessContextKey(header.PassIndex, null, 2)))
                        {
                            bool admitted = admissionComplete && (pendingRequirements is null || !pendingRequirements.Contains(r));
                            tally.Add(admitted, retryable: true, MeshTaskPipelinePendingReason);
                        }
                        break;
                }
            }

            _drawReadinessContextScratch.Clear();
            if (!viewSeen)
            {
                state = default;
                reason = string.Empty;
                return false;
            }

            state = tally.ToState(NoReadinessContextReason, out reason);
            return true;
        }

        private void PrewarmTemplateDraw(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            VulkanDrawReadinessRequest request,
            int opIndex,
            int passIndex,
            in FrameOpContext context,
            in PendingMeshDraw template,
            bool passOverridesMaterial,
            ref VulkanDrawReadinessTally tally)
        {
            XRFrameBuffer? target = recordingState.Ops.GetTarget(opIndex);
            if (!_drawReadinessContextScratch.Add(ComputeReadinessContextKey(passIndex, target, passOverridesMaterial ? 1 : 0)))
                return;

            // A pass with an override material (for example a depth prepass) draws every mesh with that material.
            XRMaterial material = passOverridesMaterial && template.MaterialOverride is { } passMaterial
                ? passMaterial
                : request.Material;
            VulkanFixedFunctionStateSnapshot templateState = new(
                template.DepthTestEnabled,
                template.DepthWriteEnabled,
                template.DepthCompareOp,
                template.StencilTestEnabled,
                template.FrontStencilState,
                template.BackStencilState,
                template.StencilWriteMask,
                template.ColorWriteMask,
                template.CullMode,
                template.FrontFace,
                template.BlendEnabled,
                template.AlphaToCoverageEnabled,
                template.ColorBlendOp,
                template.AlphaBlendOp,
                template.SrcColorBlendFactor,
                template.DstColorBlendFactor,
                template.SrcAlphaBlendFactor,
                template.DstAlphaBlendFactor);
            VulkanFixedFunctionStateSnapshot drawState = VulkanMeshDrawFixedState.Resolve(
                material.RenderOptions,
                templateState,
                template.RasterizationSamples);
            PendingMeshDraw draw = template with
            {
                Renderer = request.Renderer,
                DepthTestEnabled = drawState.DepthTestEnabled,
                DepthWriteEnabled = drawState.DepthWriteEnabled,
                DepthCompareOp = drawState.DepthCompareOp,
                StencilTestEnabled = drawState.StencilTestEnabled,
                FrontStencilState = drawState.FrontStencilState,
                BackStencilState = drawState.BackStencilState,
                StencilWriteMask = drawState.StencilWriteMask,
                ColorWriteMask = drawState.ColorWriteMask,
                CullMode = drawState.CullMode,
                FrontFace = drawState.FrontFace,
                BlendEnabled = drawState.BlendEnabled,
                AlphaToCoverageEnabled = drawState.AlphaToCoverageEnabled,
                ColorBlendOp = drawState.ColorBlendOp,
                AlphaBlendOp = drawState.AlphaBlendOp,
                SrcColorBlendFactor = drawState.SrcColorBlendFactor,
                DstColorBlendFactor = drawState.DstColorBlendFactor,
                SrcAlphaBlendFactor = drawState.SrcAlphaBlendFactor,
                DstAlphaBlendFactor = drawState.DstAlphaBlendFactor,
                MaterialOverride = material,
                Instances = 1u,
                PreparedProgram = null,
                PreparedProgramIdentity = null,
                PreparedProgramLinkGeneration = 0UL,
                ProgramBindingSnapshot = null,
            };

            PrewarmReadinessDraw(ref recordingState, request.Renderer, opIndex, passIndex, in context, target, in draw, ref tally);
        }

        private void CheckIndirectDraw(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            VulkanDrawReadinessRequest request,
            int opIndex,
            int passIndex,
            in FrameOpContext context,
            in PendingMeshDraw draw,
            bool passOverridesMaterial,
            ref VulkanDrawReadinessTally tally)
        {
            // On a GPU-driven route the batch of the material is the real draw. Another material's batch says nothing.
            if (!passOverridesMaterial && !ReferenceEquals(draw.MaterialOverride, request.Material))
                return;

            XRFrameBuffer? target = recordingState.Ops.GetTarget(opIndex);
            if (!_drawReadinessContextScratch.Add(ComputeReadinessContextKey(passIndex, target, 3)))
                return;

            PrewarmReadinessDraw(ref recordingState, draw.Renderer, opIndex, passIndex, in context, target, in draw, ref tally);
        }

        private void PrewarmReadinessDraw(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            VkMeshRenderer renderer,
            int opIndex,
            int passIndex,
            in FrameOpContext context,
            XRFrameBuffer? target,
            in PendingMeshDraw draw,
            ref VulkanDrawReadinessTally tally)
        {
            using VulkanPreparedResourcePlannerThreadScope resourceScope =
                EnterRecordingResourceScope(recordingState.FramePlan, context);
            using var pipelineScope =
                RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(context.PipelineInstance);
            VulkanCompiledRenderGraph operationGraph = recordingState.RenderGraphPlan.CompiledGraph;
            if (recordingState.FramePlan is not null &&
                recordingState.FramePlan.TryResolveRenderGraphPlan(in context, out VulkanRenderGraphPlan operationPlan))
            {
                operationGraph = operationPlan.CompiledGraph;
            }

            if (!TryResolveGraphicsPipelinePrewarmTarget(
                    target,
                    passIndex,
                    context,
                    recordingState.SwapchainTarget,
                    recordingState.Policy.UseDynamicRendering,
                    operationGraph,
                    out bool useDynamicRendering,
                    out RenderPass renderPass,
                    out DynamicRenderingFormatSignature dynamicRenderingFormats,
                    out _,
                    out bool depthStencilReadOnly,
                    out string targetReason))
            {
                tally.Add(ready: false, retryable: true, targetReason);
                return;
            }

            bool ready = renderer.TryPrewarmGraphicsPipelinesForRecording(
                draw,
                renderPass,
                useDynamicRendering,
                dynamicRenderingFormats,
                passIndex,
                context.PassMetadata,
                depthStencilReadOnly,
                context.PipelineInstance?.DebugName ?? "<no pipeline>",
                foregroundRequired: false,
                out bool retryable,
                out string reason);
            tally.Add(ready, retryable, reason);
        }

        private static ulong ComputeReadinessContextKey(int passIndex, XRFrameBuffer? target, int kind)
            => ((ulong)(uint)passIndex << 32) ^
               ((ulong)(uint)(target is null ? 0 : RuntimeHelpers.GetHashCode(target)) << 2) ^
               (ulong)(uint)kind;
    }
}
