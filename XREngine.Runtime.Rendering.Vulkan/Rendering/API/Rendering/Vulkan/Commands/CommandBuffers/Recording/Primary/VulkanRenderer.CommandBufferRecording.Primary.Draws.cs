using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanCommandRuntime
    {

        internal int GetMeshDrawUniformSlot(scoped ref PrimaryCommandBufferRecordingState recordingState,
            int opIndex,
            VkMeshRenderer renderer,
            in FrameOpContext context,
            in PendingMeshDraw draw)
        {
            if ((uint)opIndex >= (uint)recordingState.Ops.Length)
                throw new ArgumentOutOfRangeException(nameof(opIndex));

            return GetOrAssignPrimaryMeshDrawUniformSlot(
                opIndex,
                recordingState.MeshDrawUniformSlotsByOpIndex,
                recordingState.MeshDrawSlotsByRendererFamily,
                recordingState.MeshFrameDataFamilyBases,
                recordingState.CommandBufferImageSlot,
                renderer,
                context,
                draw);
        }

        internal bool RecordMeshDrawPayloadIntoCommandBuffer(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            CommandBuffer commandBuffer,
            in MeshDrawPayload payload,
            XRFrameBuffer? target,
            in FrameOpContext context,
            int passIndex,
            int uniformSlot)
        {
            PendingMeshDraw draw = payload.Draw;
            if (draw.ViewportScissorCount > 1 && draw.IndexedViewports is { } viewports && draw.IndexedScissors is { } scissors && viewports.Length >= (int)draw.ViewportScissorCount && scissors.Length >= (int)draw.ViewportScissorCount)
                SetViewportScissorTracked(commandBuffer, viewports, scissors, draw.ViewportScissorCount);
            else
                SetViewportScissorTracked(commandBuffer, draw.Viewport, draw.Scissor);
            return draw.Renderer.RecordDraw(commandBuffer, draw, recordingState.RenderScope.RenderPass, recordingState.RenderScope.UsesDynamicRendering, recordingState.RenderScope.DynamicRenderingFormats, passIndex, context.PassMetadata, target, context, recordingState.RenderScope.DepthStencilReadOnly, context.PipelineInstance?.DebugName ?? "<no pipeline>", target?.Name ?? "<swapchain>", uniformSlot, recordingState.CommandBufferImageSlot);
        }

        internal void RecordIndirectDrawPayloadIntoCommandBuffer(
            scoped ref PrimaryCommandBufferRecordingState recordingState,
            CommandBuffer commandBuffer,
            in IndirectDrawPayload payload,
            XRFrameBuffer? target,
            in FrameOpContext context,
            int passIndex,
            int opIndex)
        {
            PendingMeshDraw draw = payload.Draw;
            if (draw.ViewportScissorCount > 1 && draw.IndexedViewports is { } viewports && draw.IndexedScissors is { } scissors && viewports.Length >= (int)draw.ViewportScissorCount && scissors.Length >= (int)draw.ViewportScissorCount)
                SetViewportScissorTracked(commandBuffer, viewports, scissors, draw.ViewportScissorCount);
            else
                SetViewportScissorTracked(commandBuffer, draw.Viewport, draw.Scissor);
            if (!payload.MeshRenderer.RecordIndirectDrawState(commandBuffer, draw, recordingState.RenderScope.RenderPass, recordingState.RenderScope.UsesDynamicRendering, recordingState.RenderScope.DynamicRenderingFormats, passIndex, context.PassMetadata, recordingState.RenderScope.DepthStencilReadOnly, context.PipelineInstance?.DebugName ?? "<no pipeline>", target?.Name ?? "<swapchain>", GetMeshDrawUniformSlot(ref recordingState, opIndex, payload.MeshRenderer, context, draw), out _)) return;
            RecordIndirectDrawPayload(commandBuffer, in payload, allowInlineBarrier: false);
        }

        internal bool RecordMeshDrawIntoCommandBuffer(scoped ref PrimaryCommandBufferRecordingState recordingState,
            CommandBuffer targetCommandBuffer,
            MeshDrawOp drawOp,
            int passIndex,
            int drawUniformSlot)
        {
            Viewport viewport = drawOp.Draw.Viewport;
            Rect2D scissor = drawOp.Draw.Scissor;
            uint viewportScissorCount = drawOp.Draw.ViewportScissorCount;
            if (viewportScissorCount > 1 &&
                drawOp.Draw.IndexedViewports is { } indexedViewports &&
                drawOp.Draw.IndexedScissors is { } indexedScissors &&
                indexedViewports.Length >= (int)viewportScissorCount &&
                indexedScissors.Length >= (int)viewportScissorCount)
                SetViewportScissorTracked(targetCommandBuffer, indexedViewports, indexedScissors, viewportScissorCount);
            else
                SetViewportScissorTracked(targetCommandBuffer, viewport, scissor);

            if (CommandRecordingDiagnosticsEnabled && drawOp.Target?.Name == "ForwardPassFBO")
            {
                Debug.VulkanEvery(
                    "Vulkan.FwdDraw." + passIndex,
                    TimeSpan.FromSeconds(2),
                    "[Vulkan][FwdDraw] pipe='{0}' pass={1} rp=0x{2:X} vp=(x={3},y={4},w={5},h={6})",
                    drawOp.Context.PipelineInstance?.DebugName ?? "?",
                    passIndex, recordingState.RenderScope.RenderPass.Handle,
                    viewport.X, viewport.Y, viewport.Width, viewport.Height);
            }

            string? drawTargetName = drawOp.Target?.Name;
            if (DeferredLightingDiagnostics.Enabled &&
                (string.Equals(drawTargetName, DefaultRenderPipeline.DeferredGBufferFBOName, StringComparison.Ordinal) ||
                 string.Equals(drawTargetName, DefaultRenderPipeline.MsaaGBufferFBOName, StringComparison.Ordinal)))
            {
                var draw = drawOp.Draw;
                var material = draw.MaterialOverride ?? draw.Renderer.MeshRenderer.Material;
                string gBufferTargetName = drawTargetName ?? "<unknown>";
                Debug.VulkanEvery(
                    $"DeferredLighting.GBufferDraw.{gBufferTargetName}.{passIndex}.{draw.Renderer.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[DeferredLightingDiag][GBufferDraw] target='{0}' pass={1} dyn={2} rp=0x{3:X} colors={4} depthFmt={5} layers={6} viewMask=0x{7:X} dsReadOnly={8} mesh='{9}' material='{10}' program='{11}' stereo={12} colorMask={13} blend={14} depth={15}/{16}/{17} cull={18} front={19} vp=({20},{21},{22},{23}) scissor=({24},{25},{26},{27}) pipe={28} pipeName='{29}' camera='{30}' camPos=({31},{32},{33}) camFwd=({34},{35},{36}) vpM=({37},{38},{39},{40})",
                    gBufferTargetName,
                    passIndex,
                    recordingState.RenderScope.UsesDynamicRendering,
                    recordingState.RenderScope.RenderPass.Handle,
                    recordingState.RenderScope.UsesDynamicRendering ? recordingState.RenderScope.DynamicRenderingFormats.DescribeColorFormats() : "<render-pass>",
                    recordingState.RenderScope.UsesDynamicRendering ? recordingState.RenderScope.DynamicRenderingFormats.DepthAttachmentFormat : Format.Undefined,
                    recordingState.RenderScope.UsesDynamicRendering ? recordingState.RenderScope.DynamicRenderingFormats.LayerCount : 1u,
                    recordingState.RenderScope.UsesDynamicRendering ? recordingState.RenderScope.DynamicRenderingFormats.ViewMask : 0u,
                    recordingState.RenderScope.DepthStencilReadOnly,
                    draw.Renderer.Mesh?.Name ?? draw.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                    material?.Name ?? "<unnamed material>",
                    draw.PreparedProgram?.Data?.Name ?? "<uncaptured program>",
                    draw.IsStereoPass,
                    draw.ColorWriteMask,
                    draw.BlendEnabled,
                    draw.DepthTestEnabled,
                    draw.DepthWriteEnabled,
                    draw.DepthCompareOp,
                    draw.CullMode,
                    draw.FrontFace,
                    viewport.X,
                    viewport.Y,
                    viewport.Width,
                    viewport.Height,
                    scissor.Offset.X,
                    scissor.Offset.Y,
                    scissor.Extent.Width,
                    scissor.Extent.Height,
                    drawOp.Context.PipelineIdentity,
                    drawOp.Context.PipelineInstance?.DebugName ?? "<no pipeline>",
                    draw.Camera?.GetType().Name ?? "<no camera>",
                    draw.CameraPosition.X,
                    draw.CameraPosition.Y,
                    draw.CameraPosition.Z,
                    draw.CameraForward.X,
                    draw.CameraForward.Y,
                    draw.CameraForward.Z,
                    draw.ViewProjectionMatrix.M11,
                    draw.ViewProjectionMatrix.M22,
                    draw.ViewProjectionMatrix.M33,
                    draw.ViewProjectionMatrix.M44);
            }

            bool recordedDraw = drawOp.Draw.Renderer.RecordDraw(
                targetCommandBuffer,
                drawOp.Draw,
                recordingState.RenderScope.RenderPass,
                recordingState.RenderScope.UsesDynamicRendering,
                recordingState.RenderScope.DynamicRenderingFormats,
                passIndex,
                drawOp.Context.PassMetadata,
                drawOp.Target,
                drawOp.Context,
                recordingState.RenderScope.DepthStencilReadOnly,
                drawOp.Context.PipelineInstance?.DebugName ?? "<no pipeline>",
                drawOp.Target?.Name ?? "<swapchain>",
                drawUniformSlot,
                recordingState.CommandBufferImageSlot);

            if (DeferredLightingDiagnostics.Enabled &&
                (string.Equals(drawTargetName, DefaultRenderPipeline.DeferredGBufferFBOName, StringComparison.Ordinal) ||
                 string.Equals(drawTargetName, DefaultRenderPipeline.MsaaGBufferFBOName, StringComparison.Ordinal)))
            {
                Debug.VulkanEvery(
                    $"DeferredLighting.GBufferDraw.Result.{drawTargetName}.{passIndex}.{drawOp.Draw.Renderer.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[DeferredLightingDiag][GBufferDraw.Result] target='{0}' pass={1} recorded={2} slot={3} blocker='{4}'",
                    drawTargetName ?? "<unknown>",
                    passIndex,
                    recordedDraw,
                    drawUniformSlot,
                    recordedDraw
                        ? "<none>"
                        : drawOp.Draw.Renderer.DescribeReusableCommandBufferFrameDataBlocker(drawOp.Draw, drawUniformSlot));
            }

            return recordedDraw;
        }

        internal void RecordIndirectDrawIntoCommandBuffer(scoped ref PrimaryCommandBufferRecordingState recordingState,
            CommandBuffer targetCommandBuffer,
            IndirectDrawOp indirectOp,
            int passIndex,
            int opIndex)
        {
            Viewport viewport = indirectOp.Draw.Viewport;
            Rect2D scissor = indirectOp.Draw.Scissor;
            uint viewportScissorCount = indirectOp.Draw.ViewportScissorCount;
            if (viewportScissorCount > 1 &&
                indirectOp.Draw.IndexedViewports is { } indexedViewports &&
                indirectOp.Draw.IndexedScissors is { } indexedScissors &&
                indexedViewports.Length >= (int)viewportScissorCount &&
                indexedScissors.Length >= (int)viewportScissorCount)
                SetViewportScissorTracked(targetCommandBuffer, indexedViewports, indexedScissors, viewportScissorCount);
            else
                SetViewportScissorTracked(targetCommandBuffer, viewport, scissor);

            if (!indirectOp.MeshRenderer.RecordIndirectDrawState(
                    targetCommandBuffer,
                    indirectOp.Draw,
                    recordingState.RenderScope.RenderPass,
                    recordingState.RenderScope.UsesDynamicRendering,
                    recordingState.RenderScope.DynamicRenderingFormats,
                    passIndex,
                    indirectOp.Context.PassMetadata,
                    recordingState.RenderScope.DepthStencilReadOnly,
                    indirectOp.Context.PipelineInstance?.DebugName ?? "<no pipeline>",
                    indirectOp.Target?.Name ?? "<swapchain>",
                    GetMeshDrawUniformSlot(ref recordingState,
                        opIndex,
                        indirectOp.MeshRenderer,
                        indirectOp.Context,
                        indirectOp.Draw),
                    out _))
                return;

            RecordIndirectDrawOp(targetCommandBuffer, indirectOp, allowInlineBarrier: false);
        }

        private void RecordIndirectDrawIntoSecondaryCommandBuffer(
            CommandBuffer targetCommandBuffer,
            in IndirectDrawPayload payload,
            XRFrameBuffer? target,
            in FrameOpContext context,
            in VkMeshRenderer.IndirectDrawRecordingState recordingState,
            int passIndex,
            bool inheritedDynamicRendering,
            RenderPass inheritedRenderPass,
            DynamicRenderingFormatSignature inheritedDynamicRenderingFormats,
            bool inheritedDepthStencilReadOnly,
            int uniformSlot)
        {
            Viewport viewport = payload.Draw.Viewport;
            Rect2D scissor = payload.Draw.Scissor;
            uint viewportScissorCount = payload.Draw.ViewportScissorCount;
            if (viewportScissorCount > 1 &&
                payload.Draw.IndexedViewports is { } indexedViewports &&
                payload.Draw.IndexedScissors is { } indexedScissors &&
                indexedViewports.Length >= (int)viewportScissorCount &&
                indexedScissors.Length >= (int)viewportScissorCount)
                SetViewportScissorTracked(targetCommandBuffer, indexedViewports, indexedScissors, viewportScissorCount);
            else
                SetViewportScissorTracked(targetCommandBuffer, viewport, scissor);

            if (!payload.MeshRenderer.RecordPreparedIndirectDrawState(targetCommandBuffer, recordingState))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.IndirectSecondary.PreparedStateMissing.{GetHashCode()}.{payload.MeshRenderer.GetHashCode()}.{uniformSlot}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Skipping indirect secondary draw because prepared immutable recording state is unavailable. mesh='{0}' target='{1}' slot={2}",
                    payload.MeshRenderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                    target?.Name ?? "<swapchain>",
                    uniformSlot);
                return;
            }

            RecordIndirectDrawPayload(targetCommandBuffer, in payload, allowInlineBarrier: false);
        }

        private int ResolveRunCandidatePassIndex(scoped ref PrimaryCommandBufferRecordingState recordingState, int drawPassIndex)
            => drawPassIndex == int.MinValue &&
                recordingState.ActivePassIndex != int.MinValue
                ? recordingState.ActivePassIndex
                : drawPassIndex;

        private int ResolveIndirectRunCandidatePassIndex(scoped ref PrimaryCommandBufferRecordingState recordingState, int drawPassIndex)
            => drawPassIndex == int.MinValue &&
                recordingState.ActivePassIndex != int.MinValue
                ? recordingState.ActivePassIndex
                : drawPassIndex;

        internal int CountContiguousMeshCommandChainRun(scoped ref PrimaryCommandBufferRecordingState recordingState, int startIndex, in MeshDrawPayload firstDraw, int passIndex)
        {
            bool partitionByScheduledMembership =
                recordingState.ScheduledCommandChainKeysByOpIndex is not null &&
                recordingState.ScheduledCommandChainCache is not null;
            bool firstDrawIsScheduled = partitionByScheduledMembership &&
                TryGetScheduledCommandChainForOp(
                    ref recordingState,
                    startIndex,
                    out _,
                    out _);
            int count = 0;
            for (int i = startIndex; i < recordingState.Ops.Length; i++)
            {
                if (recordingState.PipelineDeferredOperationIndices.Contains(i))
                    break;
                if (recordingState.Ops.GetHeader(i).OpCode != EVulkanPrimaryPlanNodeKind.MeshDraw)
                    break;
                ref readonly MeshDrawPayload candidate = ref recordingState.Ops.GetMeshDraw(i);
                ref readonly FrameOpContext candidateContext = ref recordingState.Ops.GetContext(i);
                XRFrameBuffer? candidateTarget = recordingState.Ops.GetTarget(i);
                if (recordingState.SkipUiPipelineOps && candidateContext.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
                    break;
                if (recordingState.SkipUiBatchTextOps && IsUiBatchTextDrawPayload(in candidate))
                    break;
                if (candidateContext.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
                    break;
                if (candidateTarget != recordingState.Ops.GetTarget(startIndex))
                    break;
                if (!FrameOpContextCompatibility.AreCommandChainBatchCompatible(candidateContext, recordingState.Ops.GetContext(startIndex)))
                    break;
                if (candidateContext.SchedulingIdentity != recordingState.ActiveSchedulingIdentity)
                    break;
                if (ResolveRunCandidatePassIndex(ref recordingState, recordingState.Ops.GetHeader(i).PassIndex) != passIndex)
                    break;
                if (partitionByScheduledMembership &&
                    TryGetScheduledCommandChainForOp(
                        ref recordingState,
                        i,
                        out _,
                        out _) != firstDrawIsScheduled)
                {
                    // The primary recorder supports mixed scheduled-secondary and
                    // inline islands. Keep each attempt inside one membership
                    // class: preflighting a whole pass made one mutable draw reject
                    // every reusable secondary later in that pass.
                    break;
                }

                count++;
            }

            return count;
        }

        internal int CountContiguousMeshCommandChainRun(scoped ref PrimaryCommandBufferRecordingState recordingState, int startIndex, int passIndex)
        {
            ref readonly MeshDrawPayload firstDraw =
                ref recordingState.Ops.GetMeshDraw(startIndex);
            return CountContiguousMeshCommandChainRun(
                ref recordingState,
                startIndex,
                in firstDraw,
                passIndex);
        }

        internal int CountContiguousIndirectCommandChainRun(scoped ref PrimaryCommandBufferRecordingState recordingState, int startIndex, in IndirectDrawPayload firstDraw, int passIndex)
        {
            int count = 0;
            for (int i = startIndex; i < recordingState.Ops.Length; i++)
            {
                if (recordingState.PipelineDeferredOperationIndices.Contains(i))
                    break;
                if (recordingState.Ops.GetHeader(i).OpCode != EVulkanPrimaryPlanNodeKind.IndirectDraw)
                    break;
                ref readonly IndirectDrawPayload candidate = ref recordingState.Ops.GetIndirectDraw(i);
                ref readonly FrameOpContext candidateContext = ref recordingState.Ops.GetContext(i);
                if (!FrameOpContextCompatibility.AreRecordingCompatible(candidateContext, recordingState.ActiveContext))
                    break;
                if (candidateContext.SchedulingIdentity != recordingState.ActiveSchedulingIdentity)
                    break;
                if (recordingState.Ops.GetTarget(i) != recordingState.Ops.GetTarget(startIndex))
                    break;
                if (ResolveIndirectRunCandidatePassIndex(ref recordingState, recordingState.Ops.GetHeader(i).PassIndex) != passIndex)
                    break;
                if (_commandRuntime.EvaluateIndirectSecondaryRecordingContract(in candidate) != EVulkanIndirectSecondaryEligibility.EligibleProducerComplete)
                    break;

                count++;
            }

            return count;
        }

        internal int CountContiguousIndirectCommandChainRun(scoped ref PrimaryCommandBufferRecordingState recordingState, int startIndex, int passIndex)
        {
            ref readonly IndirectDrawPayload firstDraw =
                ref recordingState.Ops.GetIndirectDraw(startIndex);
            return CountContiguousIndirectCommandChainRun(
                ref recordingState,
                startIndex,
                in firstDraw,
                passIndex);
        }

        internal unsafe void EmitIndirectDrawRunReadBarrier(scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            MemoryBarrier memoryBarrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.IndirectCommandReadBit | AccessFlags.ShaderReadBit,
            };

            CmdPipelineBarrierTracked(
                recordingState.CommandBuffer,
                PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
                PipelineStageFlags.DrawIndirectBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
                DependencyFlags.None,
                1,
                &memoryBarrier,
                0,
                null,
                0,
                null);

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 1, redundantCount: 0);
        }

        private static bool IsUiBatchTextDrawPayload(in MeshDrawPayload payload)
        {
            XRMeshRenderer mesh = payload.Draw.Renderer.MeshRenderer;
            XRMaterial? material = payload.Draw.MaterialOverride ?? mesh.Material;
            return string.Equals(material?.Name, "UIBatchTextMaterial", StringComparison.Ordinal) || string.Equals(mesh.Name, "UIBatchTextRenderer", StringComparison.Ordinal) || string.Equals(mesh.Mesh?.Name, "UIBatchTextQuadMesh", StringComparison.Ordinal);
        }
    }
}
