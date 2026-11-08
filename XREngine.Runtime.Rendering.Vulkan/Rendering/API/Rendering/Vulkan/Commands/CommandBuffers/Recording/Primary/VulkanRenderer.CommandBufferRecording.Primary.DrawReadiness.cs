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
        private const string PassStateUnknownReason =
            "no draw of the view stored the pass state of this pass context yet";
        private const string RendererCannotServeReason =
            "the renderer wrapper is not active, not linked, retired, or deleted";

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
        /// <item><description>A CPU mesh draw is a template for its pass and target. The renderer of the request draws with
        /// the material of the request (or with the pass override material when the pass uses one, for example the
        /// depth-normal prepass, which draws the material passes with its own material), and its draw state comes from the
        /// pass state of the template and the render options of the request
        /// (<see cref="VulkanDrawReadinessTemplate"/>), never from the material of the template.
        /// <see cref="VkMeshRenderer.TryPrewarmGraphicsPipelinesForRecording"/> compiles it (asynchronously), also when no
        /// view draws the renderer yet.</description></item>
        /// <item><description>A GPU-driven indirect draw of the same material is the real draw of that route, so its own
        /// renderer and draw are checked.</description></item>
        /// <item><description>A mesh-task dispatch uses the shared material-table program, so the context is ready when the
        /// normal admission admitted that requirement.</description></item>
        /// </list>
        /// <para>
        /// The result is published with the program of the request renderer that it measured. A frame plan that has no
        /// operation of the view publishes nothing, so another window or output does not change the result. A request whose
        /// renderer cannot serve (not active, unlinked, retired, or deleted) is dropped and does no backend work. The scratch
        /// collections are the thread-local recording scratch, so parallel admissions do not share them. With no active
        /// request, this costs one volatile read for each admission.
        /// </para>
        /// </remarks>
        private void PrewarmDrawReadinessRequests(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            VulkanPipelineVariantManifest manifest,
            HashSet<int>? pendingRequirements,
            bool admissionComplete)
        {
            VulkanDrawReadinessRegistry registry = ResourceRuntime.DrawReadiness;
            if (!registry.HasActiveRequests)
                return;

            List<VulkanDrawReadinessRequest> requests = recordingState.RecordingScratch.DrawReadinessRequests;
            registry.CopyActive(requests, Environment.TickCount64);
            long retirementGeneration = ResourceRuntime.PipelineManager.SharedGraphicsPipelineRetirementGeneration;
            ulong targetSignature = VulkanPipelineVariantManifest.ComputeTargetCompatibilitySignature(
                recordingState.ResourcePlanStamp.ResourceAllocationSignature,
                recordingState.SwapchainTarget,
                recordingState.Policy.UseDynamicRendering,
                ResourceRuntime.SwapchainRenderPass);
            for (int i = 0; i < requests.Count; i++)
            {
                VulkanDrawReadinessRequest request = requests[i];
                VkMeshRenderer renderer = request.Renderer;
                if (!ReferenceEquals(renderer.BackendContext.Resources, ResourceRuntime))
                    continue;

                if (!renderer.CanServeDrawReadiness)
                {
                    registry.RemoveRenderer(renderer);
                    continue;
                }

                if (!TryEvaluateDrawReadinessRequest(
                        ref recordingState,
                        manifest,
                        pendingRequirements,
                        admissionComplete,
                        retirementGeneration,
                        targetSignature,
                        request,
                        out ERenderDrawReadiness state,
                        out string reason,
                        out VkRenderProgram? measuredProgram))
                    continue;

                registry.Publish(
                    request,
                    state,
                    reason,
                    measuredProgram,
                    measuredProgram?.LinkGeneration ?? 0UL,
                    retirementGeneration,
                    targetSignature);
            }

            requests.Clear();
        }

        private bool TryEvaluateDrawReadinessRequest(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            VulkanPipelineVariantManifest manifest,
            HashSet<int>? pendingRequirements,
            bool admissionComplete,
            long retirementGeneration,
            ulong targetSignature,
            VulkanDrawReadinessRequest request,
            out ERenderDrawReadiness state,
            out string reason,
            out VkRenderProgram? measuredProgram)
        {
            VulkanDrawReadinessTally tally = default;
            bool viewSeen = false;
            bool programMeasured = false;
            measuredProgram = null;
            int materialPass = request.Material.RenderPass;
            HashSet<ulong> contexts = recordingState.RecordingScratch.DrawReadinessContexts;
            contexts.Clear();

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

                // The pass of the operation is the pass of the render command. An override-material pass (the depth-normal
                // prepass) draws the material passes again with its own material, so it has the pass of the material too.
                ref readonly FrameOperationHeader header = ref recordingState.Ops.GetHeader(opIndex);
                if (header.PassIndex != materialPass)
                    continue;

                switch (header.OpCode)
                {
                    case EVulkanPrimaryPlanNodeKind.MeshDraw:
                        if (PrewarmTemplateDraw(
                                ref recordingState,
                                contexts,
                                request,
                                opIndex,
                                header.PassIndex,
                                in context,
                                recordingState.Ops.GetMeshDraw(opIndex).Draw,
                                requirement.MaterialOverride,
                                ref tally,
                                out VkRenderProgram? stepProgram) &&
                            !requirement.MaterialOverride &&
                            !programMeasured)
                        {
                            // The program that the pipeline step used with the material of the request, read under the
                            // recording lock of the renderer.
                            measuredProgram = stepProgram;
                            programMeasured = true;
                        }
                        break;
                    case EVulkanPrimaryPlanNodeKind.IndirectDraw:
                        CheckIndirectDraw(
                            ref recordingState,
                            contexts,
                            request,
                            opIndex,
                            header.PassIndex,
                            in context,
                            recordingState.Ops.GetIndirectDraw(opIndex).Draw,
                            requirement.MaterialOverride,
                            ref tally);
                        break;
                    case EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount:
                        // Before the normal admission ran (the PresentNow readiness gate), the admission state of the
                        // mesh-task requirement is not known, so it does not count.
                        if (admissionComplete && contexts.Add(ComputeReadinessContextKey(header.PassIndex, null, 2)))
                        {
                            bool admitted = admissionComplete && (pendingRequirements is null || !pendingRequirements.Contains(r));
                            tally.Add(admitted, retryable: true, MeshTaskPipelinePendingReason);
                        }
                        break;
                }
            }

            contexts.Clear();
            if (!viewSeen)
            {
                state = default;
                reason = string.Empty;
                return false;
            }

            // A frame plan of the view with no context for the material (for example a sub-plan with no scene draw) says
            // nothing about the pipelines: it only names the reason while no frame measured the request.
            if (tally.Required == 0)
            {
                ResourceRuntime.DrawReadiness.NoteNoContext(request, NoReadinessContextReason, retirementGeneration, targetSignature);
                state = default;
                reason = string.Empty;
                return false;
            }

            // On a GPU-driven route no template step ran for the request renderer; its active program is the one that the
            // query prepared with the material of the request.
            if (!programMeasured)
                measuredProgram = request.Renderer.ActiveProgramForReadiness;

            state = tally.ToState(NoReadinessContextReason, out reason);
            return true;
        }

        /// <summary>Prewarms one template context. Returns true when the context was new and the pipeline step ran.</summary>
        private bool PrewarmTemplateDraw(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            HashSet<ulong> contexts,
            VulkanDrawReadinessRequest request,
            int opIndex,
            int passIndex,
            in FrameOpContext context,
            in PendingMeshDraw template,
            bool passOverridesMaterial,
            ref VulkanDrawReadinessTally tally,
            out VkRenderProgram? activeProgram)
        {
            activeProgram = null;
            XRFrameBuffer? target = recordingState.Ops.GetTarget(opIndex);
            if (!contexts.Add(ComputeReadinessContextKey(passIndex, target, passOverridesMaterial ? 1 : 0)))
                return false;

            // The draw enqueue stores the pass state of each pass context while a request is active.
            if (!ResourceRuntime.DrawReadiness.TryGetPassState(context.PipelineInstance, passIndex, target, out VulkanFixedFunctionStateSnapshot passState))
            {
                tally.Add(ready: false, retryable: true, PassStateUnknownReason);
                return false;
            }

            XRMaterial material = passOverridesMaterial && template.MaterialOverride is { } passMaterial
                ? passMaterial
                : request.Material;
            PendingMeshDraw draw = VulkanDrawReadinessTemplate.Build(
                template,
                passState,
                request.Renderer,
                material,
                request.RenderOptionsOverride);
            return PrewarmReadinessDraw(ref recordingState, request.Renderer, opIndex, passIndex, in context, target, in draw, ref tally, out activeProgram);
        }

        private void CheckIndirectDraw(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            HashSet<ulong> contexts,
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
            if (!contexts.Add(ComputeReadinessContextKey(passIndex, target, 3)))
                return;

            _ = PrewarmReadinessDraw(ref recordingState, draw.Renderer, opIndex, passIndex, in context, target, in draw, ref tally, out _);
        }

        /// <summary>Runs the pipeline step of one readiness draw. Returns false when the renderer cannot serve or the target is not ready.</summary>
        private bool PrewarmReadinessDraw(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            VkMeshRenderer renderer,
            int opIndex,
            int passIndex,
            in FrameOpContext context,
            XRFrameBuffer? target,
            in PendingMeshDraw draw,
            ref VulkanDrawReadinessTally tally,
            out VkRenderProgram? activeProgram)
        {
            activeProgram = null;

            // A wrapper that cannot serve must not make programs or pipelines again.
            if (renderer is null || !renderer.CanServeDrawReadiness)
            {
                tally.Add(ready: false, retryable: true, RendererCannotServeReason);
                return false;
            }

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
                return false;
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
                out string reason,
                out activeProgram);
            tally.Add(ready, retryable, reason);
            return true;
        }

        private static ulong ComputeReadinessContextKey(int passIndex, XRFrameBuffer? target, int kind)
            => ((ulong)(uint)passIndex << 32) ^
               ((ulong)(uint)(target is null ? 0 : RuntimeHelpers.GetHashCode(target)) << 2) ^
               (ulong)(uint)kind;
    }
}
