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
    internal sealed unsafe partial class VulkanCommandRuntime
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
            IndirectDrawOp indirectOp,
            in VkMeshRenderer.IndirectDrawRecordingState recordingState,
            int passIndex,
            bool inheritedDynamicRendering,
            RenderPass inheritedRenderPass,
            DynamicRenderingFormatSignature inheritedDynamicRenderingFormats,
            bool inheritedDepthStencilReadOnly,
            int uniformSlot)
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

            if (!indirectOp.MeshRenderer.RecordPreparedIndirectDrawState(targetCommandBuffer, recordingState))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.IndirectSecondary.PreparedStateMissing.{GetHashCode()}.{indirectOp.MeshRenderer.GetHashCode()}.{uniformSlot}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Skipping indirect secondary draw because prepared immutable recording state is unavailable. mesh='{0}' target='{1}' slot={2}",
                    indirectOp.MeshRenderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                    indirectOp.Target?.Name ?? "<swapchain>",
                    uniformSlot);
                return;
            }

            RecordIndirectDrawOp(targetCommandBuffer, indirectOp, allowInlineBarrier: false);
        }

        private int ResolveRunCandidatePassIndex(scoped ref PrimaryCommandBufferRecordingState recordingState, MeshDrawOp drawOp)
            => drawOp.PassIndex == int.MinValue &&
                recordingState.ActivePassIndex != int.MinValue
                ? recordingState.ActivePassIndex
                : drawOp.PassIndex;

        private int ResolveIndirectRunCandidatePassIndex(scoped ref PrimaryCommandBufferRecordingState recordingState, IndirectDrawOp drawOp)
            => drawOp.PassIndex == int.MinValue &&
                recordingState.ActivePassIndex != int.MinValue
                ? recordingState.ActivePassIndex
                : drawOp.PassIndex;

        internal int CountContiguousMeshCommandChainRun(scoped ref PrimaryCommandBufferRecordingState recordingState, int startIndex, MeshDrawOp firstDraw, int passIndex)
        {
            int count = 0;
            for (int i = startIndex; i < recordingState.Ops.Length; i++)
            {
                if (recordingState.PipelineDeferredOps.Contains(recordingState.Ops[i]))
                    break;
                if (recordingState.Ops[i] is not MeshDrawOp candidate)
                    break;
                if (recordingState.SkipUiPipelineOps && candidate.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
                    break;
                if (recordingState.SkipUiBatchTextOps && IsUiBatchTextDrawOp(candidate))
                    break;
                if (candidate.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
                    break;
                if (candidate.Target != firstDraw.Target)
                    break;
                if (!FrameOpContextCompatibility.AreCommandChainBatchCompatible(candidate.Context, firstDraw.Context))
                    break;
                if (candidate.Context.SchedulingIdentity != recordingState.ActiveSchedulingIdentity)
                    break;
                if (ResolveRunCandidatePassIndex(ref recordingState, candidate) != passIndex)
                    break;

                count++;
            }

            return count;
        }

        internal int CountContiguousIndirectCommandChainRun(scoped ref PrimaryCommandBufferRecordingState recordingState, int startIndex, IndirectDrawOp firstDraw, int passIndex)
        {
            int count = 0;
            for (int i = startIndex; i < recordingState.Ops.Length; i++)
            {
                if (recordingState.PipelineDeferredOps.Contains(recordingState.Ops[i]))
                    break;
                if (recordingState.Ops[i] is not IndirectDrawOp candidate)
                    break;
                if (!FrameOpContextCompatibility.AreRecordingCompatible(candidate.Context, recordingState.ActiveContext))
                    break;
                if (candidate.Context.SchedulingIdentity != recordingState.ActiveSchedulingIdentity)
                    break;
                if (candidate.Target != firstDraw.Target)
                    break;
                if (ResolveIndirectRunCandidatePassIndex(ref recordingState, candidate) != passIndex)
                    break;
                if (_commandRuntime.EvaluateIndirectSecondaryRecordingContract(candidate) != EVulkanIndirectSecondaryEligibility.EligibleProducerComplete)
                    break;

                count++;
            }

            return count;
        }

        internal void EmitIndirectDrawRunReadBarrier(scoped ref PrimaryCommandBufferRecordingState recordingState)
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
    }
}
