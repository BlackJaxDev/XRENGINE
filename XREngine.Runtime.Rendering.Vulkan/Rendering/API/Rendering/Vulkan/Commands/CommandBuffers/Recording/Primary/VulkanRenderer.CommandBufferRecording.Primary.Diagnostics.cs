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

        private void RememberPipelineName(scoped ref PrimaryCommandBufferRecordingState recordingState, in FrameOpContext context)
        {
            if (!recordingState.PipelineNameByIdentity.ContainsKey(context.PipelineIdentity))
            {
                string? name = context.PipelineInstance?.Pipeline?.GetType().Name;
                if (string.IsNullOrWhiteSpace(name))
                    name = "UnknownPipeline";
                recordingState.PipelineNameByIdentity[context.PipelineIdentity] = name;
            }
        }

        private static string DescribeFrameOpTraceDetails(FrameOp op)
            => op switch
            {
                ComputeDispatchOp compute =>
                    $" compute='{compute.Program.Data.Name ?? "<unnamed program>"}' groups={compute.GroupsX},{compute.GroupsY},{compute.GroupsZ} uniforms={compute.Snapshot.Uniforms.Count} samplers={compute.Snapshot.Samplers.Count + compute.Snapshot.SamplersByName.Count} images={compute.Snapshot.Images.Count} buffers={compute.Snapshot.Buffers.Count}",
                ComputeDispatchIndirectOp computeIndirect =>
                    $" computeIndirect='{computeIndirect.Program.Data.Name ?? "<unnamed program>"}' args=0x{computeIndirect.ArgumentBuffer.Handle:X}+{computeIndirect.ArgumentOffset}",
                BufferCopyOp copy =>
                    $" copy=0x{copy.SourceBuffer.Handle:X}+{copy.SourceOffset}->0x{copy.DestinationBuffer.Handle:X}+{copy.DestinationOffset} bytes={copy.ByteCount}",
                SubmissionMarkerOp marker => $" marker='{marker.Label}'",
                IndirectDrawOp indirect =>
                    $" renderer='{indirect.MeshRenderer.MeshRenderer?.Name ?? "<unnamed renderer>"}' draws={indirect.DrawCount} stride={indirect.Stride} offset={indirect.ByteOffset} countOffset={indirect.CountByteOffset} useCount={indirect.UseCount}",
                QueryOp query =>
                    $" query={query.Operation} descriptor={query.Descriptor}",
                BlitOp blit =>
                    $" in='{blit.InFbo?.Name ?? "<swapchain>"}' out='{blit.OutFbo?.Name ?? "<swapchain>"}' color={blit.ColorBit} depth={blit.DepthBit} stencil={blit.StencilBit}",
                _ => string.Empty
            };

        private void MarkSwapchainWriterCore(scoped ref PrimaryCommandBufferRecordingState recordingState, string writerLabel, int passIndex, int opIndex, int pipelineIdentity)
        {
            recordingState.SwapchainLastWriter = writerLabel;
            recordingState.SwapchainLastWriterPass = passIndex;
            recordingState.SwapchainLastWriterOpIndex = opIndex;
            recordingState.SwapchainWritesByPipeline.TryGetValue(pipelineIdentity, out int count);
            recordingState.SwapchainWritesByPipeline[pipelineIdentity] = count + 1;
            recordingState.SwapchainWriterLabelByPipeline[pipelineIdentity] = writerLabel;
            recordingState.SwapchainWriterPassByPipeline[pipelineIdentity] = passIndex;
            recordingState.SwapchainWriterOpIndexByPipeline[pipelineIdentity] = opIndex;
        }

        private void MarkSwapchainFrameOpWriter(scoped ref PrimaryCommandBufferRecordingState recordingState, string writerLabel, FrameOp op, int passIndex, int opIndex, int pipelineIdentity)
        {
            MarkSwapchainWriterCore(ref recordingState, writerLabel, passIndex, opIndex, pipelineIdentity);
            recordingState.SwapchainWriterOpByPipeline[pipelineIdentity] = op;
            recordingState.SwapchainWriterDetailByPipeline.Remove(pipelineIdentity);
            recordingState.SwapchainWriterDynamicUiDrawCountByPipeline.Remove(pipelineIdentity);
        }

        private void MarkSwapchainStaticWriter(scoped ref PrimaryCommandBufferRecordingState recordingState, string writerLabel, string writerDetail, int passIndex, int opIndex, int pipelineIdentity)
        {
            MarkSwapchainWriterCore(ref recordingState, writerLabel, passIndex, opIndex, pipelineIdentity);
            recordingState.SwapchainWriterDetailByPipeline[pipelineIdentity] = writerDetail;
            recordingState.SwapchainWriterOpByPipeline.Remove(pipelineIdentity);
            recordingState.SwapchainWriterDynamicUiDrawCountByPipeline.Remove(pipelineIdentity);
        }

        private void MarkSwapchainDynamicUiWriter(scoped ref PrimaryCommandBufferRecordingState recordingState, string writerLabel, int drawCount, int passIndex, int opIndex, int pipelineIdentity)
        {
            MarkSwapchainWriterCore(ref recordingState, writerLabel, passIndex, opIndex, pipelineIdentity);
            recordingState.SwapchainWriterDynamicUiDrawCountByPipeline[pipelineIdentity] = drawCount;
            recordingState.SwapchainWriterOpByPipeline.Remove(pipelineIdentity);
            recordingState.SwapchainWriterDetailByPipeline.Remove(pipelineIdentity);
        }

        private static bool IsOverlayContext(FrameOpContext context)
            => context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline;

        private void CountLogicalSwapchainWriter(scoped ref PrimaryCommandBufferRecordingState recordingState, FrameOpContext context)
        {
            if (IsOverlayContext(context))
                recordingState.OverlaySwapchainWriters++;
            else
                recordingState.SceneSwapchainWriters++;
        }

        private void LogSwapchainWritersByPipeline(scoped ref PrimaryCommandBufferRecordingState recordingState, string phase)
        {
            if (!VulkanFrameDiagnosticsTraceEnabled)
                return;

            if (recordingState.SwapchainWritesByPipeline.Count == 0)
                return;

            TimeSpan logInterval = TimeSpan.FromSeconds(1);
            string summaryKey = $"Vulkan.FrameOpsByPipeline.{phase}.{GetHashCode()}";
            string detailKey = $"Vulkan.FrameOpsByPipeline.{phase}.Details.{GetHashCode()}";
            bool shouldLogSummary = Debug.ShouldLogEvery(summaryKey, logInterval);
            bool shouldLogDetails = Debug.ShouldLogEvery(detailKey, logInterval);
            if (!shouldLogSummary && !shouldLogDetails)
                return;

            List<KeyValuePair<int, int>> sortedWriters = recordingState.RecordingScratch.SwapchainWriterCountSort;
            sortedWriters.Clear();
            sortedWriters.EnsureCapacity(recordingState.SwapchainWritesByPipeline.Count);
            foreach (KeyValuePair<int, int> pair in recordingState.SwapchainWritesByPipeline)
                sortedWriters.Add(pair);
            sortedWriters.Sort(static (left, right) => right.Value.CompareTo(left.Value));

            if (shouldLogSummary)
            {
                StringBuilder builder = recordingState.RecordingScratch.SwapchainWriterSummaryBuilder;
                builder.Clear();
                AppendSwapchainWriterSummary(
                    builder,
                    sortedWriters,
                    recordingState.SwapchainWriterLabelByPipeline,
                    recordingState.PipelineNameByIdentity,
                    maxEntries: 6);
                Debug.Vulkan(
                    "[Vulkan] Swapchain writers by pipeline ({0}): {1}",
                    phase,
                    builder.ToString());
            }

            if (shouldLogDetails)
            {
                StringBuilder builder = recordingState.RecordingScratch.SwapchainWriterSummaryBuilder;
                builder.Clear();
                AppendSwapchainWriterDetails(
                    builder,
                    sortedWriters,
                    recordingState.SwapchainWriterLabelByPipeline,
                    recordingState.SwapchainWriterDetailByPipeline,
                    recordingState.SwapchainWriterOpByPipeline,
                    recordingState.SwapchainWriterDynamicUiDrawCountByPipeline,
                    recordingState.SwapchainWriterPassByPipeline,
                    recordingState.SwapchainWriterOpIndexByPipeline,
                    maxEntries: 4);
                Debug.Vulkan(
                    "[Vulkan] Swapchain writer details ({0}): {1}",
                    phase,
                    builder.ToString());
            }
        }
    }
}
