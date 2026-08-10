using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using XREngine.Data.Colors;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    private readonly VulkanFrameOpDiagnosticsState _recordingFrameOpDiagnostics = new();
    private string? _lastOnScreenDiagnostic;

    private static FrameOpFailureSnapshot CaptureFrameOpFailure(
        FrameOp operation,
        Exception exception)
    {
        string targetName = operation.Target?.Name ?? "<swapchain/null>";
        string materialName = string.Empty;
        string shaderName = string.Empty;
        if (operation is MeshDrawOp draw)
        {
            XRMeshRenderer meshRenderer = draw.Draw.Renderer.MeshRenderer;
            XRMaterial? material = draw.Draw.MaterialOverride ?? meshRenderer.Material;
            materialName = material?.Name ?? "<unnamed material>";
            shaderName = material is not null && material.FragmentShaders.Count > 0
                ? material.FragmentShaders[0].Name ??
                  material.FragmentShaders[0].Source?.Name ??
                  "<unnamed shader>"
                : "<none>";
        }

        return new FrameOpFailureSnapshot(
            operation.GetType().Name,
            operation.PassIndex,
            operation.Context.PipelineIdentity,
            operation.Context.ViewportIdentity,
            targetName,
            materialName,
            shaderName,
            exception.Message);
    }

    private static string BuildFrameOpFailureContext(FrameOp operation)
    {
        string pipelineLabel = operation.Context.PipelineInstance?.Pipeline?.GetType().Name ?? "<no pipeline>";
        string targetName = operation.Target?.Name ?? "<swapchain/null>";
        if (operation is MeshDrawOp draw)
        {
            XRMeshRenderer meshRenderer = draw.Draw.Renderer.MeshRenderer;
            XRMaterial? material = draw.Draw.MaterialOverride ?? meshRenderer.Material;
            string fragmentShaderName = material is not null && material.FragmentShaders.Count > 0
                ? material.FragmentShaders[0].Name ??
                  material.FragmentShaders[0].Source?.Name ??
                  "<unnamed shader>"
                : "<none>";
            return
                $"{Environment.NewLine}[Vulkan]   Context: pass={draw.PassIndex} target='{targetName}' pipe={draw.Context.PipelineIdentity}({pipelineLabel}) vp={draw.Context.ViewportIdentity} mesh='{meshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' material='{material?.Name ?? "<unnamed material>"}' fragment='{fragmentShaderName}' instances={draw.Draw.Instances} stereo={draw.Draw.IsStereoPass} unjittered={draw.Draw.UseUnjitteredProjection}";
        }

        if (operation is IndirectDrawOp indirect)
            return $"{Environment.NewLine}[Vulkan]   Context: pass={indirect.PassIndex} target='{targetName}' pipe={indirect.Context.PipelineIdentity}({pipelineLabel}) vp={indirect.Context.ViewportIdentity} drawCount={indirect.DrawCount} stride={indirect.Stride} useCount={indirect.UseCount}";

        if (operation is MeshTaskDispatchIndirectCountOp meshTask)
            return $"{Environment.NewLine}[Vulkan]   Context: pass={meshTask.PassIndex} target='{targetName}' pipe={meshTask.Context.PipelineIdentity}({pipelineLabel}) vp={meshTask.Context.ViewportIdentity} maxDrawCount={meshTask.MaxDrawCount} stride={meshTask.Stride}";

        return $"{Environment.NewLine}[Vulkan]   Context: pass={operation.PassIndex} target='{targetName}' pipe={operation.Context.PipelineIdentity}({pipelineLabel}) vp={operation.Context.ViewportIdentity}";
    }

    private static bool IsUiBatchTextDrawOp(FrameOp operation)
    {
        if (operation is not MeshDrawOp draw)
            return false;

        XRMeshRenderer meshRenderer = draw.Draw.Renderer.MeshRenderer;
        XRMaterial? material = draw.Draw.MaterialOverride ?? meshRenderer.Material;
        return
            string.Equals(material?.Name, "UIBatchTextMaterial", StringComparison.Ordinal) ||
            string.Equals(meshRenderer.Name, "UIBatchTextRenderer", StringComparison.Ordinal) ||
            string.Equals(meshRenderer.Mesh?.Name, "UIBatchTextQuadMesh", StringComparison.Ordinal);
    }

    private static string BuildSwapchainWriterDetail(FrameOp operation)
    {
        string pipelineLabel = operation.Context.PipelineInstance?.Pipeline?.GetType().Name ?? "<no pipeline>";
        return operation switch
        {
            MeshDrawOp draw =>
                $"pass={draw.PassIndex} pipe={draw.Context.PipelineIdentity}({pipelineLabel}) vp={draw.Context.ViewportIdentity} mesh='{draw.Draw.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' material='{(draw.Draw.MaterialOverride ?? draw.Draw.Renderer.MeshRenderer.Material)?.Name ?? "<unnamed material>"}' instances={draw.Draw.Instances} stereo={draw.Draw.IsStereoPass}",
            IndirectDrawOp indirect =>
                $"pass={indirect.PassIndex} pipe={indirect.Context.PipelineIdentity}({pipelineLabel}) vp={indirect.Context.ViewportIdentity} indirectDraws={indirect.DrawCount} useCount={indirect.UseCount}",
            MeshTaskDispatchIndirectCountOp meshTask =>
                $"pass={meshTask.PassIndex} pipe={meshTask.Context.PipelineIdentity}({pipelineLabel}) vp={meshTask.Context.ViewportIdentity} meshTaskMaxDraws={meshTask.MaxDrawCount}",
            BlitOp blit =>
                $"pass={blit.PassIndex} pipe={blit.Context.PipelineIdentity}({pipelineLabel}) vp={blit.Context.ViewportIdentity} color={blit.ColorBit} depth={blit.DepthBit} stencil={blit.StencilBit}",
            ClearOp clear =>
                $"pass={clear.PassIndex} pipe={clear.Context.PipelineIdentity}({pipelineLabel}) vp={clear.Context.ViewportIdentity} clearColor={clear.ClearColor} clearDepth={clear.ClearDepth} clearStencil={clear.ClearStencil}",
            _ =>
                $"pass={operation.PassIndex} pipe={operation.Context.PipelineIdentity}({pipelineLabel}) vp={operation.Context.ViewportIdentity} op={operation.GetType().Name}",
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
        for (int index = 0; index < sortedWriters.Count && emitted < maxEntries; index++)
        {
            KeyValuePair<int, int> writer = sortedWriters[index];
            if (emitted > 0)
                builder.Append(", ");
            string label = writerLabels.TryGetValue(writer.Key, out string? resolvedLabel)
                ? resolvedLabel
                : "Unknown";
            string pipelineName = pipelineNames.TryGetValue(writer.Key, out string? resolvedPipeline)
                ? resolvedPipeline
                : "UnknownPipeline";
            builder.Append(label)
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
        for (int index = 0; index < sortedWriters.Count && emitted < maxEntries; index++)
        {
            KeyValuePair<int, int> writer = sortedWriters[index];
            if (emitted > 0)
                builder.Append(" | ");
            string label = writerLabels.TryGetValue(writer.Key, out string? resolvedLabel)
                ? resolvedLabel
                : "Unknown";
            int passIndex = writerPasses.TryGetValue(writer.Key, out int pass)
                ? pass
                : int.MinValue;
            int operationIndex = writerOpIndices.TryGetValue(writer.Key, out int operation)
                ? operation
                : -1;
            builder.Append(label)
                .Append("@pass")
                .Append(passIndex)
                .Append("/op")
                .Append(operationIndex)
                .Append(": ");
            if (writerOps.TryGetValue(writer.Key, out FrameOp? writerOperation))
                builder.Append(BuildSwapchainWriterDetail(writerOperation));
            else if (writerDynamicUiDrawCounts.TryGetValue(writer.Key, out int dynamicDrawCount))
                builder.Append("secondary overlay draws=").Append(dynamicDrawCount);
            else
                builder.Append(writerDetails.TryGetValue(writer.Key, out string? detail) ? detail : "<no detail>");
            emitted++;
        }
    }

    private string BuildVulkanFrameDiagnosticSummary(
        FrameOperationSequence operations,
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
        StringBuilder operationSummary = new();
        int summaryCount = Math.Min(operations.Length, 12);
        for (int index = 0; index < summaryCount; index++)
        {
            if (index > 0)
                operationSummary.Append(", ");
            FrameOp operation = operations[index];
            operationSummary.Append(operation.GetType().Name)
                .Append(":p").Append(operation.PassIndex)
                .Append(":pipe").Append(operation.Context.PipelineIdentity)
                .Append(":vp").Append(operation.Context.ViewportIdentity)
                .Append(":target=").Append(operation.Target?.Name ?? "<swapchain>");
        }

        string failureSummary = firstFailure is { } failure
            ? $"firstFailure={failure.OpType} pass={failure.PassIndex} pipe={failure.PipelineIdentity} vp={failure.ViewportIdentity} target={failure.TargetName} material={failure.MaterialName} shader={failure.ShaderName} message={failure.Message}"
            : "firstFailure=<none>";
        return
            $"ops={operations.Length} C/D/B/Comp={clearCount}/{drawCount}/{blitCount}/{computeCount}; " +
            $"writers scene={sceneSwapchainWriters} overlay={overlaySwapchainWriters} forcedDiag={forcedDiagnosticSwapchainWriters} fboOnlyD/B={fboOnlyDrawOps}/{fboOnlyBlitOps}; " +
            $"swapchain={swapchainWriterSummary}; resourceGeneration={context.ResourceGeneration} descriptorGeneration={context.DescriptorGeneration}; " +
            $"descriptorSkips={RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorBindSkips} descriptorFallbacks={RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorFallbacksCurrentFrame} descriptorFailures={RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorBindingFailuresCurrentFrame} oomFallbacks={RuntimeEngine.Rendering.Stats.Vulkan.VulkanOomFallbackCount}; " +
            $"validationCurrent={RuntimeEngine.Rendering.Stats.Vulkan.VulkanValidationMessageCountCurrentFrame}/{RuntimeEngine.Rendering.Stats.Vulkan.VulkanValidationErrorCountCurrentFrame}; " +
            $"{failureSummary}; opList=[{operationSummary}]";
    }

    private void UpdateVulkanOnScreenDiagnostic(
        string pipelineLabel,
        ColorF4 clearColor,
        int droppedDrawOps,
        int droppedOperations,
        string swapchainWriter)
    {
        string diagnostic =
            $"VK[{pipelineLabel}] clr=({clearColor.R:F2},{clearColor.G:F2},{clearColor.B:F2},{clearColor.A:F2}) sw={swapchainWriter} dropDraw={droppedDrawOps} dropOps={droppedOperations}";
        if (string.Equals(_lastOnScreenDiagnostic, diagnostic, StringComparison.Ordinal))
            return;

        _lastOnScreenDiagnostic = diagnostic;
        Debug.Vulkan("[Vulkan.Diagnostic] {0}", diagnostic);
    }

    private static void RecordVulkanFrameOpCensus(
        FrameOperationSequence operations,
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

        const int stackUniqueLimit = 256;
        if (operations.Length <= stackUniqueLimit)
        {
            Span<int> passIds = stackalloc int[stackUniqueLimit];
            Span<int> contextIds = stackalloc int[stackUniqueLimit];
            Span<int> targetIds = stackalloc int[stackUniqueLimit];
            RecordVulkanFrameOpCensusCore(
                operations,
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

        int[] passIdsArray = ArrayPool<int>.Shared.Rent(operations.Length);
        int[] contextIdsArray = ArrayPool<int>.Shared.Rent(operations.Length);
        int[] targetIdsArray = ArrayPool<int>.Shared.Rent(operations.Length);
        try
        {
            RecordVulkanFrameOpCensusCore(
                operations,
                clearCount,
                meshDrawCount,
                indirectDrawCount,
                meshTaskDispatchCount,
                blitCount,
                computeCount,
                swapchainWriteCount,
                fboWriteCount,
                passIdsArray.AsSpan(0, operations.Length),
                contextIdsArray.AsSpan(0, operations.Length),
                targetIdsArray.AsSpan(0, operations.Length));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(passIdsArray, clearArray: true);
            ArrayPool<int>.Shared.Return(contextIdsArray, clearArray: true);
            ArrayPool<int>.Shared.Return(targetIdsArray, clearArray: true);
        }
    }

    private static void RecordVulkanFrameOpCensusCore(
        FrameOperationSequence operations,
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
        for (int index = 0; index < operations.Length; index++)
        {
            FrameOp operation = operations[index];
            AddUnique(passIds, ref uniquePassCount, operation.PassIndex);
            AddUnique(contextIds, ref uniqueContextCount, operation.Context.SchedulingIdentity);
            XRFrameBuffer? target = operation is BlitOp blit ? blit.OutFbo : operation.Target;
            AddUnique(
                targetIds,
                ref uniqueTargetCount,
                target is null ? int.MinValue : RuntimeHelpers.GetHashCode(target));
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameOpCensus(
            operations.Length,
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
        for (int index = 0; index < count; index++)
            if (values[index] == value)
                return false;
        values[count++] = value;
        return true;
    }

    internal object GetLastFrameOpTraceDiagnostics(
        int limit,
        string? targetContains)
    {
        _recordingFrameOpDiagnostics.CaptureTraceSnapshot(
            out VulkanFrameOpTraceEntry[] entries,
            out ulong frameId,
            out int totalCount);
        int clampedLimit = Math.Clamp(limit, 1, 512);
        bool hasFilter = !string.IsNullOrWhiteSpace(targetContains);
        List<VulkanFrameOpTraceEntry> filtered = new(Math.Min(entries.Length, clampedLimit));
        for (int index = 0; index < entries.Length && filtered.Count < clampedLimit; index++)
        {
            VulkanFrameOpTraceEntry entry = entries[index];
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
            returnedCount = filtered.Count,
            entries = filtered,
        };
    }

    internal static FrameOp[] FilterDiagnosticSkippedFrameOps(FrameOp[] operations)
    {
        if (operations.Length == 0 || !RenderDiagnosticsFlags.VkSkipUiBatchText)
            return operations;

        int keepCount = 0;
        for (int index = 0; index < operations.Length; index++)
            if (!IsUiBatchTextDrawOp(operations[index]))
                keepCount++;
        if (keepCount == operations.Length)
            return operations;

        FrameOp[] filtered = new FrameOp[keepCount];
        int writeIndex = 0;
        for (int index = 0; index < operations.Length; index++)
            if (!IsUiBatchTextDrawOp(operations[index]))
                filtered[writeIndex++] = operations[index];
        return filtered;
    }

    private void CaptureLastFrameOpTrace(FrameOperationSequence operations)
    {
        const int maxCapturedEntries = 512;
        int count = Math.Min(operations.Length, maxCapturedEntries);
        VulkanFrameOpTraceEntry[] entries = new VulkanFrameOpTraceEntry[count];
        for (int index = 0; index < count; index++)
        {
            FrameOp operation = operations[index];
            FrameOpContext context = operation.Context;
            XRFrameBuffer? target = operation is BlitOp blit ? blit.OutFbo : operation.Target;
            entries[index] = new VulkanFrameOpTraceEntry(
                index,
                operation.GetType().Name,
                operation.PassIndex,
                ResolveFrozenPassName(operation),
                target?.Name ?? "<swapchain>",
                target is null ? 0 : RuntimeHelpers.GetHashCode(target),
                context.PipelineIdentity,
                context.PipelineInstance?.Pipeline?.GetType().Name ?? "<no pipeline>",
                context.ViewportIdentity,
                context.DisplayWidth,
                context.DisplayHeight,
                context.InternalWidth,
                context.InternalHeight,
                BuildSwapchainWriterDetail(operation));
        }

        _recordingFrameOpDiagnostics.StoreTrace(entries, VulkanFrameCounter, operations.Length);
    }

    private static string ResolveFrozenPassName(FrameOp operation)
    {
        if (operation.Context.PassMetadata is not { } metadata)
            return "<unknown>";
        foreach (var pass in metadata)
            if (pass.PassIndex == operation.PassIndex)
                return pass.Name ?? "<unnamed>";
        return "<unknown>";
    }

}
