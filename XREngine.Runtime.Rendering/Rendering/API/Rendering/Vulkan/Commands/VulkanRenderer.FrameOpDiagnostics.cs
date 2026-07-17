using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private static bool IsBloomDiagnosticName(string? name)
            => !string.IsNullOrWhiteSpace(name) &&
               name.Contains("Bloom", StringComparison.OrdinalIgnoreCase);

        private static bool IsParallelSecondaryCommandBufferRecordingDisabled()
        {
            string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanDisableParallelSecondaryRecording);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static int ReadFrameOpSignatureDiffLogLimit()
        {
            string? raw = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanFrameOpSignatureDiffLimit);
            return int.TryParse(raw, out int value) && value >= 0 ? value : 48;
        }

        private readonly struct ComputeDispatchPushConstants
        {
            public readonly uint GroupsX;
            public readonly uint GroupsY;
            public readonly uint GroupsZ;
            public readonly uint DebugFlags;

            public ComputeDispatchPushConstants(uint groupsX, uint groupsY, uint groupsZ, uint debugFlags)
            {
                GroupsX = groupsX;
                GroupsY = groupsY;
                GroupsZ = groupsZ;
                DebugFlags = debugFlags;
            }
        }

        private static FrameOpFailureSnapshot CaptureFrameOpFailure(FrameOp op, Exception exception)
        {
            string targetName = op.Target?.Name ?? "<swapchain/null>";
            string materialName = string.Empty;
            string shaderName = string.Empty;

            if (op is MeshDrawOp drawOp)
            {
                var meshRenderer = drawOp.Draw.Renderer.MeshRenderer;
                var material = drawOp.Draw.MaterialOverride ?? meshRenderer.Material;
                materialName = material?.Name ?? "<unnamed material>";
                shaderName =
                    material is not null && material.FragmentShaders.Count > 0
                        ? material.FragmentShaders[0].Name ?? material.FragmentShaders[0].Source?.Name ?? "<unnamed shader>"
                        : "<none>";
            }

            return new FrameOpFailureSnapshot(
                op.GetType().Name,
                op.PassIndex,
                op.Context.PipelineIdentity,
                op.Context.ViewportIdentity,
                targetName,
                materialName,
                shaderName,
                exception.Message);
        }

        private readonly object _lastFrameOpTraceLock = new();
        private FrameOpTraceEntry[] _lastFrameOpTraceEntries = [];
        private ulong _lastFrameOpTraceFrameId;
        private int _lastFrameOpTraceTotalCount;

        private sealed record FrameOpTraceEntry(
            int Index,
            string OpType,
            int PassIndex,
            string PassName,
            string TargetName,
            int TargetIdentity,
            int PipelineIdentity,
            string PipelineName,
            int ViewportIdentity,
            uint DisplayWidth,
            uint DisplayHeight,
            uint InternalWidth,
            uint InternalHeight,
            string Detail);

        private void CaptureLastFrameOpTrace(FrameOp[] ops)
        {
            const int MaxCapturedEntries = 512;
            int count = Math.Min(ops.Length, MaxCapturedEntries);
            FrameOpTraceEntry[] entries = new FrameOpTraceEntry[count];

            for (int i = 0; i < count; i++)
            {
                FrameOp op = ops[i];
                FrameOpContext context = op.Context;
                entries[i] = new FrameOpTraceEntry(
                    i,
                    op.GetType().Name,
                    op.PassIndex,
                    TryGetPassName(op) ?? "<unknown>",
                    ResolveCommandChainTargetName(op),
                    ResolveCommandChainTargetIdentity(op),
                    context.PipelineIdentity,
                    context.PipelineInstance?.Pipeline?.GetType().Name ?? "<no pipeline>",
                    context.ViewportIdentity,
                    context.DisplayWidth,
                    context.DisplayHeight,
                    context.InternalWidth,
                    context.InternalHeight,
                    BuildFrameOpTraceDetail(op));
            }

            lock (_lastFrameOpTraceLock)
            {
                _lastFrameOpTraceFrameId = VulkanFrameCounter;
                _lastFrameOpTraceTotalCount = ops.Length;
                _lastFrameOpTraceEntries = entries;
            }
        }

        private static string BuildFrameOpTraceDetail(FrameOp op)
            => op switch
            {
                MeshDrawOp drawOp => BuildMeshDrawFrameOpTraceDetail(drawOp),
                BlitOp blitOp => $"in='{blitOp.InFbo?.Name ?? "<swapchain>"}' out='{blitOp.OutFbo?.Name ?? "<swapchain>"}'",
                ComputeDispatchOp computeOp => $"compute='{computeOp.Program.Data.Name ?? "<unnamed program>"}' groups={computeOp.GroupsX},{computeOp.GroupsY},{computeOp.GroupsZ}",
                IndirectDrawOp indirectOp => $"renderer='{indirectOp.MeshRenderer.MeshRenderer?.Name ?? "<unnamed renderer>"}' draws={indirectOp.DrawCount}",
                QueryOp queryOp => $"query={queryOp.Operation} target={queryOp.QueryTarget}",
                ClearOp clearOp => $"clearColor={clearOp.ClearColor} clearDepth={clearOp.ClearDepth} clearStencil={clearOp.ClearStencil}",
                _ => string.Empty
            };

        private static string BuildMeshDrawFrameOpTraceDetail(MeshDrawOp drawOp)
        {
            XRMeshRenderer meshRenderer = drawOp.Draw.Renderer.MeshRenderer;
            XRMaterial? material = drawOp.Draw.MaterialOverride ?? meshRenderer.Material;
            return
                $"mesh='{meshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' material='{material?.Name ?? "<unnamed material>"}' instances={drawOp.Draw.Instances}";
        }

        public object GetLastFrameOpTraceDiagnostics(int limit = 128, string? targetContains = null)
        {
            FrameOpTraceEntry[] entries;
            ulong frameId;
            int totalCount;
            lock (_lastFrameOpTraceLock)
            {
                entries = _lastFrameOpTraceEntries;
                frameId = _lastFrameOpTraceFrameId;
                totalCount = _lastFrameOpTraceTotalCount;
            }

            int clampedLimit = Math.Clamp(limit, 1, 512);
            bool hasFilter = !string.IsNullOrWhiteSpace(targetContains);
            List<FrameOpTraceEntry> filtered = new(Math.Min(entries.Length, clampedLimit));
            for (int i = 0; i < entries.Length && filtered.Count < clampedLimit; i++)
            {
                FrameOpTraceEntry entry = entries[i];
                if (hasFilter &&
                    !entry.TargetName.Contains(targetContains!, StringComparison.OrdinalIgnoreCase) &&
                    !entry.Detail.Contains(targetContains!, StringComparison.OrdinalIgnoreCase) &&
                    !entry.PassName.Contains(targetContains!, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                filtered.Add(entry);
            }

            return new
            {
                enabled = FrameOpTraceEnabled,
                frameId,
                totalCount,
                capturedCount = entries.Length,
                returnedCount = filtered.Count,
                targetContains,
                entries = filtered
            };
        }

        private static string BuildFrameOpFailureContext(FrameOp op)
        {
            string pipelineLabel = op.Context.PipelineInstance?.Pipeline?.GetType().Name ?? "<no pipeline>";
            string targetName = op.Target?.Name ?? "<swapchain/null>";

            if (op is MeshDrawOp drawOp)
            {
                var meshRenderer = drawOp.Draw.Renderer.MeshRenderer;
                var material = drawOp.Draw.MaterialOverride ?? meshRenderer.Material;
                string meshName = meshRenderer.Mesh?.Name ?? "<unnamed mesh>";
                string materialName = material?.Name ?? "<unnamed material>";
                string fragmentShaderName =
                    material is not null && material.FragmentShaders.Count > 0
                        ? material.FragmentShaders[0].Name ?? material.FragmentShaders[0].Source?.Name ?? "<unnamed shader>"
                        : "<none>";

                return
                    $"{Environment.NewLine}[Vulkan]   Context: pass={drawOp.PassIndex} target='{targetName}' pipe={drawOp.Context.PipelineIdentity}({pipelineLabel}) vp={drawOp.Context.ViewportIdentity} mesh='{meshName}' material='{materialName}' fragment='{fragmentShaderName}' instances={drawOp.Draw.Instances} stereo={drawOp.Draw.IsStereoPass} unjittered={drawOp.Draw.UseUnjitteredProjection}";
            }

            if (op is IndirectDrawOp indirectOp)
            {
                return
                    $"{Environment.NewLine}[Vulkan]   Context: pass={indirectOp.PassIndex} target='{targetName}' pipe={indirectOp.Context.PipelineIdentity}({pipelineLabel}) vp={indirectOp.Context.ViewportIdentity} drawCount={indirectOp.DrawCount} stride={indirectOp.Stride} useCount={indirectOp.UseCount}";
            }

            if (op is MeshTaskDispatchIndirectCountOp meshTaskOp)
            {
                return
                    $"{Environment.NewLine}[Vulkan]   Context: pass={meshTaskOp.PassIndex} target='{targetName}' pipe={meshTaskOp.Context.PipelineIdentity}({pipelineLabel}) vp={meshTaskOp.Context.ViewportIdentity} maxDrawCount={meshTaskOp.MaxDrawCount} stride={meshTaskOp.Stride}";
            }

            return
                $"{Environment.NewLine}[Vulkan]   Context: pass={op.PassIndex} target='{targetName}' pipe={op.Context.PipelineIdentity}({pipelineLabel}) vp={op.Context.ViewportIdentity}";
        }

        private static string BuildSwapchainWriterDetail(FrameOp op)
        {
            string pipelineLabel = op.Context.PipelineInstance?.Pipeline?.GetType().Name ?? "<no pipeline>";

            return op switch
            {
                MeshDrawOp drawOp =>
                    $"pass={drawOp.PassIndex} pipe={drawOp.Context.PipelineIdentity}({pipelineLabel}) vp={drawOp.Context.ViewportIdentity} mesh='{drawOp.Draw.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' material='{(drawOp.Draw.MaterialOverride ?? drawOp.Draw.Renderer.MeshRenderer.Material)?.Name ?? "<unnamed material>"}' instances={drawOp.Draw.Instances} stereo={drawOp.Draw.IsStereoPass}",
                IndirectDrawOp indirectOp =>
                    $"pass={indirectOp.PassIndex} pipe={indirectOp.Context.PipelineIdentity}({pipelineLabel}) vp={indirectOp.Context.ViewportIdentity} indirectDraws={indirectOp.DrawCount} useCount={indirectOp.UseCount}",
                MeshTaskDispatchIndirectCountOp meshTaskOp =>
                    $"pass={meshTaskOp.PassIndex} pipe={meshTaskOp.Context.PipelineIdentity}({pipelineLabel}) vp={meshTaskOp.Context.ViewportIdentity} meshTaskMaxDraws={meshTaskOp.MaxDrawCount}",
                BlitOp blitOp =>
                    $"pass={blitOp.PassIndex} pipe={blitOp.Context.PipelineIdentity}({pipelineLabel}) vp={blitOp.Context.ViewportIdentity} color={blitOp.ColorBit} depth={blitOp.DepthBit} stencil={blitOp.StencilBit}",
                ClearOp clearOp =>
                    $"pass={clearOp.PassIndex} pipe={clearOp.Context.PipelineIdentity}({pipelineLabel}) vp={clearOp.Context.ViewportIdentity} clearColor={clearOp.ClearColor} clearDepth={clearOp.ClearDepth} clearStencil={clearOp.ClearStencil}",
                _ =>
                    $"pass={op.PassIndex} pipe={op.Context.PipelineIdentity}({pipelineLabel}) vp={op.Context.ViewportIdentity} op={op.GetType().Name}"
            };
        }

        private static void AppendSwapchainWriterSummary(
            StringBuilder builder,
            List<KeyValuePair<int, int>> sortedWriters,
            Dictionary<int, string> writerLabels,
            Dictionary<int, string> pipelineNames,
            int maxEntries)
        {
            int emitted = 0;
            for (int i = 0; i < sortedWriters.Count && emitted < maxEntries; i++)
            {
                KeyValuePair<int, int> writer = sortedWriters[i];
                if (emitted > 0)
                    builder.Append(", ");

                string label = writerLabels.TryGetValue(writer.Key, out string? resolvedLabel)
                    ? resolvedLabel
                    : "Unknown";
                string pipelineName = pipelineNames.TryGetValue(writer.Key, out string? resolvedPipeline)
                    ? resolvedPipeline
                    : "UnknownPipeline";
                builder
                    .Append(label)
                    .Append("#P")
                    .Append(writer.Key)
                    .Append('[')
                    .Append(pipelineName)
                    .Append("]:")
                    .Append(writer.Value);
                emitted++;
            }
        }

        private static void AppendSwapchainWriterDetails(
            StringBuilder builder,
            List<KeyValuePair<int, int>> sortedWriters,
            Dictionary<int, string> writerLabels,
            Dictionary<int, string> writerDetails,
            Dictionary<int, FrameOp> writerOps,
            Dictionary<int, int> writerDynamicUiDrawCounts,
            Dictionary<int, int> writerPasses,
            Dictionary<int, int> writerOpIndices,
            int maxEntries)
        {
            int emitted = 0;
            for (int i = 0; i < sortedWriters.Count && emitted < maxEntries; i++)
            {
                KeyValuePair<int, int> writer = sortedWriters[i];
                if (emitted > 0)
                    builder.Append(" | ");

                string label = writerLabels.TryGetValue(writer.Key, out string? resolvedLabel)
                    ? resolvedLabel
                    : "Unknown";
                int passIndex = writerPasses.TryGetValue(writer.Key, out int pass)
                    ? pass
                    : int.MinValue;
                int opIndex = writerOpIndices.TryGetValue(writer.Key, out int op)
                    ? op
                    : -1;
                builder
                    .Append(label)
                    .Append("@pass")
                    .Append(passIndex)
                    .Append("/op")
                    .Append(opIndex)
                    .Append(": ");
                AppendSwapchainWriterDetail(builder, writer.Key, writerDetails, writerOps, writerDynamicUiDrawCounts);
                emitted++;
            }
        }

        private static void AppendSwapchainWriterDetail(
            StringBuilder builder,
            int pipelineIdentity,
            Dictionary<int, string> writerDetails,
            Dictionary<int, FrameOp> writerOps,
            Dictionary<int, int> writerDynamicUiDrawCounts)
        {
            if (writerOps.TryGetValue(pipelineIdentity, out FrameOp? writerOp))
            {
                builder.Append(BuildSwapchainWriterDetail(writerOp));
                return;
            }

            if (writerDynamicUiDrawCounts.TryGetValue(pipelineIdentity, out int dynamicDrawCount))
            {
                builder
                    .Append("secondary overlay draws=")
                    .Append(dynamicDrawCount);
                return;
            }

            builder.Append(writerDetails.TryGetValue(pipelineIdentity, out string? detail)
                ? detail
                : "<no detail>");
        }

        private static bool IsUiBatchTextDrawOp(FrameOp op)
        {
            if (op is not MeshDrawOp drawOp)
                return false;

            VkMeshRenderer? renderer = drawOp.Draw.Renderer;
            if (renderer is null)
                return false;

            XRMeshRenderer meshRenderer = renderer.MeshRenderer;
            XRMaterial? material = drawOp.Draw.MaterialOverride ?? meshRenderer.Material;
            return
                string.Equals(material?.Name, "UIBatchTextMaterial", StringComparison.Ordinal) ||
                string.Equals(meshRenderer.Name, "UIBatchTextRenderer", StringComparison.Ordinal) ||
                string.Equals(meshRenderer.Mesh?.Name, "UIBatchTextQuadMesh", StringComparison.Ordinal);
        }

        private static bool IsDynamicUiOverlayDrawOp(FrameOp op)
        {
            if (IsUiBatchTextDrawOp(op))
                return true;

            if (op is not MeshDrawOp drawOp ||
                drawOp.Target is not null ||
                drawOp.PassIndex != (int)EDefaultRenderPass.OnTopForward ||
                drawOp.Context.PipelineInstance?.Pipeline is not UserInterfaceRenderPipeline)
            {
                return false;
            }

            PendingMeshDraw draw = drawOp.Draw;
            Matrix4x4 model = draw.ModelMatrix;
            float width = MathF.Abs(model.M11);
            float height = MathF.Abs(model.M22);
            if (width < 1.0f || height < 1.0f)
                return false;

            float maxX = MathF.Max(draw.RenderAreaWidth, drawOp.Context.DisplayWidth);
            float maxY = MathF.Max(draw.RenderAreaHeight, drawOp.Context.DisplayHeight);
            if (maxX <= 0.0f || maxY <= 0.0f)
                return false;

            const float edgeTolerance = 4.0f;
            return !draw.IsStereoPass &&
                model.M41 >= -edgeTolerance &&
                model.M42 >= -edgeTolerance &&
                model.M41 <= maxX + edgeTolerance &&
                model.M42 <= maxY + edgeTolerance;
        }

        private static bool HasTextureUploadFrameOps(FrameOp[] ops)
        {
            for (int i = 0; i < ops.Length; i++)
            {
                if (ops[i] is TextureUploadFrameOp)
                    return true;
            }

            return false;
        }

        private static bool HasMutableGpuDrivenFrameOps(FrameOp[] ops)
        {
            for (int i = 0; i < ops.Length; i++)
            {
                // The count and argument buffers are produced by compute work every
                // frame. Re-recording the primary preserves the producer/consumer
                // dependency chain and avoids resubmitting a cached primary across
                // mutable GPU publications.
                if (ops[i] is ComputeDispatchOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp)
                    return true;
            }

            return false;
        }

        private static FrameOp[] FilterDiagnosticSkippedFrameOps(FrameOp[] ops)
        {
            if (ops.Length == 0 || !XREngine.Rendering.RenderDiagnosticsFlags.VkSkipUiBatchText)
                return ops;

            int keepCount = 0;
            for (int i = 0; i < ops.Length; i++)
            {
                if (!IsUiBatchTextDrawOp(ops[i]))
                    keepCount++;
            }

            if (keepCount == ops.Length)
                return ops;

            FrameOp[] filtered = new FrameOp[keepCount];
            int writeIndex = 0;
            for (int i = 0; i < ops.Length; i++)
            {
                if (!IsUiBatchTextDrawOp(ops[i]))
                    filtered[writeIndex++] = ops[i];
            }

            Debug.VulkanEvery(
                "Vulkan.SkipUiBatchText.FilterFrameOps",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Filtered {0} batched UI text frame ops before command-buffer signature due to XRE_SKIP_UI_BATCH_TEXT=1.",
                ops.Length - filtered.Length);
            return filtered;
        }

        private FrameOp[] _staticFrameOpsSplitBuffer = Array.Empty<FrameOp>();
        private FrameOp[] _dynamicUiBatchTextFrameOpsSplitBuffer = Array.Empty<FrameOp>();

        private void SplitDynamicUiBatchTextFrameOps(
            FrameOp[] ops,
            out FrameOp[] staticOps,
            out FrameOp[] dynamicUiBatchTextOps)
        {
            if (ops.Length == 0)
            {
                staticOps = ops;
                dynamicUiBatchTextOps = Array.Empty<FrameOp>();
                return;
            }

            int dynamicCount = 0;
            for (int i = 0; i < ops.Length; i++)
            {
                if (IsDynamicUiOverlayDrawOp(ops[i]))
                    dynamicCount++;
            }

            if (dynamicCount == 0)
            {
                staticOps = ops;
                dynamicUiBatchTextOps = Array.Empty<FrameOp>();
                return;
            }

            int staticCount = ops.Length - dynamicCount;
            if (_staticFrameOpsSplitBuffer.Length != staticCount)
                _staticFrameOpsSplitBuffer = new FrameOp[staticCount];
            if (_dynamicUiBatchTextFrameOpsSplitBuffer.Length != dynamicCount)
                _dynamicUiBatchTextFrameOpsSplitBuffer = new FrameOp[dynamicCount];

            staticOps = _staticFrameOpsSplitBuffer;
            dynamicUiBatchTextOps = _dynamicUiBatchTextFrameOpsSplitBuffer;
            int staticIndex = 0;
            int dynamicIndex = 0;
            for (int i = 0; i < ops.Length; i++)
            {
                FrameOp op = ops[i];
                if (IsDynamicUiOverlayDrawOp(op))
                    dynamicUiBatchTextOps[dynamicIndex++] = op;
                else
                    staticOps[staticIndex++] = op;
            }
        }

        private string BuildVulkanFrameDiagnosticSummary(
            FrameOp[] ops,
            int clearCount,
            int drawCount,
            int blitCount,
            int computeCount,
            int sceneSwapchainWriters,
            int overlaySwapchainWriters,
            int forcedDiagnosticSwapchainWriters,
            int fboOnlyDrawOps,
            int fboOnlyBlitOps,
            string swapchainWriterSummary,
            in FrameOpContext context,
            FrameOpFailureSnapshot? firstFailure)
        {
            string opSummary = string.Join(", ",
                ops.Take(12).Select(op =>
                    $"{op.GetType().Name}:p{op.PassIndex}:pipe{op.Context.PipelineIdentity}:vp{op.Context.ViewportIdentity}:target={op.Target?.Name ?? "<swapchain>"}"));

            string passSummary = context.PassMetadata is null
                ? "passes=<null>"
                : $"passes={context.PassMetadata.Count}[{string.Join(", ", context.PassMetadata.Take(10).Select(p => $"{p.PassIndex}:{p.Name}"))}]";

            string registrySummary = context.ResourceRegistry is null
                ? "registry=<null>"
                : $"registry=fbo({string.Join(", ", context.ResourceRegistry.FrameBufferRecords.Keys.Take(8))}) tex({string.Join(", ", context.ResourceRegistry.TextureRecords.Keys.Take(8))}) buf({string.Join(", ", context.ResourceRegistry.BufferRecords.Keys.Take(8))})";

            string physicalSummary =
                $"plannerRev={ResourcePlannerRevision} logicalImages={ResourceAllocator.LogicalTextureAllocations.Count} physicalImages={ResourceAllocator.EnumeratePhysicalGroups().Count()} logicalBuffers={ResourceAllocator.LogicalBufferAllocations.Count} physicalBuffers={ResourceAllocator.EnumeratePhysicalBufferGroups().Count()}";

            string failureSummary = firstFailure is { } failure
                ? $"firstFailure={failure.OpType} pass={failure.PassIndex} pipe={failure.PipelineIdentity} vp={failure.ViewportIdentity} target={failure.TargetName} material={failure.MaterialName} shader={failure.ShaderName} message={failure.Message}"
                : "firstFailure=<none>";

            return
                $"ops={ops.Length} C/D/B/Comp={clearCount}/{drawCount}/{blitCount}/{computeCount}; " +
                $"writers scene={sceneSwapchainWriters} overlay={overlaySwapchainWriters} forcedDiag={forcedDiagnosticSwapchainWriters} fboOnlyD/B={fboOnlyDrawOps}/{fboOnlyBlitOps}; " +
                $"swapchain={swapchainWriterSummary}; descriptorSkips={RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorBindSkips} descriptorFallbacks={RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorFallbacksCurrentFrame} descriptorFailures={RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorBindingFailuresCurrentFrame} oomFallbacks={RuntimeEngine.Rendering.Stats.Vulkan.VulkanOomFallbackCount}; " +
                $"validationCurrent={RuntimeEngine.Rendering.Stats.Vulkan.VulkanValidationMessageCountCurrentFrame}/{RuntimeEngine.Rendering.Stats.Vulkan.VulkanValidationErrorCountCurrentFrame}; " +
                $"{failureSummary}; {passSummary}; {registrySummary}; {physicalSummary}; opList=[{opSummary}]";
        }

        private static void RecordVulkanFrameOpCensus(
            FrameOp[] ops,
            int clearCount,
            int meshDrawCount,
            int indirectDrawCount,
            int meshTaskDispatchCount,
            int blitCount,
            int computeCount,
            int swapchainWriteCount,
            int fboWriteCount)
        {
            if (!RuntimeEngine.Rendering.Stats.EnableTracking)
                return;

            const int StackUniqueLimit = 256;
            if (ops.Length <= StackUniqueLimit)
            {
                Span<int> passIds = stackalloc int[StackUniqueLimit];
                Span<int> contextIds = stackalloc int[StackUniqueLimit];
                Span<int> targetIds = stackalloc int[StackUniqueLimit];
                RecordVulkanFrameOpCensusCore(
                    ops,
                    clearCount,
                    meshDrawCount,
                    indirectDrawCount,
                    meshTaskDispatchCount,
                    blitCount,
                    computeCount,
                    swapchainWriteCount,
                    fboWriteCount,
                    passIds,
                    contextIds,
                    targetIds);
                return;
            }

            int[]? rentedPassIds = null;
            int[]? rentedContextIds = null;
            int[]? rentedTargetIds = null;
            try
            {
                rentedPassIds = ArrayPool<int>.Shared.Rent(ops.Length);
                rentedContextIds = ArrayPool<int>.Shared.Rent(ops.Length);
                rentedTargetIds = ArrayPool<int>.Shared.Rent(ops.Length);
                RecordVulkanFrameOpCensusCore(
                    ops,
                    clearCount,
                    meshDrawCount,
                    indirectDrawCount,
                    meshTaskDispatchCount,
                    blitCount,
                    computeCount,
                    swapchainWriteCount,
                    fboWriteCount,
                    rentedPassIds.AsSpan(0, ops.Length),
                    rentedContextIds.AsSpan(0, ops.Length),
                    rentedTargetIds.AsSpan(0, ops.Length));
            }
            finally
            {
                if (rentedPassIds is not null)
                    ArrayPool<int>.Shared.Return(rentedPassIds, clearArray: true);
                if (rentedContextIds is not null)
                    ArrayPool<int>.Shared.Return(rentedContextIds, clearArray: true);
                if (rentedTargetIds is not null)
                    ArrayPool<int>.Shared.Return(rentedTargetIds, clearArray: true);
            }
        }

        private static void RecordVulkanFrameOpCensusCore(
            FrameOp[] ops,
            int clearCount,
            int meshDrawCount,
            int indirectDrawCount,
            int meshTaskDispatchCount,
            int blitCount,
            int computeCount,
            int swapchainWriteCount,
            int fboWriteCount,
            Span<int> passIds,
            Span<int> contextIds,
            Span<int> targetIds)
        {
            int uniquePassCount = 0;
            int uniqueContextCount = 0;
            int uniqueTargetCount = 0;

            for (int i = 0; i < ops.Length; i++)
            {
                FrameOp op = ops[i];
                AddUnique(passIds, ref uniquePassCount, op.PassIndex);
                AddUnique(contextIds, ref uniqueContextCount, op.Context.SchedulingIdentity);
                AddUnique(targetIds, ref uniqueTargetCount, ResolveFrameOpTargetIdentity(op));
            }

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameOpCensus(
                ops.Length,
                clearCount,
                meshDrawCount,
                indirectDrawCount,
                meshTaskDispatchCount,
                blitCount,
                computeCount,
                swapchainWriteCount,
                fboWriteCount,
                uniquePassCount,
                uniqueContextCount,
                uniqueTargetCount);
        }

        private static bool AddUnique(Span<int> values, ref int count, int value)
        {
            for (int i = 0; i < count; i++)
            {
                if (values[i] == value)
                    return false;
            }

            values[count++] = value;
            return true;
        }

        private static int ResolveFrameOpTargetIdentity(FrameOp op)
        {
            XRFrameBuffer? target = op switch
            {
                BlitOp blit => blit.OutFbo,
                _ => op.Target,
            };

            return target?.GetHashCode() ?? int.MinValue;
        }

        private void UpdateVulkanOnScreenDiagnostic(string pipelineLabel, ColorF4 clearColor, int droppedDrawOps, int droppedOps, string swapchainWriter)
        {
            string currentTitle = Window?.Title ?? string.Empty;
            if (string.IsNullOrWhiteSpace(_vulkanDiagnosticBaseWindowTitle))
                _vulkanDiagnosticBaseWindowTitle = currentTitle;

            string baseTitle = _vulkanDiagnosticBaseWindowTitle ?? string.Empty;
            string diagnosticTitle =
                $"{baseTitle} | VK[{pipelineLabel}] clr=({clearColor.R:F2},{clearColor.G:F2},{clearColor.B:F2},{clearColor.A:F2}) sw={swapchainWriter} dropDraw={droppedDrawOps} dropOps={droppedOps}";

            if (string.Equals(_vulkanDiagnosticLastTitle, diagnosticTitle, StringComparison.Ordinal) &&
                _vulkanLastFrameDroppedDrawOps == droppedDrawOps &&
                _vulkanLastFrameDroppedOps == droppedOps)
            {
                return;
            }

            _vulkanDiagnosticLastTitle = diagnosticTitle;
            _vulkanLastFrameDroppedDrawOps = droppedDrawOps;
            _vulkanLastFrameDroppedOps = droppedOps;
            if (Window is not null)
                Window.Title = diagnosticTitle;
        }
    }
}
