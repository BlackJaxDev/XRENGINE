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

internal sealed partial class VulkanCommandRuntime
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
            FrameOperationStream sourceOps = packet.DynamicOverlay ? volatileOps : staticOps;
            int sourceIndex = packet.SourceStartIndex;
            bool hasSource = sourceIndex >= 0 && sourceIndex < sourceOps.Count;
            builder.AppendLine()
                .Append("  #")
                .Append(i)
                .Append(" pass=")
                .Append(packet.PassIndex)
                .Append(" passName=")
                .Append(hasSource ? TryGetPassName(in sourceOps.GetContext(sourceIndex), packet.PassIndex) ?? "<unnamed>" : "<unknown>")
                .Append(" target=")
                .Append(packet.GetDiagnosticTargetName())
                .Append(" view=")
                .Append(packet.ViewKey.Kind)
                .Append(" op=")
                .Append(hasSource ? DescribeCommandChainTraceOp(sourceOps, sourceIndex) : "<unknown>")
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

    private static string DescribeCommandChainTraceRow(int packetIndex, RenderPacket packet, CommandChain chain, FrameOperationStream sourceOps)
    {
        string dirtyDetails = chain.State == CommandChainState.Recorded && chain.DirtyReason != CommandChainDirtyReason.VolatileCommand
            ? " " + DescribeCommandChainDirtyReason(chain, packet) : string.Empty;
        int index = packet.SourceStartIndex;
        bool hasSource = index >= 0 && index < sourceOps.Count;
        string passName = hasSource ? TryGetPassName(in sourceOps.GetContext(index), packet.PassIndex) ?? "<unnamed>" : "<unknown>";
        string opDescription = hasSource ? DescribeCommandChainTraceOp(sourceOps, index) : "<unknown>";
        return $"#{packetIndex} state={chain.State} reason={chain.DirtyReason} pass={packet.PassIndex} passName={passName} target={packet.GetDiagnosticTargetName()} view={packet.ViewKey.Kind}:{packet.ViewKey.ViewIndex} op={opDescription} draws={packet.DrawCount} dispatches={packet.DispatchCount} volatility={packet.Volatility}{dirtyDetails}";
    }

    private static string DescribeCommandChainTraceOp(FrameOperationStream ops, int index)
    {
        ref readonly FrameOperationHeader header = ref ops.GetHeader(index);
        return header.OpCode switch
        {
            EVulkanPrimaryPlanNodeKind.MeshDraw => $"MeshDraw[{ops.GetMeshDraw(index).Draw.Renderer.MeshRenderer.Name ?? "<unnamed renderer>"}]",
            EVulkanPrimaryPlanNodeKind.ComputeDispatch => $"ComputeDispatch[{ops.GetComputeDispatch(index).Program.Data?.Name ?? "<unnamed program>"}]",
            EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect => $"ComputeDispatchIndirect[offset={ops.GetComputeDispatchIndirect(index).ArgumentOffset}]",
            EVulkanPrimaryPlanNodeKind.BufferCopy => $"BufferCopy[{ops.GetBufferCopy(index).ByteCount} bytes]",
            EVulkanPrimaryPlanNodeKind.MemoryBarrier => $"MemoryBarrier[{ops.GetMemoryBarrier(index).Mask}]",
            _ => header.OpCode.ToString(),
        };
    }

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

