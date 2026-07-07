using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    #region Frame Operation Queue

    private readonly Lock _frameOpsLock = new();
    private readonly List<FrameOp> _frameOps = [];
    private FrameOp[] _drainedFrameOpsBuffer = Array.Empty<FrameOp>();
    [ThreadStatic]
    private static FrameOpCapture? t_frameOpCapture;
    [ThreadStatic]
    private static FrameOpCapture? t_frameOpCaptureScratch;
    [ThreadStatic]
    private static Dictionary<int, FrameOp[]>? t_frameOpCaptureBuffersByCount;
    private const int FrameOpKindUnknown = 0;
    private const int FrameOpKindClear = 1;
    private const int FrameOpKindMeshDraw = 2;
    private const int FrameOpKindBlit = 3;
    private const int FrameOpKindIndirectDraw = 4;
    private const int FrameOpKindMeshTaskDispatchIndirectCount = 5;
    private const int FrameOpKindMemoryBarrier = 6;
    private const int FrameOpKindDlssUpscale = 7;
    private const int FrameOpKindDlssFrameGeneration = 8;
    private const int FrameOpKindTransformFeedback = 9;
    private const int FrameOpKindComputeDispatch = 10;
    private const int FrameOpKindTextureUpload = 11;
    private const int FrameOpKindQuery = 12;

    internal abstract record FrameOp(int PassIndex, XRFrameBuffer? Target, FrameOpContext Context);

    internal sealed record ClearOp(
        int PassIndex,
        XRFrameBuffer? Target,
        bool ClearColor,
        bool ClearDepth,
        bool ClearStencil,
        ColorF4 Color,
        float Depth,
        uint Stencil,
        Rect2D Rect,
        FrameOpContext Context) : FrameOp(PassIndex, Target, Context);

    internal readonly record struct VulkanBindlessMaterialDescriptorBinding(
        VkRenderProgram Program,
        string Consumer);

    internal sealed record MeshDrawOp(int PassIndex, XRFrameBuffer? Target, PendingMeshDraw Draw, FrameOpContext Context) : FrameOp(PassIndex, Target, Context)
    {
        /// <summary>
        /// True when this draw was enqueued inside an occlusion QueryOp Begin/End bracket
        /// (CPU occlusion proxy AABB draws). Such draws must keep their enqueue position
        /// relative to the surrounding QueryOps: canonical opaque-draw reordering would
        /// make the frame-op sort comparer intransitive and scramble Begin/End pairing
        /// (observed as VUID-vkCmdBeginQuery-queryPool-01922 and
        /// VUID-vkEndCommandBuffer-commandBuffer-00061).
        /// </summary>
        internal bool PreserveSubmissionOrder { get; init; }
    }

    internal enum EVulkanQueryFrameOpKind
    {
        Begin,
        End,
    }

    internal sealed record QueryOp(
        int PassIndex,
        XRFrameBuffer? Target,
        VkRenderQuery Query,
        EQueryTarget QueryTarget,
        EVulkanQueryFrameOpKind Operation,
        FrameOpContext Context) : FrameOp(PassIndex, Target, Context);

    internal readonly record struct VulkanFrameDrawStats(int DrawCalls, int MultiDrawCalls, int TrianglesRendered);

    internal sealed record BlitOp(
        int PassIndex,
        XRFrameBuffer? InFbo,
        XRFrameBuffer? OutFbo,
        int InX,
        int InY,
        uint InW,
        uint InH,
        int OutX,
        int OutY,
        uint OutW,
        uint OutH,
        EReadBufferMode ReadBufferMode,
        bool ColorBit,
        bool DepthBit,
        bool StencilBit,
        bool LinearFilter,
        FrameOpContext Context) : FrameOp(PassIndex, OutFbo, Context);

    internal sealed record IndirectDrawOp(
        int PassIndex,
        XRFrameBuffer? Target,
        VkDataBuffer IndirectBuffer,
        VkDataBuffer? ParameterBuffer,
        VkMeshRenderer MeshRenderer,
        PendingMeshDraw Draw,
        uint DrawCount,
        uint Stride,
        nuint ByteOffset,
        nuint CountByteOffset,
        bool UseCount,
        VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures,
        FrameOpContext Context) : FrameOp(PassIndex, Target, Context);

    internal sealed record MeshTaskDispatchIndirectCountOp(
        int PassIndex,
        VkDataBuffer IndirectBuffer,
        VkDataBuffer CountBuffer,
        uint MaxDrawCount,
        uint Stride,
        nuint ByteOffset,
        nuint CountByteOffset,
        VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);

    internal sealed record MemoryBarrierOp(
        int PassIndex,
        EMemoryBarrierMask Mask,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);

    internal sealed record DlssUpscaleOp(
        int PassIndex,
        NvidiaDlssManager.Native.NativeVulkanSession Session,
        VulkanStreamlineImage SourceColor,
        VulkanStreamlineImage Depth,
        VulkanStreamlineImage Motion,
        VulkanStreamlineImage OutputColor,
        VulkanStreamlineImage? Exposure,
        VulkanUpscaleBridgeDispatchParameters Parameters,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);

    internal sealed record DlssFrameGenerationOp(
        int PassIndex,
        NvidiaDlssManager.Native.NativeFrameGenerationSession Session,
        VulkanStreamlineImage Depth,
        VulkanStreamlineImage Motion,
        VulkanStreamlineImage HudlessColor,
        VulkanUpscaleBridgeDispatchParameters Parameters,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);

    internal sealed record TransformFeedbackOp(
        int PassIndex,
        XRFrameBuffer? Target,
        VkTransformFeedback TransformFeedback,
        EXRTransformFeedbackOperation Operation,
        XRDataBuffer? CounterBuffer,
        ulong FeedbackBufferOffset,
        ulong? FeedbackBufferSize,
        ulong CounterBufferOffset,
        uint CounterOffset,
        uint VertexStride,
        uint InstanceCount,
        uint FirstInstance,
        FrameOpContext Context) : FrameOp(PassIndex, Target, Context);

    internal readonly record struct ProgramUniformValue(EShaderVarType Type, object Value, bool IsArray);

    internal readonly record struct ProgramImageBinding(
        XRTexture Texture,
        int Level,
        bool Layered,
        int Layer,
        XRRenderProgram.EImageAccess Access,
        XRRenderProgram.EImageFormat Format);

    internal sealed record ComputeDispatchSnapshot(
        Dictionary<string, ProgramUniformValue> Uniforms,
        Dictionary<uint, XRTexture> Samplers,
        Dictionary<uint, string> SamplerNamesByUnit,
        Dictionary<string, XRTexture> SamplersByName,
        Dictionary<uint, ProgramImageBinding> Images,
        Dictionary<uint, XRDataBuffer> Buffers);

    private const ulong FrameSourceMutableDescriptorSignature = 0x4652534D55544453UL;

    private static bool IsFrameSourceSamplerName(string? name)
        => string.Equals(name, "SourceTexture", StringComparison.Ordinal) ||
            string.Equals(name, "SourceTex", StringComparison.Ordinal);

    private static bool IsMutableFrameSourceSamplerName(string? name, XRRenderPipelineInstance? pipeline)
    {
        if (IsFrameSourceSamplerName(name))
            return true;

        return !string.IsNullOrWhiteSpace(name) &&
            pipeline is not null &&
            pipeline.TryGetTexture(name, out XRTexture? texture) &&
            texture is not null;
    }

    internal sealed record ComputeDispatchOp(
        int PassIndex,
        VkRenderProgram Program,
        uint GroupsX,
        uint GroupsY,
        uint GroupsZ,
        ComputeDispatchSnapshot Snapshot,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);

    internal sealed record TextureUploadFrameOp(
        VulkanImportedTexturePendingUpload Upload,
        FrameOpContext Context) : FrameOp(int.MinValue, null, Context);

    internal void EnqueueFrameOp(FrameOp op)
    {
        FrameOp validatedOp = EnsureValidFrameOpPassIndex(op);
        PublishFrameOpDrawStats(validatedOp);

        FrameOpCapture? capture = t_frameOpCapture;
        if (capture is not null)
        {
            if (capture.ExcludeTextureUploads && validatedOp is TextureUploadFrameOp)
            {
                using (_frameOpsLock.EnterScope())
                    _frameOps.Add(validatedOp);
            }
            else
            {
                capture.Add(validatedOp);
            }

            return;
        }

        using (_frameOpsLock.EnterScope())
            _frameOps.Add(validatedOp);
    }

    internal bool EnqueueOcclusionQueryBegin(XRRenderQuery query, EQueryTarget target)
        => EnqueueOcclusionQueryOp(query, target, EVulkanQueryFrameOpKind.Begin);

    internal bool EnqueueOcclusionQueryEnd(XRRenderQuery query)
        => EnqueueOcclusionQueryOp(query, EQueryTarget.AnySamplesPassedConservative, EVulkanQueryFrameOpKind.End);

    // Tracks whether the calling thread is currently between an occlusion QueryOp
    // Begin and End enqueue. Mesh draws enqueued inside the bracket (proxy AABB
    // draws) are marked PreserveSubmissionOrder so the frame-op sort cannot move
    // them (or reorder other draws across the bracket).
    [ThreadStatic]
    private static int t_occlusionQueryBracketDepth;

    internal static bool IsInOcclusionQueryBracket => t_occlusionQueryBracketDepth > 0;

    private bool EnqueueOcclusionQueryOp(
        XRRenderQuery query,
        EQueryTarget target,
        EVulkanQueryFrameOpKind operation)
    {
        if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline is null)
            return false;

        VkRenderQuery? vkQuery = GenericToAPI<VkRenderQuery>(query);
        if (vkQuery is null)
            return false;

        FrameOpContext context = CaptureFrameOpContext();
        int passIndex = EnsureValidPassIndex(
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
            "Query",
            context.PassMetadata);

        EnqueueFrameOp(new QueryOp(
            passIndex,
            ResolveCurrentFrameOpDrawTarget(),
            vkQuery,
            target,
            operation,
            context));

        if (operation == EVulkanQueryFrameOpKind.Begin)
            t_occlusionQueryBracketDepth++;
        else if (t_occlusionQueryBracketDepth > 0)
            t_occlusionQueryBracketDepth--;

        return true;
    }

    internal FrameOp[] CaptureFrameOpsExcludingTextureUploads(Action emitFrameOps, out ulong signature)
        => CaptureFrameOps(emitFrameOps, excludeTextureUploads: true, out signature);

    private FrameOp[] CaptureFrameOps(Action emitFrameOps, bool excludeTextureUploads, out ulong signature)
    {
        FrameOpCapture? previous = t_frameOpCapture;
        FrameOpCapture capture = RentFrameOpCapture(previous, excludeTextureUploads);
        t_frameOpCapture = capture;
        try
        {
            emitFrameOps();
        }
        finally
        {
            t_frameOpCapture = previous;
        }

        int opCount = capture.Count;
        if (opCount == 0)
        {
            signature = 0;
            return Array.Empty<FrameOp>();
        }

        FrameOp[] ops = GetThreadFrameOpCaptureBuffer(opCount);
        Array.Copy(capture.Buffer, ops, opCount);
        signature = ComputeFrameOpsSignature(ops);
        return ops;
    }

    private static FrameOpCapture RentFrameOpCapture(FrameOpCapture? previous, bool excludeTextureUploads)
    {
        FrameOpCapture capture;
        if (previous is null)
        {
            capture = t_frameOpCaptureScratch ??= new FrameOpCapture();
        }
        else
        {
            // Nested capture scopes are not expected in steady-state recording; keep them correct
            // without complicating the common single-scope hot path.
            capture = new FrameOpCapture();
        }

        capture.Begin(previous, excludeTextureUploads);
        return capture;
    }

    private static FrameOp[] GetThreadFrameOpCaptureBuffer(int opCount)
    {
        Dictionary<int, FrameOp[]> buffersByCount = t_frameOpCaptureBuffersByCount ??= [];
        if (!buffersByCount.TryGetValue(opCount, out FrameOp[]? buffer))
        {
            buffer = new FrameOp[opCount];
            buffersByCount.Add(opCount, buffer);
        }

        return buffer;
    }

    private sealed class FrameOpCapture
    {
        public FrameOpCapture? Previous { get; private set; }
        public bool ExcludeTextureUploads { get; private set; }
        public FrameOp[] Buffer { get; private set; } = new FrameOp[256];
        public int Count { get; private set; }

        public void Begin(FrameOpCapture? previous, bool excludeTextureUploads)
        {
            Previous = previous;
            ExcludeTextureUploads = excludeTextureUploads;
            Count = 0;
        }

        public void Add(FrameOp op)
        {
            int count = Count;
            if ((uint)count >= (uint)Buffer.Length)
                Grow();

            Buffer[count] = op;
            Count = count + 1;
        }

        private void Grow()
        {
            int newLength = Buffer.Length == 0 ? 256 : Buffer.Length * 2;
            FrameOp[] grown = new FrameOp[newLength];
            Array.Copy(Buffer, grown, Count);
            Buffer = grown;
        }
    }

    private static void PublishFrameOpDrawStats(FrameOp op)
    {
        if (op.PassIndex == int.MinValue)
            return;

        switch (op)
        {
            case MeshDrawOp meshDraw:
                PublishFrameDrawStats(meshDraw.Draw.Renderer.EstimateFrameDrawStats(meshDraw.Draw));
                break;
            case IndirectDrawOp indirectDraw:
                PublishFrameDrawStats(new VulkanFrameDrawStats(
                    SaturateToInt(indirectDraw.DrawCount),
                    MultiDrawCalls: indirectDraw.DrawCount > 0u ? 1 : 0,
                    TrianglesRendered: 0));
                break;
            case MeshTaskDispatchIndirectCountOp meshTaskDispatch:
                PublishFrameDrawStats(new VulkanFrameDrawStats(
                    SaturateToInt(meshTaskDispatch.MaxDrawCount),
                    MultiDrawCalls: meshTaskDispatch.MaxDrawCount > 0u ? 1 : 0,
                    TrianglesRendered: 0));
                break;
        }
    }

    private static void PublishFrameDrawStats(VulkanFrameDrawStats stats)
    {
        if (stats.DrawCalls > 0)
            RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(stats.DrawCalls);
        if (stats.MultiDrawCalls > 0)
            RuntimeEngine.Rendering.Stats.Frame.IncrementMultiDrawCalls(stats.MultiDrawCalls);
        if (stats.TrianglesRendered > 0)
            RuntimeEngine.Rendering.Stats.Frame.AddTrianglesRendered(stats.TrianglesRendered);
    }

    private static int SaturateToInt(uint value)
        => value > int.MaxValue ? int.MaxValue : (int)value;

    private static int SaturateToInt(ulong value)
        => value > int.MaxValue ? int.MaxValue : (int)value;

    private FrameOp EnsureValidFrameOpPassIndex(FrameOp op)
    {
        if (op is TextureUploadFrameOp)
            return op;

        int validatedPassIndex = EnsureValidPassIndex(op.PassIndex, op.GetType().Name, op.Context.PassMetadata);
        if (validatedPassIndex == op.PassIndex)
            return op;

        return op switch
        {
            ClearOp clear => clear with { PassIndex = validatedPassIndex },
            MeshDrawOp meshDraw => meshDraw with { PassIndex = validatedPassIndex },
            QueryOp query => query with { PassIndex = validatedPassIndex },
            BlitOp blit => blit with { PassIndex = validatedPassIndex },
            IndirectDrawOp indirectDraw => indirectDraw with { PassIndex = validatedPassIndex },
            MeshTaskDispatchIndirectCountOp meshTaskDispatch => meshTaskDispatch with { PassIndex = validatedPassIndex },
            MemoryBarrierOp memoryBarrier => memoryBarrier with { PassIndex = validatedPassIndex },
            DlssUpscaleOp dlssUpscale => dlssUpscale with { PassIndex = validatedPassIndex },
            DlssFrameGenerationOp dlssFrameGeneration => dlssFrameGeneration with { PassIndex = validatedPassIndex },
            TransformFeedbackOp transformFeedback => transformFeedback with { PassIndex = validatedPassIndex },
            ComputeDispatchOp computeDispatch => computeDispatch with { PassIndex = validatedPassIndex },
            _ => op
        };
    }

    internal FrameOp[] DrainFrameOps() => DrainFrameOps(out _);

    internal FrameOp[] DrainFrameOps(out ulong signature)
        => DrainFrameOps(out signature, computeSignature: true);

    internal FrameOp[] DrainFrameOps(out ulong signature, bool computeSignature)
    {
        using (_frameOpsLock.EnterScope())
        {
            if (_frameOps.Count == 0)
            {
                signature = 0;
                return Array.Empty<FrameOp>();
            }

            int opCount = _frameOps.Count;
            if (_drainedFrameOpsBuffer.Length != opCount)
                _drainedFrameOpsBuffer = new FrameOp[opCount];

            _frameOps.CopyTo(_drainedFrameOpsBuffer);
            _frameOps.Clear();
            signature = computeSignature ? ComputeFrameOpsSignature(_drainedFrameOpsBuffer) : 0;
            return _drainedFrameOpsBuffer;
        }
    }

    internal FrameOp[] DrainFrameOpsExcludingTextureUploads(out ulong signature)
    {
        using (_frameOpsLock.EnterScope())
        {
            if (_frameOps.Count == 0)
            {
                signature = 0;
                return Array.Empty<FrameOp>();
            }

            int opCount = _frameOps.Count;
            int uploadCount = 0;
            for (int i = 0; i < opCount; i++)
            {
                if (_frameOps[i] is TextureUploadFrameOp)
                    uploadCount++;
            }

            if (uploadCount == 0)
            {
                if (_drainedFrameOpsBuffer.Length != opCount)
                    _drainedFrameOpsBuffer = new FrameOp[opCount];

                _frameOps.CopyTo(_drainedFrameOpsBuffer);
                _frameOps.Clear();
                signature = ComputeFrameOpsSignature(_drainedFrameOpsBuffer);
                return _drainedFrameOpsBuffer;
            }

            int drainedCount = opCount - uploadCount;
            if (drainedCount == 0)
            {
                signature = 0;
                return Array.Empty<FrameOp>();
            }

            if (_drainedFrameOpsBuffer.Length != drainedCount)
                _drainedFrameOpsBuffer = new FrameOp[drainedCount];

            int drainedIndex = 0;
            int retainedIndex = 0;
            for (int i = 0; i < opCount; i++)
            {
                FrameOp op = _frameOps[i];
                if (op is TextureUploadFrameOp)
                {
                    _frameOps[retainedIndex++] = op;
                }
                else
                {
                    _drainedFrameOpsBuffer[drainedIndex++] = op;
                }
            }

            if (retainedIndex < _frameOps.Count)
                _frameOps.RemoveRange(retainedIndex, _frameOps.Count - retainedIndex);

            signature = ComputeFrameOpsSignature(_drainedFrameOpsBuffer);
            return _drainedFrameOpsBuffer;
        }
    }

    private static ulong ComputeFrameOpsSignature(FrameOp[] ops)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(ops.Length);

        for (int i = 0; i < ops.Length; i++)
        {
            FrameOp op = ops[i];
            hash.Add(GetFrameOpKindId(op));
            hash.Add(op.PassIndex);
            hash.Add(op.Target?.GetHashCode() ?? 0);
            hash.Add(op.Context.PipelineIdentity);
            hash.Add(op.Context.ViewportIdentity);

            switch (op)
            {
                case ClearOp clear:
                    hash.Add(clear.ClearColor);
                    hash.Add(clear.ClearDepth);
                    hash.Add(clear.ClearStencil);
                    hash.Add(clear.Color.R);
                    hash.Add(clear.Color.G);
                    hash.Add(clear.Color.B);
                    hash.Add(clear.Color.A);
                    hash.Add(clear.Depth);
                    hash.Add(clear.Stencil);
                    hash.Add(clear.Rect.Offset.X);
                    hash.Add(clear.Rect.Offset.Y);
                    hash.Add(clear.Rect.Extent.Width);
                    hash.Add(clear.Rect.Extent.Height);
                    break;
                case MeshDrawOp meshDraw:
                    hash.Add(meshDraw.Draw.Renderer?.GetHashCode() ?? 0);
                    hash.Add(meshDraw.Draw.Viewport.X);
                    hash.Add(meshDraw.Draw.Viewport.Y);
                    hash.Add(meshDraw.Draw.Viewport.Width);
                    hash.Add(meshDraw.Draw.Viewport.Height);
                    hash.Add(meshDraw.Draw.Scissor.Offset.X);
                    hash.Add(meshDraw.Draw.Scissor.Offset.Y);
                    hash.Add(meshDraw.Draw.Scissor.Extent.Width);
                    hash.Add(meshDraw.Draw.Scissor.Extent.Height);
                    hash.Add(meshDraw.Draw.ViewportScissorCount);
                    if (meshDraw.Draw.ViewportScissorCount > 1 &&
                        meshDraw.Draw.IndexedViewports is { } indexedViewports &&
                        meshDraw.Draw.IndexedScissors is { } indexedScissors)
                    {
                        int indexedCount = (int)Math.Min(
                            meshDraw.Draw.ViewportScissorCount,
                            (uint)Math.Min(indexedViewports.Length, indexedScissors.Length));
                        for (int indexedIndex = 0; indexedIndex < indexedCount; indexedIndex++)
                        {
                            Viewport indexedViewport = indexedViewports[indexedIndex];
                            Rect2D indexedScissor = indexedScissors[indexedIndex];
                            hash.Add(indexedViewport.X);
                            hash.Add(indexedViewport.Y);
                            hash.Add(indexedViewport.Width);
                            hash.Add(indexedViewport.Height);
                            hash.Add(indexedViewport.MinDepth);
                            hash.Add(indexedViewport.MaxDepth);
                            hash.Add(indexedScissor.Offset.X);
                            hash.Add(indexedScissor.Offset.Y);
                            hash.Add(indexedScissor.Extent.Width);
                            hash.Add(indexedScissor.Extent.Height);
                        }
                    }
                    hash.Add(meshDraw.Draw.DepthTestEnabled);
                    hash.Add(meshDraw.Draw.DepthWriteEnabled);
                    hash.Add((int)meshDraw.Draw.DepthCompareOp);
                    hash.Add(meshDraw.Draw.StencilTestEnabled);
                    hash.Add(meshDraw.Draw.StencilWriteMask);
                    hash.Add((int)meshDraw.Draw.ColorWriteMask);
                    hash.Add((int)meshDraw.Draw.CullMode);
                    hash.Add((int)meshDraw.Draw.FrontFace);
                    hash.Add(meshDraw.Draw.BlendEnabled);
                    hash.Add((int)meshDraw.Draw.ColorBlendOp);
                    hash.Add((int)meshDraw.Draw.AlphaBlendOp);
                    hash.Add((int)meshDraw.Draw.SrcColorBlendFactor);
                    hash.Add((int)meshDraw.Draw.DstColorBlendFactor);
                    hash.Add((int)meshDraw.Draw.SrcAlphaBlendFactor);
                    hash.Add((int)meshDraw.Draw.DstAlphaBlendFactor);
                    hash.Add(meshDraw.Draw.MaterialOverride?.GetHashCode() ?? 0);
                    hash.Add(meshDraw.Draw.Instances);
                    hash.Add((int)meshDraw.Draw.BillboardMode);
                    hash.Add(meshDraw.Draw.IsStereoPass);
                    hash.Add(meshDraw.Draw.UseUnjitteredProjection);
                    hash.Add(meshDraw.Draw.PreparedProgramIdentity);
                    HashProgramBindingSnapshot(ref hash, meshDraw.Draw.ProgramBindingSnapshot, meshDraw.Context.PipelineInstance);
                    break;
                case QueryOp query:
                    hash.Add(query.Query.GetHashCode());
                    hash.Add((int)query.QueryTarget);
                    hash.Add((int)query.Operation);
                    break;
                case BlitOp blit:
                    hash.Add(blit.InFbo?.GetHashCode() ?? 0);
                    hash.Add(blit.OutFbo?.GetHashCode() ?? 0);
                    hash.Add(blit.InX);
                    hash.Add(blit.InY);
                    hash.Add(blit.InW);
                    hash.Add(blit.InH);
                    hash.Add(blit.OutX);
                    hash.Add(blit.OutY);
                    hash.Add(blit.OutW);
                    hash.Add(blit.OutH);
                    hash.Add((int)blit.ReadBufferMode);
                    hash.Add(blit.ColorBit);
                    hash.Add(blit.DepthBit);
                    hash.Add(blit.StencilBit);
                    hash.Add(blit.LinearFilter);
                    break;
                case IndirectDrawOp indirect:
                    hash.Add(ComputeCommandBufferDataBufferSignature(indirect.IndirectBuffer));
                    hash.Add(ComputeCommandBufferDataBufferSignature(indirect.ParameterBuffer));
                    hash.Add(indirect.DrawCount);
                    hash.Add(indirect.Stride);
                    hash.Add(indirect.ByteOffset);
                    hash.Add(indirect.CountByteOffset);
                    hash.Add(indirect.UseCount);
                    break;
                case MeshTaskDispatchIndirectCountOp meshTaskDispatch:
                    hash.Add(ComputeCommandBufferDataBufferSignature(meshTaskDispatch.IndirectBuffer));
                    hash.Add(ComputeCommandBufferDataBufferSignature(meshTaskDispatch.CountBuffer));
                    hash.Add(meshTaskDispatch.MaxDrawCount);
                    hash.Add(meshTaskDispatch.Stride);
                    hash.Add(meshTaskDispatch.ByteOffset);
                    hash.Add(meshTaskDispatch.CountByteOffset);
                    break;
                case MemoryBarrierOp barrier:
                    hash.Add((int)barrier.Mask);
                    break;
                case DlssUpscaleOp dlss:
                    hash.Add(dlss.Session.GetHashCode());
                    hash.Add(dlss.SourceColor.Image.Handle);
                    hash.Add(dlss.Depth.Image.Handle);
                    hash.Add(dlss.Motion.Image.Handle);
                    hash.Add(dlss.OutputColor.Image.Handle);
                    hash.Add(dlss.Exposure?.Image.Handle ?? 0UL);
                    hash.Add(dlss.Parameters.InputWidth);
                    hash.Add(dlss.Parameters.InputHeight);
                    hash.Add(dlss.Parameters.OutputWidth);
                    hash.Add(dlss.Parameters.OutputHeight);
                    hash.Add(dlss.Parameters.FrameIndex);
                    hash.Add(dlss.Parameters.ResetHistory);
                    hash.Add(dlss.Parameters.OutputHdr);
                    hash.Add((int)dlss.Parameters.DlssQuality);
                    break;
                case DlssFrameGenerationOp dlssFrameGeneration:
                    hash.Add(dlssFrameGeneration.Session.GetHashCode());
                    hash.Add(dlssFrameGeneration.Depth.Image.Handle);
                    hash.Add(dlssFrameGeneration.Motion.Image.Handle);
                    hash.Add(dlssFrameGeneration.HudlessColor.Image.Handle);
                    hash.Add(dlssFrameGeneration.Parameters.InputWidth);
                    hash.Add(dlssFrameGeneration.Parameters.InputHeight);
                    hash.Add(dlssFrameGeneration.Parameters.OutputWidth);
                    hash.Add(dlssFrameGeneration.Parameters.OutputHeight);
                    hash.Add(dlssFrameGeneration.Parameters.FrameIndex);
                    hash.Add(dlssFrameGeneration.Parameters.ResetHistory);
                    hash.Add(dlssFrameGeneration.Parameters.OutputHdr);
                    break;
                case TransformFeedbackOp transformFeedback:
                    hash.Add(transformFeedback.TransformFeedback.GetHashCode());
                    hash.Add((int)transformFeedback.Operation);
                    hash.Add(transformFeedback.CounterBuffer?.GetHashCode() ?? 0);
                    hash.Add(transformFeedback.FeedbackBufferOffset);
                    hash.Add(transformFeedback.FeedbackBufferSize ?? 0ul);
                    hash.Add(transformFeedback.CounterBufferOffset);
                    hash.Add(transformFeedback.CounterOffset);
                    hash.Add(transformFeedback.VertexStride);
                    hash.Add(transformFeedback.InstanceCount);
                    hash.Add(transformFeedback.FirstInstance);
                    break;
                case ComputeDispatchOp compute:
                    hash.Add(compute.Program.GetHashCode());
                    hash.Add(compute.GroupsX);
                    hash.Add(compute.GroupsY);
                    hash.Add(compute.GroupsZ);
                    HashProgramBindingSnapshot(ref hash, compute.Snapshot, compute.Context.PipelineInstance);
                    break;
                case TextureUploadFrameOp upload:
                    hash.Add(upload.Upload.PublicationToken);
                    hash.Add(upload.Upload.Request.StreamingGeneration);
                    hash.Add(upload.Upload.Image.Handle);
                    hash.Add(upload.Upload.ImageView.Handle);
                    hash.Add(upload.Upload.Sampler.Handle);
                    hash.Add(upload.Upload.Extent.Width);
                    hash.Add(upload.Upload.Extent.Height);
                    hash.Add(upload.Upload.MipLevels);
                    hash.Add((ulong)Math.Max(upload.Upload.CommittedBytes, 0L));
                    hash.Add(upload.Upload.StagingResources.Length);
                    break;
            }
        }

        return hash.ToHash();
    }

    private static int GetFrameOpKindId(FrameOp op)
        => op switch
        {
            ClearOp => FrameOpKindClear,
            MeshDrawOp => FrameOpKindMeshDraw,
            QueryOp => FrameOpKindQuery,
            BlitOp => FrameOpKindBlit,
            IndirectDrawOp => FrameOpKindIndirectDraw,
            MeshTaskDispatchIndirectCountOp => FrameOpKindMeshTaskDispatchIndirectCount,
            MemoryBarrierOp => FrameOpKindMemoryBarrier,
            DlssUpscaleOp => FrameOpKindDlssUpscale,
            DlssFrameGenerationOp => FrameOpKindDlssFrameGeneration,
            TransformFeedbackOp => FrameOpKindTransformFeedback,
            ComputeDispatchOp => FrameOpKindComputeDispatch,
            TextureUploadFrameOp => FrameOpKindTextureUpload,
            _ => FrameOpKindUnknown
        };

    private static ulong ComputeCommandBufferDataBufferSignature(VkDataBuffer? buffer)
    {
        FrameOpSignatureHasher hash = new();
        if (buffer is null)
        {
            hash.Add(0UL);
            return hash.ToHash();
        }

        hash.Add(buffer.GetHashCode());
        hash.Add(buffer.BufferHandle?.Handle ?? 0UL);
        hash.Add(buffer.AllocatedByteSize);
        hash.Add(buffer.UploadedByteCount);
        hash.Add(buffer.HasPendingUpload);
        hash.Add(buffer.Data.Length);
        hash.Add((int)buffer.Data.Target);
        hash.Add((ulong)buffer.LastUsageFlags);
        return hash.ToHash();
    }

    private struct FrameOpSignatureHasher
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;
        private ulong _value;

        public FrameOpSignatureHasher()
        {
            _value = OffsetBasis;
        }

        public void Add(bool value) => Add(value ? 1 : 0);
        public void Add(int value) => Mix(unchecked((uint)value));
        public void Add(uint value) => Mix(value);
        public void Add(ulong value) => Mix(value);
        public void Add(float value) => Add(BitConverter.SingleToUInt32Bits(value));

        public void Add(string? value)
        {
            if (value is null)
            {
                Add(-1);
                return;
            }

            Add(value.Length);
            for (int i = 0; i < value.Length; i++)
                Add(value[i]);
        }

        public ulong ToHash() => _value;

        private void Mix(ulong value)
        {
            unchecked
            {
                _value ^= value;
                _value *= Prime;
                _value ^= value >> 32;
                _value *= Prime;
            }
        }
    }

    private static void HashProgramBindingSnapshot(
        ref FrameOpSignatureHasher hash,
        ComputeDispatchSnapshot? snapshot,
        XRRenderPipelineInstance? pipeline = null,
        bool includeMutableFrameSourceDescriptors = false)
    {
        if (snapshot is null)
        {
            hash.Add(0);
            return;
        }

        hash.Add(1);
        hash.Add(HashUniformBindingLayout(snapshot.Uniforms));
        hash.Add(HashSamplerUnitBindings(snapshot.Samplers, snapshot.SamplerNamesByUnit, pipeline, includeMutableFrameSourceDescriptors));
        hash.Add(HashSamplerNameBindings(snapshot.SamplersByName, pipeline, includeMutableFrameSourceDescriptors));
        hash.Add(HashImageBindings(snapshot.Images));
        hash.Add(HashBufferBindings(snapshot.Buffers));
    }

    private static ulong HashUniformBindingLayout(Dictionary<string, ProgramUniformValue> uniforms)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (var pair in uniforms)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            item.Add((int)pair.Value.Type);
            item.Add(pair.Value.IsArray);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(uniforms.Count, xor, sum);
    }

    private static int HashUniformBindings(Dictionary<string, ProgramUniformValue> uniforms)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (var pair in uniforms)
        {
            HashCode item = new();
            item.Add(pair.Key, StringComparer.Ordinal);
            item.Add((int)pair.Value.Type);
            item.Add(pair.Value.IsArray);
            HashUniformValue(ref item, pair.Value.Value);
            AddUnorderedItemHash(ref xor, ref sum, unchecked((ulong)item.ToHashCode()));
        }

        return unchecked((int)FinishUnorderedHash(uniforms.Count, xor, sum));
    }

    private static ulong HashSamplerUnitBindings(
        Dictionary<uint, XRTexture> samplers,
        Dictionary<uint, string> samplerNamesByUnit,
        XRRenderPipelineInstance? pipeline = null,
        bool includeMutableFrameSourceDescriptors = false)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (var pair in samplers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            bool mutableFrameSource = samplerNamesByUnit.TryGetValue(pair.Key, out string? samplerName) &&
                IsMutableFrameSourceSamplerName(samplerName, pipeline);
            if (!includeMutableFrameSourceDescriptors && mutableFrameSource)
                AddFrameSourceTextureDescriptorSignature(ref item, pair.Value);
            else
                AddTextureDescriptorSignature(ref item, pair.Value);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(samplers.Count, xor, sum);
    }

    private static ulong HashSamplerNameBindings(
        Dictionary<string, XRTexture> samplers,
        XRRenderPipelineInstance? pipeline = null,
        bool includeMutableFrameSourceDescriptors = false)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (var pair in samplers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            if (!includeMutableFrameSourceDescriptors && IsMutableFrameSourceSamplerName(pair.Key, pipeline))
                AddFrameSourceTextureDescriptorSignature(ref item, pair.Value);
            else
                AddTextureDescriptorSignature(ref item, pair.Value);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(samplers.Count, xor, sum);
    }

    private static ulong HashImageBindings(Dictionary<uint, ProgramImageBinding> images)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (var pair in images)
        {
            ProgramImageBinding binding = pair.Value;
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            AddTextureDescriptorSignature(ref item, binding.Texture);
            item.Add(binding.Level);
            item.Add(binding.Layered);
            item.Add(binding.Layer);
            item.Add((int)binding.Access);
            item.Add((int)binding.Format);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(images.Count, xor, sum);
    }

    private static void AddTextureDescriptorSignature(ref FrameOpSignatureHasher hash, XRTexture? texture)
    {
        hash.Add(texture?.GetHashCode() ?? 0);
        if (texture is null)
        {
            hash.Add(0UL);
            return;
        }

        if (AbstractRenderer.Current is VulkanRenderer renderer &&
            renderer.GetOrCreateAPIRenderObject(texture, generateNow: false) is IVkImageDescriptorSource source)
        {
            hash.Add(source.IsDescriptorReady);
            hash.Add(source.DescriptorGeneration);
            hash.Add(source.DescriptorImage.Handle);
            hash.Add(source.DescriptorView.Handle);
            hash.Add(source.DescriptorSampler.Handle);
            hash.Add((int)source.DescriptorViewType);
            hash.Add((int)source.DescriptorFormat);
            hash.Add((int)source.DescriptorAspect);
            hash.Add((int)source.DescriptorUsage);
            hash.Add((int)source.DescriptorSamples);
            hash.Add(source.DescriptorMipLevels);
            hash.Add(source.DescriptorArrayLayers);
        }
        else
        {
            hash.Add(0UL);
        }
    }

    private static void AddFrameSourceTextureDescriptorSignature(ref FrameOpSignatureHasher hash, XRTexture? texture)
    {
        hash.Add(FrameSourceMutableDescriptorSignature);
    }

    private static ulong ComputeTextureDescriptorSignature(XRTexture? texture)
    {
        FrameOpSignatureHasher hash = new();
        AddTextureDescriptorSignature(ref hash, texture);
        return hash.ToHash();
    }

    private static ulong HashBufferBindings(Dictionary<uint, XRDataBuffer> buffers)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (var pair in buffers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            item.Add(pair.Value.GetHashCode());
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(buffers.Count, xor, sum);
    }

    private static void AddUnorderedItemHash(ref ulong xor, ref ulong sum, ulong itemHash)
    {
        unchecked
        {
            xor ^= itemHash;
            sum += BitOperations.RotateLeft(itemHash, (int)(itemHash & 31));
        }
    }

    private static ulong FinishUnorderedHash(int count, ulong xor, ulong sum)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(count);
        hash.Add(xor);
        hash.Add(sum);
        return hash.ToHash();
    }

    private static void HashUniformValue(ref HashCode hash, object? value)
    {
        if (value is null)
        {
            hash.Add(0);
            return;
        }

        if (value is Array array)
        {
            hash.Add(array.Length);
            HashUniformArray(ref hash, array);
            return;
        }

        hash.Add(value);
    }

    private static void HashUniformArray(ref HashCode hash, Array array)
    {
        switch (array)
        {
            case float[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case int[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case uint[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case bool[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case Vector2[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case Vector3[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case Vector4[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case Matrix4x4[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            default:
                for (int i = 0; i < array.Length; i++)
                    HashUniformValue(ref hash, array.GetValue(i));
                return;
        }
    }

    #endregion

    #region Draw State Snapshot

    internal readonly record struct PendingMeshDraw(
        VkMeshRenderer Renderer,
        Viewport Viewport,
        Rect2D Scissor,
        Viewport[]? IndexedViewports,
        Rect2D[]? IndexedScissors,
        uint ViewportScissorCount,
        SampleCountFlags RasterizationSamples,
        bool DepthTestEnabled,
        bool DepthWriteEnabled,
        CompareOp DepthCompareOp,
        bool StencilTestEnabled,
        StencilOpState FrontStencilState,
        StencilOpState BackStencilState,
        uint StencilWriteMask,
        ColorComponentFlags ColorWriteMask,
        CullModeFlags CullMode,
        FrontFace FrontFace,
        bool BlendEnabled,
        bool AlphaToCoverageEnabled,
        BlendOp ColorBlendOp,
        BlendOp AlphaBlendOp,
        BlendFactor SrcColorBlendFactor,
        BlendFactor DstColorBlendFactor,
        BlendFactor SrcAlphaBlendFactor,
        BlendFactor DstAlphaBlendFactor,
        Matrix4x4 ModelMatrix,
        Matrix4x4 PreviousModelMatrix,
        XRMaterial? MaterialOverride,
        uint Instances,
        EMeshBillboardMode BillboardMode,
        XRCamera? Camera,
        XRCamera? StereoRightEyeCamera,
        bool IsStereoPass,
        bool UseUnjitteredProjection,
        uint TransformId,
        // Camera matrices/vectors are snapshotted at enqueue time
        // while the camera is still the active rendering camera. The command buffer is
        // recorded later, after the pipeline camera stack has been popped, so reading
        // Camera.* at record time can yield stale values.
        Matrix4x4 ViewMatrix,
        Matrix4x4 InverseViewMatrix,
        Matrix4x4 ProjectionMatrix,
        Matrix4x4 InverseProjectionMatrix,
        Matrix4x4 ViewProjectionMatrix,
        Matrix4x4 PreviousViewMatrix,
        Matrix4x4 PreviousProjectionMatrix,
        Matrix4x4 PreviousViewProjectionMatrix,
        Matrix4x4 PreviousViewProjectionMatrixUnjittered,
        Matrix4x4 RightEyeViewMatrix,
        Matrix4x4 RightEyeInverseViewMatrix,
        Matrix4x4 RightEyeProjectionMatrix,
        Matrix4x4 RightEyeInverseProjectionMatrix,
        Matrix4x4 RightEyeViewProjectionMatrix,
        Matrix4x4 PreviousRightEyeViewMatrix,
        Matrix4x4 PreviousRightEyeProjectionMatrix,
        Matrix4x4 PreviousRightEyeViewProjectionMatrix,
        Matrix4x4 PreviousRightEyeViewProjectionMatrixUnjittered,
        Vector3 CameraPosition,
        Vector3 CameraForward,
        Vector3 CameraUp,
        Vector3 CameraRight,
        // Render-area dimensions snapshotted at enqueue time. The live
        // RuntimeEngine.Rendering.State.RenderArea is derived from the pipeline's
        // CurrentRenderRegion, which is reset to Empty by the time the command buffer
        // is recorded, so ScreenWidth/ScreenHeight engine uniforms (used by the debug
        // line/point geometry shaders) must read these snapshots instead.
        int RenderAreaWidth,
        int RenderAreaHeight,
        LayeredShadowUniformState ShadowUniformState,
        VkRenderProgram? PreparedProgram,
        string? PreparedProgramIdentity,
        ComputeDispatchSnapshot? ProgramBindingSnapshot);

    private static bool ViewportEquals(in Viewport a, in Viewport b)
        => a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height && a.MinDepth == b.MinDepth && a.MaxDepth == b.MaxDepth;

    private static bool RectEquals(in Rect2D a, in Rect2D b)
        => a.Offset.X == b.Offset.X && a.Offset.Y == b.Offset.Y && a.Extent.Width == b.Extent.Width && a.Extent.Height == b.Extent.Height;

    #endregion

    public partial class VkMeshRenderer(VulkanRenderer api, XRMeshRenderer.BaseVersion data) : VkObject<XRMeshRenderer.BaseVersion>(api, data), IRenderPreparationState
    {
        private readonly object _bufferStateSync = new();
        private readonly Dictionary<string, VkDataBuffer> _bufferCache = new(StringComparer.Ordinal);
        private XRMesh.BufferCollection? _subscribedRendererBuffers;
        private XRMesh.BufferCollection? _subscribedMeshBuffers;
        private XRDataBuffer? _cachedSkinnedPositionsBuffer;
        private XRDataBuffer? _cachedSkinnedNormalsBuffer;
        private XRDataBuffer? _cachedSkinnedTangentsBuffer;
        private XRDataBuffer? _cachedSkinnedInterleavedBuffer;
        private XRDataBuffer? _cachedPrecombinedBlendshapePositionsBuffer;
        private XRDataBuffer? _cachedPrecombinedBlendshapeNormalsBuffer;
        private XRDataBuffer? _cachedPrecombinedBlendshapeTangentsBuffer;
        private bool _cachedHasValidPrecombinedBlendshapeDeltas;
        private VkDataBuffer? _triangleIndexBuffer;
        private VkDataBuffer? _lineIndexBuffer;
        private VkDataBuffer? _pointIndexBuffer;
        private IndexSize _triangleIndexSize;
        private IndexSize _lineIndexSize;
        private IndexSize _pointIndexSize;
        private bool _triangleIndexBufferExternallyProvided;
        private bool _indexBuffersSkippedForShaderGeneratedVertices;

        private readonly Dictionary<PipelineKey, Pipeline> _pipelines = new();

        internal VulkanFrameDrawStats EstimateFrameDrawStats(in PendingMeshDraw draw)
        {
            lock (_bufferStateSync)
            {
                bool skipLinePointDraws = MeshRenderMaterialResolver.RequiresTriangleOnlyDrawsForCurrentPass();
                uint instances = draw.Instances;
                int drawCalls = 0;
                int trianglesRendered = 0;

                uint triangleIndexCount = _triangleIndexBuffer?.Data.ElementCount ?? 0u;
                if (triangleIndexCount > 0u)
                {
                    drawCalls++;
                    trianglesRendered = AddSaturated(
                        trianglesRendered,
                        EstimateTriangleCount(triangleIndexCount, instances));
                }

                if (!skipLinePointDraws)
                {
                    if ((_lineIndexBuffer?.Data.ElementCount ?? 0u) > 0u)
                        drawCalls++;
                    if ((_pointIndexBuffer?.Data.ElementCount ?? 0u) > 0u)
                        drawCalls++;
                }

                if (drawCalls == 0 && Mesh is not null)
                {
                    uint vertexCount = (uint)Math.Max(Mesh.VertexCount, 0);
                    PrimitiveTopology fallbackTopology = Mesh.Type switch
                    {
                        EPrimitiveType.Points => PrimitiveTopology.PointList,
                        EPrimitiveType.Lines => PrimitiveTopology.LineList,
                        EPrimitiveType.LineStrip => PrimitiveTopology.LineStrip,
                        EPrimitiveType.TriangleStrip => PrimitiveTopology.TriangleStrip,
                        EPrimitiveType.TriangleFan => PrimitiveTopology.TriangleFan,
                        EPrimitiveType.Patches => PrimitiveTopology.PatchList,
                        _ => PrimitiveTopology.TriangleList,
                    };

                    if (vertexCount > 0u && (!skipLinePointDraws || IsTriangleClassTopology(fallbackTopology)))
                    {
                        drawCalls = 1;
                        if (IsTriangleClassTopology(fallbackTopology))
                            trianglesRendered = EstimateTriangleCount(vertexCount, instances);
                    }
                }

                return new VulkanFrameDrawStats(drawCalls, MultiDrawCalls: 0, trianglesRendered);
            }
        }

        private static int EstimateTriangleCount(uint vertexOrIndexCount, uint instances)
            => SaturateToInt((ulong)(vertexOrIndexCount / 3u) * instances);

        private static int AddSaturated(int current, int value)
        {
            long total = (long)current + value;
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        private VkRenderProgram? _program;
        private XRRenderProgram? _generatedProgram;
        private string? _activeProgramIdentity;
        private readonly Dictionary<string, GeneratedProgramCacheEntry> _programCache = new(StringComparer.Ordinal);
        private VertexInputBindingDescription[] _vertexBindings = [];
        private VertexInputAttributeDescription[] _vertexAttributes = [];
        private bool _vertexInputStateDirty = true;
        private MeshGeometryLayoutSignature _geometryLayoutSignature = MeshGeometryLayoutSignature.Empty;
        private readonly Dictionary<uint, VkDataBuffer> _vertexBuffersByBinding = new();
        private readonly Silk.NET.Vulkan.Buffer[] _singleVertexBindingBuffer = new Silk.NET.Vulkan.Buffer[1];
        private readonly ulong[] _singleVertexBindingOffset = [0UL];
        private bool _buffersDirty = true;
        private bool _pipelineDirty = true;
        private XRMaterial? _lastPreparedMaterial;
        private string _lastPrepareResult = "NeverCalled";
        private string _lastPrepareDetail = string.Empty;
        private int _pipelineShaderConfigVersion = -1;
        private bool _pipelineUsesShaderClipDepthRemap;
        private bool _pipelineUsesNativeDepthClipControl;
        private DescriptorPool _descriptorPool;
        private DescriptorSet[][]? _descriptorSets;
        private DescriptorAllocation? _activeDescriptorAllocation;
        private readonly Dictionary<DescriptorAllocationKey, DescriptorAllocation> _descriptorAllocations = new();
        private bool _descriptorDirty = true;
        private ulong _descriptorSchemaFingerprint;
        private ulong _descriptorResourceFingerprint;
        private string _descriptorResourceFingerprintDetails = string.Empty;
        private int _uniformDrawSlotCapacity = 1;
        private readonly HashSet<string> _descriptorWarnings = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, EngineUniformBuffer[]> _engineUniformBuffers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _engineUniformWarnings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AutoUniformBuffer[]> _autoUniformBuffers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _autoUniformWarnings = new(StringComparer.Ordinal);
        private const string VertexUniformSuffix = "_VTX";
        private const string TransformIdUniformName = "TransformId";
        private const string SkinPaletteBaseUniformName = "skinPaletteBase";
        private const string SkinPaletteCountUniformName = "skinPaletteCount";
        private const string SkinningInfluenceCapUniformName = "skinningInfluenceCap";
        private const string BlendshapeActiveCountUniformName = "blendshapeActiveCount";
        private const string BlendshapeWeightThresholdUniformName = "blendshapeWeightThreshold";
        private const string UsePrecombinedBlendshapeDeltasUniformName = "usePrecombinedBlendshapeDeltas";
        private const string FallbackDescriptorUniformName = "__FallbackDescriptorBuffer";
        private const uint FallbackDescriptorUniformSize = 1024u;
        private const uint ComputeInterleavedBinding = 9u;
        private const uint ComputePositionBinding = 11u;
        private const uint ComputeNormalBinding = 12u;
        private const uint PrecombinedBlendshapePositionBinding = 13u;
        private const uint PrecombinedBlendshapeNormalBinding = 14u;
        private const uint ComputeTangentBinding = 15u;
        private const uint PrecombinedBlendshapeTangentBinding = 15u;
        private const string ComputeInterleavedBufferName = "SkinnedInterleaved";
        private const string ComputePositionBufferName = "SkinnedPositions";
        private const string ComputeNormalBufferName = "SkinnedNormals";
        private const string ComputeTangentBufferName = "SkinnedTangents";
        private const string PrecombinedBlendshapePositionBufferName = "PrecombinedBlendshapePositionDeltas";
        private const string PrecombinedBlendshapeNormalBufferName = "PrecombinedBlendshapeNormalDeltas";
        private const string PrecombinedBlendshapeTangentBufferName = "PrecombinedBlendshapeTangentDeltas";

        private static bool IsStencilCapableFormat(Format format)
            => format is Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint;

        public XRMeshRenderer MeshRenderer => Data.Parent;
        public XRMesh? Mesh => MeshRenderer.Mesh;
        public override VkObjectType Type => VkObjectType.MeshRenderer;
        public override bool IsGenerated => IsActive;

        protected override uint CreateObjectInternal() => CacheObject(this);

        protected override void DeleteObjectInternal()
        {
            Renderer.DrainVulkanPipelineCompileJobsForOwner(this);
            DestroyPipelines();
            DestroyGeneratedPrograms();
            RemoveCachedObject(BindingId);
        }

        protected override void LinkData()
        {
            Data.RenderRequested += OnRenderRequested;
            MeshRenderer.PropertyChanged += OnMeshRendererPropertyChanged;
            MeshRenderer.PropertyChanging += OnMeshRendererPropertyChanging;
            SubscribeRendererBuffers(MeshRenderer.Buffers);

            if (Mesh is not null)
                Mesh.DataChanged += OnMeshChanged;
            SubscribeMeshBufferCollection(Mesh?.Buffers);

            CollectBuffers();
        }

        protected override void UnlinkData()
        {
            Data.RenderRequested -= OnRenderRequested;
            MeshRenderer.PropertyChanged -= OnMeshRendererPropertyChanged;
            MeshRenderer.PropertyChanging -= OnMeshRendererPropertyChanging;
            SubscribeRendererBuffers(null);

            if (Mesh is not null)
                Mesh.DataChanged -= OnMeshChanged;
            SubscribeMeshBufferCollection(null);

            Renderer.DrainVulkanPipelineCompileJobsForOwner(this);
            DestroyPipelines();
            DestroyGeneratedPrograms();
            lock (_bufferStateSync)
            {
                _bufferCache.Clear();
                _vertexBuffersByBinding.Clear();
                _triangleIndexBuffer = null;
                _lineIndexBuffer = null;
                _pointIndexBuffer = null;
                _indexBuffersSkippedForShaderGeneratedVertices = false;
            }
        }

        private void OnBuffersChanged() => InvalidateGeometryLayout("RendererBuffersChanged", collectBuffers: true);
        private void OnMeshBuffersChanged() => InvalidateGeometryLayout("MeshBuffersChanged", collectBuffers: true);

        private void OnMeshRendererPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(XRMeshRenderer.Mesh):
                    if (MeshRenderer.Mesh is not null)
                        MeshRenderer.Mesh.DataChanged += OnMeshChanged;

                    SubscribeMeshBufferCollection(MeshRenderer.Mesh?.Buffers);
                    InvalidateGeometryLayout("MeshChanged", collectBuffers: true);
                    break;
                case nameof(XRMeshRenderer.Material):
                    _pipelineDirty = true;
                    _descriptorDirty = true;
                    _lastPreparedMaterial = null;
                    break;
            }
        }

        private void OnMeshRendererPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
            if (e.PropertyName == nameof(XRMeshRenderer.Mesh) && e.CurrentValue is XRMesh currentMesh)
            {
                currentMesh.DataChanged -= OnMeshChanged;
                if (ReferenceEquals(_subscribedMeshBuffers, currentMesh.Buffers))
                    SubscribeMeshBufferCollection(null);
            }
        }

        private void OnMeshChanged(XRMesh? mesh)
            => InvalidateGeometryLayout("MeshDataChanged", collectBuffers: true);

        private void SubscribeRendererBuffers(XRMesh.BufferCollection? buffers)
        {
            if (ReferenceEquals(_subscribedRendererBuffers, buffers))
                return;

            if (_subscribedRendererBuffers is not null)
                _subscribedRendererBuffers.Changed -= OnBuffersChanged;

            _subscribedRendererBuffers = buffers;

            if (_subscribedRendererBuffers is not null)
                _subscribedRendererBuffers.Changed += OnBuffersChanged;
        }

        private void SubscribeMeshBufferCollection(XRMesh.BufferCollection? buffers)
        {
            if (ReferenceEquals(_subscribedMeshBuffers, buffers))
                return;

            if (_subscribedMeshBuffers is not null)
                _subscribedMeshBuffers.Changed -= OnMeshBuffersChanged;

            _subscribedMeshBuffers = buffers;

            if (_subscribedMeshBuffers is not null)
                _subscribedMeshBuffers.Changed += OnMeshBuffersChanged;
        }

        private void InvalidateGeometryLayout(string reason, bool collectBuffers)
        {
            lock (_bufferStateSync)
            {
                _pipelineDirty = true;
                _buffersDirty = true;
                _descriptorDirty = true;
                _vertexInputStateDirty = true;
                _lastPreparedMaterial = null;
                _triangleIndexBuffer = null;
                _lineIndexBuffer = null;
                _pointIndexBuffer = null;
                _indexBuffersSkippedForShaderGeneratedVertices = false;
                _geometryLayoutSignature = MeshGeometryLayoutSignature.Empty;
                _lastPrepareResult = reason;
                _lastPrepareDetail = "Geometry layout changed.";

                if (collectBuffers)
                    CollectBuffers();
                else
                    Renderer.MarkCommandBuffersDirty();
            }
        }

        private void OnRenderRequested(Matrix4x4 modelMatrix, Matrix4x4 prevModelMatrix, XRMaterial? materialOverride, RenderingParameters? renderOptionsOverride, uint instances, EMeshBillboardMode billboardMode)
        {
            if (!IsActive)
                Generate();

            // Don't enqueue mesh draw ops when there's no active rendering pipeline;
            // they would be emitted with an invalid pass index and dropped at recording time.
            if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline is null)
                return;

            int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
            XRFrameBuffer? target = Renderer.ResolveCurrentFrameOpDrawTarget();

            // Resolve the effective material and its render options so the
            // pipeline key captures per-material state (CullMode, DepthTest, etc.)
            // instead of inheriting stale values from the global state tracker.
            XRMaterial effectiveMaterial = ResolveMaterial(materialOverride, instances);
            uint drawInstances = MeshRenderMaterialResolver.ResolveLayeredShadowInstanceCount(effectiveMaterial, instances);
            if (!TryPrepareForRendering(effectiveMaterial, out string prepareReason))
            {
                // A skipped draw means the recorded frame is incomplete. Keep the
                // command buffers invalid until the pending program/buffers/descriptors
                // are ready on the legacy primary path. Command-chain primaries are
                // invalidated by the frame-op signature when the draw becomes available.
                Renderer.MarkCommandBuffersDirtyForLegacyMeshState();
                Debug.VulkanWarningEvery(
                    $"Vulkan.MeshRenderer.PrepareSkip.{MeshRenderer.Name ?? "UnnamedRenderer"}.{prepareReason}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Skipping mesh draw enqueue for renderer='{0}' mesh='{1}' material='{2}' because render preparation is not ready: {3}. {4}",
                    MeshRenderer.Name ?? "<unnamed renderer>",
                    Mesh?.Name ?? "<unnamed mesh>",
                    effectiveMaterial.Name ?? "<unnamed material>",
                    prepareReason,
                    LastPrepareDetail);
                return;
            }

            RenderingParameters? matOpts = renderOptionsOverride ?? effectiveMaterial.RenderOptions;

            // ── CullMode ──
            CullModeFlags cullMode;
            if (matOpts is not null)
                cullMode = ToVulkanCullMode(ResolveCullMode(matOpts.CullMode));
            else
                cullMode = Renderer.GetCullMode();

            // ── FrontFace ──
            FrontFace frontFace;
            if (matOpts is not null)
                frontFace = ToVulkanFrontFace(ResolveWinding(matOpts.Winding));
            else
                frontFace = Renderer.GetFrontFace();

            // ── DepthTest ──
            bool depthTestEnabled;
            bool depthWriteEnabled;
            CompareOp depthCompareOp;
            SampleCountFlags rasterizationSamples = ResolveRasterizationSamples(target);
            if (matOpts?.DepthTest is { } dt && dt.Enabled != ERenderParamUsage.Unchanged)
            {
                depthTestEnabled = dt.Enabled == ERenderParamUsage.Enabled;
                depthWriteEnabled = depthTestEnabled && dt.UpdateDepth;
                depthCompareOp = depthTestEnabled
                    ? ToVulkanCompareOp(RuntimeEngine.Rendering.State.MapDepthComparison(dt.Function))
                    : CompareOp.Always;
            }
            else
            {
                depthTestEnabled = Renderer.GetDepthTestEnabled();
                depthWriteEnabled = Renderer.GetDepthWriteEnabled();
                depthCompareOp = Renderer.GetDepthCompareOp();
            }

            // ── Blend ──
            bool blendEnabled;
            bool alphaToCoverageEnabled;
            BlendOp colorBlendOp, alphaBlendOp;
            BlendFactor srcColor, dstColor, srcAlpha, dstAlpha;
            BlendMode? matBlend = matOpts is not null ? ResolveBlendMode(matOpts) : null;
            bool requestedAlphaToCoverage = matOpts?.AlphaToCoverage == ERenderParamUsage.Enabled;
            if (matBlend is not null && matBlend.Enabled == ERenderParamUsage.Enabled)
            {
                blendEnabled = true;
                alphaToCoverageEnabled = requestedAlphaToCoverage && rasterizationSamples != SampleCountFlags.Count1Bit;
                colorBlendOp = ToVulkanBlendOp(matBlend.RgbEquation);
                alphaBlendOp = ToVulkanBlendOp(matBlend.AlphaEquation);
                srcColor = ToVulkanBlendFactor(matBlend.RgbSrcFactor);
                dstColor = ToVulkanBlendFactor(matBlend.RgbDstFactor);
                srcAlpha = ToVulkanBlendFactor(matBlend.AlphaSrcFactor);
                dstAlpha = ToVulkanBlendFactor(matBlend.AlphaDstFactor);
            }
            else if (matBlend is not null && matBlend.Enabled == ERenderParamUsage.Disabled)
            {
                blendEnabled = false;
                alphaToCoverageEnabled = requestedAlphaToCoverage && rasterizationSamples != SampleCountFlags.Count1Bit;
                colorBlendOp = BlendOp.Add;
                alphaBlendOp = BlendOp.Add;
                srcColor = BlendFactor.One;
                dstColor = BlendFactor.Zero;
                srcAlpha = BlendFactor.One;
                dstAlpha = BlendFactor.Zero;
            }
            else if (matBlend is null && matOpts is not null)
            {
                blendEnabled = false;
                alphaToCoverageEnabled = requestedAlphaToCoverage && rasterizationSamples != SampleCountFlags.Count1Bit;
                colorBlendOp = BlendOp.Add;
                alphaBlendOp = BlendOp.Add;
                srcColor = BlendFactor.One;
                dstColor = BlendFactor.Zero;
                srcAlpha = BlendFactor.One;
                dstAlpha = BlendFactor.Zero;
            }
            else
            {
                blendEnabled = Renderer.GetBlendEnabled();
                alphaToCoverageEnabled = Renderer.GetAlphaToCoverageEnabled() && rasterizationSamples != SampleCountFlags.Count1Bit;
                colorBlendOp = Renderer.GetColorBlendOp();
                alphaBlendOp = Renderer.GetAlphaBlendOp();
                srcColor = Renderer.GetSrcColorBlendFactor();
                dstColor = Renderer.GetDstColorBlendFactor();
                srcAlpha = Renderer.GetSrcAlphaBlendFactor();
                dstAlpha = Renderer.GetDstAlphaBlendFactor();
            }

            bool stencilTestEnabled;
            StencilOpState frontStencilState;
            StencilOpState backStencilState;
            uint stencilWriteMask;
            if (matOpts?.StencilTest is { } stencilTest && stencilTest.Enabled != ERenderParamUsage.Unchanged)
            {
                if (stencilTest.Enabled == ERenderParamUsage.Enabled)
                {
                    stencilTestEnabled = true;
                    frontStencilState = ToVulkanStencilState(stencilTest.FrontFace);
                    backStencilState = ToVulkanStencilState(stencilTest.BackFace);
                    stencilWriteMask = stencilTest.FrontFace.WriteMask;
                }
                else
                {
                    stencilTestEnabled = false;
                    frontStencilState = default;
                    backStencilState = default;
                    stencilWriteMask = 0u;
                }
            }
            else
            {
                stencilTestEnabled = Renderer.GetStencilTestEnabled();
                frontStencilState = Renderer.GetFrontStencilState();
                backStencilState = Renderer.GetBackStencilState();
                stencilWriteMask = Renderer.GetStencilWriteMask();
            }

            ColorComponentFlags colorWriteMask = matOpts is not null
                ? ToVulkanColorWriteMask(matOpts)
                : Renderer.GetColorWriteMask();

            // Snapshot camera matrices/vectors now. Fullscreen and UI paths may
            // intentionally render while the transient rendering camera is null,
            // but Vulkan records commands later and still needs the active pipeline
            // camera for auto-uniforms such as inverse view/projection matrices.
            XRRenderPipelineInstance? currentPipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            XRCamera? snapshotCamera = RuntimeEngine.Rendering.State.RenderingCamera
                ?? currentPipeline?.RenderState.RenderingCamera
                ?? currentPipeline?.RenderState.SceneCamera
                ?? currentPipeline?.LastRenderingCamera
                ?? currentPipeline?.LastSceneCamera;
            XRCamera? snapshotRightEyeCamera = RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera
                ?? currentPipeline?.RenderState.StereoRightEyeCamera;
            bool useUnjitteredProjectionSnapshot = RuntimeEngine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
            Matrix4x4 viewMatrixSnapshot = snapshotCamera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
            Matrix4x4 inverseViewMatrixSnapshot = snapshotCamera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
            Matrix4x4 projectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotCamera is not null
                ? snapshotCamera.ProjectionMatrixUnjittered
                : snapshotCamera?.ProjectionMatrix ?? Matrix4x4.Identity;
            Matrix4x4 inverseProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotCamera is not null
                ? snapshotCamera.InverseProjectionMatrixUnjittered
                : snapshotCamera?.InverseProjectionMatrix ?? Matrix4x4.Identity;
            Matrix4x4 viewProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotCamera is not null
                ? snapshotCamera.ViewProjectionMatrixUnjittered
                : snapshotCamera?.ViewProjectionMatrix ?? Matrix4x4.Identity;
            Matrix4x4 rightEyeViewMatrixSnapshot = snapshotRightEyeCamera?.Transform.InverseRenderMatrix ?? viewMatrixSnapshot;
            Matrix4x4 rightEyeInverseViewMatrixSnapshot = snapshotRightEyeCamera?.Transform.RenderMatrix ?? inverseViewMatrixSnapshot;
            Matrix4x4 rightEyeProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotRightEyeCamera is not null
                ? snapshotRightEyeCamera.ProjectionMatrixUnjittered
                : snapshotRightEyeCamera?.ProjectionMatrix ?? projectionMatrixSnapshot;
            Matrix4x4 rightEyeInverseProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotRightEyeCamera is not null
                ? snapshotRightEyeCamera.InverseProjectionMatrixUnjittered
                : snapshotRightEyeCamera?.InverseProjectionMatrix ?? inverseProjectionMatrixSnapshot;
            Matrix4x4 rightEyeViewProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotRightEyeCamera is not null
                ? snapshotRightEyeCamera.ViewProjectionMatrixUnjittered
                : snapshotRightEyeCamera?.ViewProjectionMatrix ?? viewProjectionMatrixSnapshot;
            Matrix4x4 previousViewMatrixSnapshot = viewMatrixSnapshot;
            Matrix4x4 previousProjectionMatrixSnapshot = projectionMatrixSnapshot;
            Matrix4x4 previousViewProjectionMatrixSnapshot = viewProjectionMatrixSnapshot;
            Matrix4x4 previousViewProjectionMatrixUnjitteredSnapshot = snapshotCamera?.ViewProjectionMatrixUnjittered ?? viewProjectionMatrixSnapshot;
            Matrix4x4 previousRightEyeViewMatrixSnapshot = rightEyeViewMatrixSnapshot;
            Matrix4x4 previousRightEyeProjectionMatrixSnapshot = rightEyeProjectionMatrixSnapshot;
            Matrix4x4 previousRightEyeViewProjectionMatrixSnapshot = rightEyeViewProjectionMatrixSnapshot;
            Matrix4x4 previousRightEyeViewProjectionMatrixUnjitteredSnapshot =
                snapshotRightEyeCamera?.ViewProjectionMatrixUnjittered ?? rightEyeViewProjectionMatrixSnapshot;
            if (VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData) && temporalData.HistoryReady)
            {
                previousViewMatrixSnapshot = temporalData.PrevViewMatrix;
                previousProjectionMatrixSnapshot = temporalData.PrevProjection;
                previousViewProjectionMatrixSnapshot = temporalData.PrevViewProjection;
                previousViewProjectionMatrixUnjitteredSnapshot = temporalData.PrevViewProjectionUnjittered;
                previousRightEyeViewMatrixSnapshot = temporalData.RightEyePrevViewMatrix;
                previousRightEyeProjectionMatrixSnapshot = temporalData.RightEyePrevProjection;
                previousRightEyeViewProjectionMatrixSnapshot = temporalData.RightEyePrevViewProjection;
                previousRightEyeViewProjectionMatrixUnjitteredSnapshot = temporalData.RightEyePrevViewProjectionUnjittered;
            }
            Vector3 cameraPositionSnapshot = snapshotCamera?.Transform.RenderTranslation ?? Vector3.Zero;
            Vector3 cameraForwardSnapshot = snapshotCamera?.Transform.RenderForward ?? Vector3.UnitZ;
            Vector3 cameraUpSnapshot = snapshotCamera?.Transform.RenderUp ?? Vector3.UnitY;
            Vector3 cameraRightSnapshot = snapshotCamera?.Transform.RenderRight ?? Vector3.UnitX;
            uint transformIdSnapshot = RuntimeEngine.Rendering.State.CurrentTransformId;
            // Snapshot the render-area dimensions now (the live RenderArea is reset to Empty by
            // deferred record time). For debug-primitive draws the pipeline render-region can
            // already be Empty even at enqueue time, so fall back to the bound draw framebuffer's
            // dimensions, which reflect the actual target the geometry shaders rasterize into.
            var renderAreaSnapshot = RuntimeEngine.Rendering.State.RenderArea;
            int renderAreaWidthSnapshot = renderAreaSnapshot.Width;
            int renderAreaHeightSnapshot = renderAreaSnapshot.Height;
            if (renderAreaWidthSnapshot <= 0 || renderAreaHeightSnapshot <= 0)
            {
                if (target is not null)
                {
                    renderAreaWidthSnapshot = (int)target.Width;
                    renderAreaHeightSnapshot = (int)target.Height;
                }
                else
                {
                    Extent2D targetExtent = Renderer.GetCurrentTargetExtent();
                    renderAreaWidthSnapshot = (int)targetExtent.Width;
                    renderAreaHeightSnapshot = (int)targetExtent.Height;
                }
            }

            LayeredShadowUniformState shadowUniformState = LayeredShadowUniformState.CaptureFromCurrentRenderingState();
            ComputeDispatchSnapshot? programBindingSnapshot = CaptureProgramBindingSnapshot(effectiveMaterial, shadowUniformState);
            IndexedViewportScissorSnapshot indexedViewportScissors = Renderer.GetCurrentIndexedViewportScissorSnapshot();
            uint viewportScissorCount = indexedViewportScissors.Count > 1 ? indexedViewportScissors.Count : 1u;
            Viewport viewportSnapshot = Renderer.GetCurrentViewport();
            Rect2D scissorSnapshot = Renderer.GetCurrentScissor();
            var draw = new PendingMeshDraw(
                this,
                viewportSnapshot,
                scissorSnapshot,
                viewportScissorCount > 1 ? indexedViewportScissors.Viewports : null,
                viewportScissorCount > 1 ? indexedViewportScissors.Scissors : null,
                viewportScissorCount,
                rasterizationSamples,
                depthTestEnabled,
                depthWriteEnabled,
                depthCompareOp,
                stencilTestEnabled,
                frontStencilState,
                backStencilState,
                stencilWriteMask,
                colorWriteMask,
                cullMode,
                frontFace,
                blendEnabled,
                alphaToCoverageEnabled,
                colorBlendOp,
                alphaBlendOp,
                srcColor,
                dstColor,
                srcAlpha,
                dstAlpha,
                modelMatrix,
                prevModelMatrix,
                effectiveMaterial,
                drawInstances,
                billboardMode,
                snapshotCamera,
                snapshotRightEyeCamera,
                RuntimeEngine.Rendering.State.IsStereoPass,
                useUnjitteredProjectionSnapshot,
                transformIdSnapshot,
                viewMatrixSnapshot,
                inverseViewMatrixSnapshot,
                projectionMatrixSnapshot,
                inverseProjectionMatrixSnapshot,
                viewProjectionMatrixSnapshot,
                previousViewMatrixSnapshot,
                previousProjectionMatrixSnapshot,
                previousViewProjectionMatrixSnapshot,
                previousViewProjectionMatrixUnjitteredSnapshot,
                rightEyeViewMatrixSnapshot,
                rightEyeInverseViewMatrixSnapshot,
                rightEyeProjectionMatrixSnapshot,
                rightEyeInverseProjectionMatrixSnapshot,
                rightEyeViewProjectionMatrixSnapshot,
                previousRightEyeViewMatrixSnapshot,
                previousRightEyeProjectionMatrixSnapshot,
                previousRightEyeViewProjectionMatrixSnapshot,
                previousRightEyeViewProjectionMatrixUnjitteredSnapshot,
                cameraPositionSnapshot,
                cameraForwardSnapshot,
                cameraUpSnapshot,
                cameraRightSnapshot,
                renderAreaWidthSnapshot,
                renderAreaHeightSnapshot,
                shadowUniformState,
                _program,
                _activeProgramIdentity,
                programBindingSnapshot);

            FrameOpContext context = Renderer.CaptureFrameOpContext();
            Renderer.EnqueueFrameOp(new MeshDrawOp(
                Renderer.EnsureValidPassIndex(passIndex, "MeshDraw", context.PassMetadata),
                target,
                draw,
                context)
            {
                PreserveSubmissionOrder = VulkanRenderer.IsInOcclusionQueryBracket,
            });
        }

        internal bool TryCreatePreparedIndirectDrawSnapshot(
            XRMaterial effectiveMaterial,
            VkRenderProgram preparedProgram,
            string? preparedProgramIdentity,
            ComputeDispatchSnapshot? programBindingSnapshot,
            Matrix4x4 modelMatrix,
            XRFrameBuffer? target,
            out PendingMeshDraw draw,
            out string reason)
        {
            draw = default;
            reason = "Ready";

            if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline is null)
                return SetPrepareResult(false, "PipelineMissing", "No active rendering pipeline is available for indirect draw capture.", out reason);

            bool preparedForIndirect;
            if (Renderer.IsPrewarmingOpenXrExternalSwapchainTarget)
            {
                preparedForIndirect = TryPrepareCapturedProgramForRecording(effectiveMaterial, preparedProgram, preparedProgramIdentity, programBindingSnapshot, 0, out reason);
            }
            else if (Renderer.IsRenderingExternalSwapchainTarget)
            {
                using (Renderer.BlockSynchronousResourceUploads("IndirectDrawSnapshot"))
                {
                    preparedForIndirect = TryReuseCapturedProgramForIndirectDrawSnapshot(effectiveMaterial, preparedProgram, preparedProgramIdentity, programBindingSnapshot, 0, out reason);
                    if (!preparedForIndirect)
                        preparedForIndirect = TryPrepareCapturedProgramForRecording(effectiveMaterial, preparedProgram, preparedProgramIdentity, programBindingSnapshot, 0, out reason);
                }
            }
            else
            {
                using (Renderer.BlockSynchronousResourceUploads("IndirectDrawSnapshot"))
                {
                    preparedForIndirect = TryReuseCapturedProgramForIndirectDrawSnapshot(effectiveMaterial, preparedProgram, preparedProgramIdentity, programBindingSnapshot, 0, out reason);
                }

                if (!preparedForIndirect)
                    preparedForIndirect = TryPrepareCapturedProgramForRecording(effectiveMaterial, preparedProgram, preparedProgramIdentity, programBindingSnapshot, 0, out reason);
            }

            if (!preparedForIndirect)
                return false;

            XRFrameBuffer? effectiveTarget = target ?? Renderer.ResolveCurrentFrameOpDrawTarget();
            SampleCountFlags rasterizationSamples = ResolveRasterizationSamples(effectiveTarget);
            bool alphaToCoverageEnabled = Renderer.GetAlphaToCoverageEnabled() && rasterizationSamples != SampleCountFlags.Count1Bit;

            XRRenderPipelineInstance? currentPipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            XRCamera? snapshotCamera = RuntimeEngine.Rendering.State.RenderingCamera
                ?? currentPipeline?.RenderState.RenderingCamera
                ?? currentPipeline?.RenderState.SceneCamera
                ?? currentPipeline?.LastRenderingCamera
                ?? currentPipeline?.LastSceneCamera;
            XRCamera? snapshotRightEyeCamera = RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera
                ?? currentPipeline?.RenderState.StereoRightEyeCamera;
            bool useUnjitteredProjectionSnapshot = RuntimeEngine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
            Matrix4x4 viewMatrixSnapshot = snapshotCamera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
            Matrix4x4 inverseViewMatrixSnapshot = snapshotCamera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
            Matrix4x4 projectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotCamera is not null
                ? snapshotCamera.ProjectionMatrixUnjittered
                : snapshotCamera?.ProjectionMatrix ?? Matrix4x4.Identity;
            Matrix4x4 inverseProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotCamera is not null
                ? snapshotCamera.InverseProjectionMatrixUnjittered
                : snapshotCamera?.InverseProjectionMatrix ?? Matrix4x4.Identity;
            Matrix4x4 viewProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotCamera is not null
                ? snapshotCamera.ViewProjectionMatrixUnjittered
                : snapshotCamera?.ViewProjectionMatrix ?? Matrix4x4.Identity;
            Matrix4x4 rightEyeViewMatrixSnapshot = snapshotRightEyeCamera?.Transform.InverseRenderMatrix ?? viewMatrixSnapshot;
            Matrix4x4 rightEyeInverseViewMatrixSnapshot = snapshotRightEyeCamera?.Transform.RenderMatrix ?? inverseViewMatrixSnapshot;
            Matrix4x4 rightEyeProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotRightEyeCamera is not null
                ? snapshotRightEyeCamera.ProjectionMatrixUnjittered
                : snapshotRightEyeCamera?.ProjectionMatrix ?? projectionMatrixSnapshot;
            Matrix4x4 rightEyeInverseProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotRightEyeCamera is not null
                ? snapshotRightEyeCamera.InverseProjectionMatrixUnjittered
                : snapshotRightEyeCamera?.InverseProjectionMatrix ?? inverseProjectionMatrixSnapshot;
            Matrix4x4 rightEyeViewProjectionMatrixSnapshot = useUnjitteredProjectionSnapshot && snapshotRightEyeCamera is not null
                ? snapshotRightEyeCamera.ViewProjectionMatrixUnjittered
                : snapshotRightEyeCamera?.ViewProjectionMatrix ?? viewProjectionMatrixSnapshot;
            Matrix4x4 previousViewMatrixSnapshot = viewMatrixSnapshot;
            Matrix4x4 previousProjectionMatrixSnapshot = projectionMatrixSnapshot;
            Matrix4x4 previousViewProjectionMatrixSnapshot = viewProjectionMatrixSnapshot;
            Matrix4x4 previousViewProjectionMatrixUnjitteredSnapshot = snapshotCamera?.ViewProjectionMatrixUnjittered ?? viewProjectionMatrixSnapshot;
            Matrix4x4 previousRightEyeViewMatrixSnapshot = rightEyeViewMatrixSnapshot;
            Matrix4x4 previousRightEyeProjectionMatrixSnapshot = rightEyeProjectionMatrixSnapshot;
            Matrix4x4 previousRightEyeViewProjectionMatrixSnapshot = rightEyeViewProjectionMatrixSnapshot;
            Matrix4x4 previousRightEyeViewProjectionMatrixUnjitteredSnapshot =
                snapshotRightEyeCamera?.ViewProjectionMatrixUnjittered ?? rightEyeViewProjectionMatrixSnapshot;
            if (VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData) && temporalData.HistoryReady)
            {
                previousViewMatrixSnapshot = temporalData.PrevViewMatrix;
                previousProjectionMatrixSnapshot = temporalData.PrevProjection;
                previousViewProjectionMatrixSnapshot = temporalData.PrevViewProjection;
                previousViewProjectionMatrixUnjitteredSnapshot = temporalData.PrevViewProjectionUnjittered;
                previousRightEyeViewMatrixSnapshot = temporalData.RightEyePrevViewMatrix;
                previousRightEyeProjectionMatrixSnapshot = temporalData.RightEyePrevProjection;
                previousRightEyeViewProjectionMatrixSnapshot = temporalData.RightEyePrevViewProjection;
                previousRightEyeViewProjectionMatrixUnjitteredSnapshot = temporalData.RightEyePrevViewProjectionUnjittered;
            }
            Vector3 cameraPositionSnapshot = snapshotCamera?.Transform.RenderTranslation ?? Vector3.Zero;
            Vector3 cameraForwardSnapshot = snapshotCamera?.Transform.RenderForward ?? Vector3.UnitZ;
            Vector3 cameraUpSnapshot = snapshotCamera?.Transform.RenderUp ?? Vector3.UnitY;
            Vector3 cameraRightSnapshot = snapshotCamera?.Transform.RenderRight ?? Vector3.UnitX;
            uint transformIdSnapshot = RuntimeEngine.Rendering.State.CurrentTransformId;

            var renderAreaSnapshot = RuntimeEngine.Rendering.State.RenderArea;
            int renderAreaWidthSnapshot = renderAreaSnapshot.Width;
            int renderAreaHeightSnapshot = renderAreaSnapshot.Height;
            if (renderAreaWidthSnapshot <= 0 || renderAreaHeightSnapshot <= 0)
            {
                if (effectiveTarget is not null)
                {
                    renderAreaWidthSnapshot = (int)effectiveTarget.Width;
                    renderAreaHeightSnapshot = (int)effectiveTarget.Height;
                }
                else
                {
                    Extent2D targetExtent = Renderer.GetCurrentTargetExtent();
                    renderAreaWidthSnapshot = (int)targetExtent.Width;
                    renderAreaHeightSnapshot = (int)targetExtent.Height;
                }
            }

            LayeredShadowUniformState shadowUniformState = LayeredShadowUniformState.CaptureFromCurrentRenderingState();
            IndexedViewportScissorSnapshot indexedViewportScissors = Renderer.GetCurrentIndexedViewportScissorSnapshot();
            uint viewportScissorCount = indexedViewportScissors.Count > 1 ? indexedViewportScissors.Count : 1u;
            Viewport viewportSnapshot = Renderer.GetCurrentViewport();
            Rect2D scissorSnapshot = Renderer.GetCurrentScissor();
            FrontFace frontFaceSnapshot = Renderer.GetFrontFace();

            draw = new PendingMeshDraw(
                this,
                viewportSnapshot,
                scissorSnapshot,
                viewportScissorCount > 1 ? indexedViewportScissors.Viewports : null,
                viewportScissorCount > 1 ? indexedViewportScissors.Scissors : null,
                viewportScissorCount,
                rasterizationSamples,
                Renderer.GetDepthTestEnabled(),
                Renderer.GetDepthWriteEnabled(),
                Renderer.GetDepthCompareOp(),
                Renderer.GetStencilTestEnabled(),
                Renderer.GetFrontStencilState(),
                Renderer.GetBackStencilState(),
                Renderer.GetStencilWriteMask(),
                Renderer.GetColorWriteMask(),
                Renderer.GetCullMode(),
                frontFaceSnapshot,
                Renderer.GetBlendEnabled(),
                alphaToCoverageEnabled,
                Renderer.GetColorBlendOp(),
                Renderer.GetAlphaBlendOp(),
                Renderer.GetSrcColorBlendFactor(),
                Renderer.GetDstColorBlendFactor(),
                Renderer.GetSrcAlphaBlendFactor(),
                Renderer.GetDstAlphaBlendFactor(),
                modelMatrix,
                modelMatrix,
                effectiveMaterial,
                1u,
                effectiveMaterial.BillboardMode,
                snapshotCamera,
                snapshotRightEyeCamera,
                RuntimeEngine.Rendering.State.IsStereoPass,
                useUnjitteredProjectionSnapshot,
                transformIdSnapshot,
                viewMatrixSnapshot,
                inverseViewMatrixSnapshot,
                projectionMatrixSnapshot,
                inverseProjectionMatrixSnapshot,
                viewProjectionMatrixSnapshot,
                previousViewMatrixSnapshot,
                previousProjectionMatrixSnapshot,
                previousViewProjectionMatrixSnapshot,
                previousViewProjectionMatrixUnjitteredSnapshot,
                rightEyeViewMatrixSnapshot,
                rightEyeInverseViewMatrixSnapshot,
                rightEyeProjectionMatrixSnapshot,
                rightEyeInverseProjectionMatrixSnapshot,
                rightEyeViewProjectionMatrixSnapshot,
                previousRightEyeViewMatrixSnapshot,
                previousRightEyeProjectionMatrixSnapshot,
                previousRightEyeViewProjectionMatrixSnapshot,
                previousRightEyeViewProjectionMatrixUnjitteredSnapshot,
                cameraPositionSnapshot,
                cameraForwardSnapshot,
                cameraUpSnapshot,
                cameraRightSnapshot,
                renderAreaWidthSnapshot,
                renderAreaHeightSnapshot,
                shadowUniformState,
                preparedProgram,
                preparedProgramIdentity,
                programBindingSnapshot);

            return true;
        }

        private ComputeDispatchSnapshot? CaptureProgramBindingSnapshot(XRMaterial material, in LayeredShadowUniformState shadowUniformState)
        {
            if (_program is not { Data: { } programData } program)
                return null;

            bool captureUniforms = MeshRenderer.CaptureUniformsOnRender;
            bool mayNeedDescriptorResourceSnapshot =
                program.DescriptorBindings.Count != 0 &&
                program.DescriptorSetLayouts.Count != 0;
            if (!captureUniforms && !mayNeedDescriptorResourceSnapshot)
                return null;

            VulkanFixedFunctionStateSnapshot stateSnapshot = Renderer.CaptureFixedFunctionState();
            try
            {
                program.ClearBindings();
                Renderer.SetMaterialUniforms(material, programData, shadowUniformState);
                if (MeshRenderer.HasSettingUniformsHandlers)
                    MeshRenderer.OnSettingUniforms(programData, programData);
                else
                    RuntimeEngine.Rendering.State.RenderingPipelineState?.ApplyScopedProgramBindings(programData);
                MeshRenderMaterialResolver.ApplyShadowUniforms(programData, material, shadowUniformState);
                if (!captureUniforms && !program.HasBoundDescriptorResources())
                    return null;

                ComputeDispatchSnapshot snapshot = program.CaptureComputeSnapshot();
                LogGizmoBindingSnapshot(material, snapshot, "capture");
                return snapshot;
            }
            finally
            {
                Renderer.RestoreFixedFunctionState(stateSnapshot);
            }
        }

        private void LogGizmoBindingSnapshot(XRMaterial material, ComputeDispatchSnapshot snapshot, string phase)
        {
            if (!MaterialBindingDiagnosticsEnabled || !IsGizmoDiagnosticProgram())
                return;

            Debug.MeshesWarningEvery(
                $"Vulkan.GizmoBindingSnapshot.{GetHashCode()}.{_program?.Data?.Name}.{material.Name}.{phase}",
                TimeSpan.FromSeconds(1),
                "[VkGizmoBindingSnapshot] phase={0} program='{1}' mesh='{2}' material='{3}' uniforms={4} MatColor={5} LineWidth={6} ArrowHeadLengthPixels={7} ArrowHeadHalfWidthPixels={8}",
                phase,
                _program?.Data?.Name ?? "<null>",
                Mesh?.Name ?? "<null>",
                material.Name ?? "<null>",
                snapshot.Uniforms.Count,
                FormatSnapshotUniform(snapshot, "MatColor"),
                FormatSnapshotUniform(snapshot, "LineWidth"),
                FormatSnapshotUniform(snapshot, "ArrowHeadLengthPixels"),
                FormatSnapshotUniform(snapshot, "ArrowHeadHalfWidthPixels"));
        }

        private static string FormatSnapshotUniform(ComputeDispatchSnapshot snapshot, string name)
        {
            if (!snapshot.Uniforms.TryGetValue(name, out ProgramUniformValue value))
                return "<missing>";

            string arraySuffix = value.IsArray ? "[]" : string.Empty;
            return $"{value.Type}{arraySuffix}:{FormatMaterialUniformDiagnosticValue(value.Value)}";
        }

        private static SampleCountFlags ResolveRasterizationSamples(XRFrameBuffer? target)
            => target?.EffectiveSampleCount switch
            {
                >= 64u => SampleCountFlags.Count64Bit,
                >= 32u => SampleCountFlags.Count32Bit,
                >= 16u => SampleCountFlags.Count16Bit,
                >= 8u => SampleCountFlags.Count8Bit,
                >= 4u => SampleCountFlags.Count4Bit,
                >= 2u => SampleCountFlags.Count2Bit,
                _ => SampleCountFlags.Count1Bit,
            };
    }
}

// Remaining VkMeshRenderer implementation lives in partial files:
// - VkMeshRenderer.Buffers.cs
// - VkMeshRenderer.Pipeline.cs
// - VkMeshRenderer.Drawing.cs
// - VkMeshRenderer.Descriptors.cs
// - VkMeshRenderer.Uniforms.cs
// - VkMeshRenderer.Cleanup.cs
