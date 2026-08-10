using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    private int CountDistinctViewKeys(List<RenderPacket> packets)
    {
        HashSet<RenderViewKey> keys = _commandChainViewKeyScratch;
        keys.Clear();
        for (int i = 0; i < packets.Count; i++)
            keys.Add(packets[i].ViewKey);
        return keys.Count;
    }

    private void TraceCommandChainSchedule(
        CommandChainSchedule schedule,
        List<RenderPacket> packets,
        FrameOperationStream staticOps,
        FrameOperationStream volatileOps,
        List<string>? commandChainTraceRows)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _commandChainTraceLastDumpTimestamp);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromSeconds(1))
            return;

        Interlocked.Exchange(ref _commandChainTraceLastDumpTimestamp, now);
        int dumpIndex = Interlocked.Increment(ref _commandChainTraceDumped);
        if (dumpIndex > 12)
            return;

        StringBuilder builder = new(1024);
        builder.Append("[Vulkan.CommandChains] dump=")
            .Append(dumpIndex)
            .Append(" schedule=0x")
            .Append(schedule.StructuralSignature.ToString("X16"))
            .Append(" groups=")
            .Append(schedule.Groups.Length)
            .Append(" packets=")
            .Append(packets.Count)
            .Append(" staticOps=")
            .Append(staticOps.Count)
            .Append(" volatileOps=")
            .Append(volatileOps.Count)
            .Append(" dirtyRows=")
            .Append(commandChainTraceRows?.Count ?? 0);

        if (commandChainTraceRows is { Count: > 0 })
        {
            int dirtyRowLimit = Math.Min(commandChainTraceRows.Count, 96);
            for (int i = 0; i < dirtyRowLimit; i++)
            {
                builder.AppendLine()
                    .Append("  dirty ")
                    .Append(commandChainTraceRows[i]);
            }

            if (dirtyRowLimit < commandChainTraceRows.Count)
            {
                builder.AppendLine()
                    .Append("  ... ")
                    .Append(commandChainTraceRows.Count - dirtyRowLimit)
                    .Append(" more dirty rows omitted");
            }
        }

        int packetLimit = dumpIndex == 1 ? packets.Count : Math.Min(packets.Count, 32);
        for (int i = 0; i < packetLimit; i++)
        {
            RenderPacket packet = packets[i];
            FrameOp? sourceOp = ResolveCommandChainTraceSourceOp(packet, staticOps, volatileOps);
            builder.AppendLine()
                .Append("  #")
                .Append(i)
                .Append(" pass=")
                .Append(packet.PassIndex)
                .Append(" passName=")
                .Append(sourceOp is null ? "<unknown>" : TryGetPassName(sourceOp) ?? "<unnamed>")
                .Append(" target=")
                .Append(packet.GetDiagnosticTargetName())
                .Append(" view=")
                .Append(packet.ViewKey.Kind)
                .Append(" op=")
                .Append(sourceOp is null ? "<unknown>" : DescribeCommandChainTraceOp(sourceOp))
                .Append(" draws=")
                .Append(packet.DrawCount)
                .Append(" dispatches=")
                .Append(packet.DispatchCount)
                .Append(" volatility=")
                .Append(packet.Volatility)
                .Append(" structural=0x")
                .Append(packet.StructuralSignature.ToString("X16"))
                .Append(" frame=0x")
                .Append(packet.FrameDataSignature.ToString("X16"));
        }

        if (packetLimit < packets.Count)
        {
            builder.AppendLine()
                .Append("  ... ")
                .Append(packets.Count - packetLimit)
                .Append(" more packets omitted");
        }

        Debug.Vulkan(builder.ToString());
    }

    private static string DescribeCommandChainTraceRow(int packetIndex, RenderPacket packet, CommandChain chain, FrameOp? sourceOp)
    {
        string dirtyDetails = chain.State == CommandChainState.Recorded && chain.DirtyReason != CommandChainDirtyReason.VolatileCommand
            ? " " + DescribeCommandChainDirtyReason(chain, packet)
            : string.Empty;
        string passName = sourceOp is null ? "<unknown>" : TryGetPassName(sourceOp) ?? "<unnamed>";
        string opDescription = sourceOp is null ? "<unknown>" : DescribeCommandChainTraceOp(sourceOp);

        return $"#{packetIndex} state={chain.State} reason={chain.DirtyReason} pass={packet.PassIndex} passName={passName} target={packet.GetDiagnosticTargetName()} view={packet.ViewKey.Kind}:{packet.ViewKey.ViewIndex} op={opDescription} draws={packet.DrawCount} dispatches={packet.DispatchCount} volatility={packet.Volatility}{dirtyDetails}";
    }

    private static FrameOp? ResolveCommandChainTraceSourceOp(RenderPacket packet, FrameOperationStream staticOps, FrameOperationStream volatileOps)
    {
        FrameOperationStream sourceOps = packet.DynamicOverlay ? volatileOps : staticOps;
        int index = packet.SourceStartIndex;
        return index >= 0 && index < sourceOps.Count
            ? sourceOps.GetPayloadForPrimaryDispatch(index)
            : null;
    }

    private static string DescribeCommandChainTraceOp(FrameOp op)
        => op switch
        {
            MeshDrawOp draw => $"MeshDraw[{draw.Draw.Renderer?.MeshRenderer?.Name ?? "<unnamed renderer>"}]",
            ComputeDispatchOp compute => $"ComputeDispatch[{compute.Program?.Data?.Name ?? "<unnamed program>"} {compute.GroupsX}x{compute.GroupsY}x{compute.GroupsZ}]",
            ComputeDispatchIndirectOp computeIndirect => $"ComputeDispatchIndirect[{computeIndirect.Program?.Data?.Name ?? "<unnamed program>"} offset={computeIndirect.ArgumentOffset}]",
            BufferCopyOp copy => $"BufferCopy[{copy.ByteCount} bytes]",
            SubmissionMarkerOp marker => $"SubmissionMarker[{marker.Label}]",
            IndirectDrawOp indirect => $"IndirectDraw[count={indirect.DrawCount}]",
            MeshTaskDispatchIndirectCountOp meshTask => $"MeshTaskDispatch[max={meshTask.MaxDrawCount}]",
            BlitOp => "Blit",
            ClearOp => "Clear",
            MemoryBarrierOp barrier => $"MemoryBarrier[{barrier.Mask}]",
            PublishFramebufferForSamplingOp publish => $"PublishFramebufferForSampling[{publish.FrameBuffer?.Name ?? "<unnamed>"}]",
            TransformFeedbackOp => "TransformFeedback",
            DlssUpscaleOp => "DlssUpscale",
            DlssFrameGenerationOp => "DlssFrameGeneration",
            TextureUploadFrameOp => "TextureUpload",
            _ => op.GetType().Name,
        };

    private static void ValidateCommandChainSchedule(CommandChainSchedule schedule, List<RenderPacket> packets, ulong frameOpsSignature)
    {
        if (packets.Count > 0 && schedule.StructuralSignature == 0)
            throw new InvalidOperationException("Command-chain schedule produced a zero structural signature for a non-empty frame.");

        if (frameOpsSignature == 0 && packets.Count > 0)
            throw new InvalidOperationException("Command-chain lowering saw packets but the source frame-op signature was zero.");
    }

    internal static void ValidateCommandChainViewSpecialization(CommandChainSchedule schedule)
    {
        bool sawStereoMultiview = false;
        bool sawSeparateStereoEye = false;
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            ReadOnlySpan<CommandChainKey> keys = groups[groupIndex].ChainKeys.Span;
            int lastEyeIndex = int.MinValue;
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                RenderViewKey viewKey = keys[keyIndex].ViewKey;
                if (viewKey.Kind == RenderViewKind.VREye)
                {
                    if (viewKey.ViewIndex != CommandChainLeftEyeViewIndex &&
                        viewKey.ViewIndex != CommandChainRightEyeViewIndex &&
                        viewKey.ViewIndex != CommandChainStereoMultiviewViewIndex)
                    {
                        throw new InvalidOperationException(
                            $"Command-chain VR eye key has invalid view index {viewKey.ViewIndex}.");
                    }

                    if (viewKey.ViewIndex == CommandChainStereoMultiviewViewIndex)
                    {
                        sawStereoMultiview = true;
                        if (sawSeparateStereoEye)
                            throw new InvalidOperationException("Command-chain schedule mixes separate VR eye chains with multiview stereo chains.");
                    }
                    else
                    {
                        sawSeparateStereoEye = true;
                        if (sawStereoMultiview)
                            throw new InvalidOperationException("Command-chain schedule mixes multiview stereo chains with separate VR eye chains.");
                        if (lastEyeIndex > viewKey.ViewIndex)
                            throw new InvalidOperationException("Command-chain VR eye chains must be ordered left eye before right eye.");

                        lastEyeIndex = viewKey.ViewIndex;
                    }
                }
                else if (viewKey.Kind == RenderViewKind.Shadow)
                {
                    if (viewKey.LightIdentity == 0)
                        throw new InvalidOperationException("Command-chain shadow key is missing a light identity.");
                    if (viewKey.CascadeIndex < 0)
                        throw new InvalidOperationException("Command-chain shadow key is missing a cascade or face identity.");
                }
            }
        }
    }
}

