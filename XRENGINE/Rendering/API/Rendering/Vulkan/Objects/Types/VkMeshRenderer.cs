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
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    #region Frame Operation Queue

    private readonly Lock _frameOpsLock = new();
    private readonly List<FrameOp> _frameOps = [];

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

    internal sealed record MeshDrawOp(int PassIndex, XRFrameBuffer? Target, PendingMeshDraw Draw, FrameOpContext Context) : FrameOp(PassIndex, Target, Context);

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
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);

    internal sealed record IndirectDrawOp(
        int PassIndex,
        VkDataBuffer IndirectBuffer,
        VkDataBuffer? ParameterBuffer,
        uint DrawCount,
        uint Stride,
        nuint ByteOffset,
        bool UseCount,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);

    internal sealed record MemoryBarrierOp(
        int PassIndex,
        EMemoryBarrierMask Mask,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);

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
        Dictionary<uint, ProgramImageBinding> Images,
        Dictionary<uint, XRDataBuffer> Buffers);

    internal sealed record ComputeDispatchOp(
        int PassIndex,
        VkRenderProgram Program,
        uint GroupsX,
        uint GroupsY,
        uint GroupsZ,
        ComputeDispatchSnapshot Snapshot,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);

    internal void EnqueueFrameOp(FrameOp op)
    {
        FrameOp validatedOp = EnsureValidFrameOpPassIndex(op);
        using (_frameOpsLock.EnterScope())
            _frameOps.Add(validatedOp);
    }

    private FrameOp EnsureValidFrameOpPassIndex(FrameOp op)
    {
        int validatedPassIndex = EnsureValidPassIndex(op.PassIndex, op.GetType().Name, op.Context.PassMetadata);
        if (validatedPassIndex == op.PassIndex)
            return op;

        return op switch
        {
            ClearOp clear => clear with { PassIndex = validatedPassIndex },
            MeshDrawOp meshDraw => meshDraw with { PassIndex = validatedPassIndex },
            BlitOp blit => blit with { PassIndex = validatedPassIndex },
            IndirectDrawOp indirectDraw => indirectDraw with { PassIndex = validatedPassIndex },
            MemoryBarrierOp memoryBarrier => memoryBarrier with { PassIndex = validatedPassIndex },
            ComputeDispatchOp computeDispatch => computeDispatch with { PassIndex = validatedPassIndex },
            _ => op
        };
    }

    internal FrameOp[] DrainFrameOps() => DrainFrameOps(out _);

    internal FrameOp[] DrainFrameOps(out ulong signature)
    {
        using (_frameOpsLock.EnterScope())
        {
            if (_frameOps.Count == 0)
            {
                signature = 0;
                return Array.Empty<FrameOp>();
            }

            var ops = _frameOps.ToArray();
            _frameOps.Clear();
            signature = ComputeFrameOpsSignature(ops);
            return ops;
        }
    }

    private static ulong ComputeFrameOpsSignature(FrameOp[] ops)
    {
        HashCode hash = new();
        hash.Add(ops.Length);

        for (int i = 0; i < ops.Length; i++)
        {
            FrameOp op = ops[i];
            hash.Add(op.GetType().Name, StringComparer.Ordinal);
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
                    hash.Add(meshDraw.Draw.ModelMatrix.GetHashCode());
                    hash.Add(meshDraw.Draw.PreviousModelMatrix.GetHashCode());
                    hash.Add(meshDraw.Draw.MaterialOverride?.GetHashCode() ?? 0);
                    hash.Add(meshDraw.Draw.Instances);
                    hash.Add((int)meshDraw.Draw.BillboardMode);
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
                    hash.Add(indirect.IndirectBuffer.GetHashCode());
                    hash.Add(indirect.ParameterBuffer?.GetHashCode() ?? 0);
                    hash.Add(indirect.DrawCount);
                    hash.Add(indirect.Stride);
                    hash.Add(indirect.ByteOffset);
                    hash.Add(indirect.UseCount);
                    break;
                case MemoryBarrierOp barrier:
                    hash.Add((int)barrier.Mask);
                    break;
                case ComputeDispatchOp compute:
                    hash.Add(compute.Program.GetHashCode());
                    hash.Add(compute.GroupsX);
                    hash.Add(compute.GroupsY);
                    hash.Add(compute.GroupsZ);
                    hash.Add(compute.Snapshot.Uniforms.Count);
                    hash.Add(compute.Snapshot.Samplers.Count);
                    hash.Add(compute.Snapshot.Images.Count);
                    hash.Add(compute.Snapshot.Buffers.Count);
                    break;
            }
        }

        return unchecked((ulong)hash.ToHashCode());
    }

    #endregion

    #region Draw State Snapshot

    internal readonly record struct PendingMeshDraw(
        VkMeshRenderer Renderer,
        Viewport Viewport,
        Rect2D Scissor,
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
        EMeshBillboardMode BillboardMode);

    private static bool ViewportEquals(in Viewport a, in Viewport b)
        => a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height && a.MinDepth == b.MinDepth && a.MaxDepth == b.MaxDepth;

    private static bool RectEquals(in Rect2D a, in Rect2D b)
        => a.Offset.X == b.Offset.X && a.Offset.Y == b.Offset.Y && a.Extent.Width == b.Extent.Width && a.Extent.Height == b.Extent.Height;

    #endregion

    public partial class VkMeshRenderer(VulkanRenderer api, XRMeshRenderer.BaseVersion data) : VkObject<XRMeshRenderer.BaseVersion>(api, data)
    {
        private readonly Dictionary<string, VkDataBuffer> _bufferCache = new(StringComparer.Ordinal);
        private VkDataBuffer? _triangleIndexBuffer;
        private VkDataBuffer? _lineIndexBuffer;
        private VkDataBuffer? _pointIndexBuffer;
        private IndexSize _triangleIndexSize;
        private IndexSize _lineIndexSize;
        private IndexSize _pointIndexSize;

        private readonly Dictionary<PipelineKey, Pipeline> _pipelines = new();
        private readonly record struct PipelineKey(
            PrimitiveTopology Topology,
            bool UseDynamicRendering,
            ulong RenderPassHandle,
            Format ColorAttachmentFormat,
            Format DepthAttachmentFormat,
            ulong ProgramPipelineHash,
            ulong VertexLayoutHash,
            bool DepthTestEnabled,
            bool DepthWriteEnabled,
            CompareOp DepthCompareOp,
            bool StencilTestEnabled,
            StencilOpState FrontStencilState,
            StencilOpState BackStencilState,
            uint StencilWriteMask,
            CullModeFlags CullMode,
            FrontFace FrontFace,
            bool BlendEnabled,
            BlendOp ColorBlendOp,
            BlendOp AlphaBlendOp,
            BlendFactor SrcColorBlendFactor,
            BlendFactor DstColorBlendFactor,
            BlendFactor SrcAlphaBlendFactor,
            BlendFactor DstAlphaBlendFactor,
            ColorComponentFlags ColorWriteMask);

        private VkRenderProgram? _program;
        private XRRenderProgram? _generatedProgram;
        private VertexInputBindingDescription[] _vertexBindings = [];
        private VertexInputAttributeDescription[] _vertexAttributes = [];
        private readonly Dictionary<uint, VkDataBuffer> _vertexBuffersByBinding = new();
        private bool _buffersDirty = true;
        private bool _pipelineDirty = true;
        private bool _meshDirty = true;
        private DescriptorPool _descriptorPool;
        private DescriptorSet[][]? _descriptorSets;
        private bool _descriptorDirty = true;
        private ulong _descriptorSchemaFingerprint;
        private readonly HashSet<string> _descriptorWarnings = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, EngineUniformBuffer[]> _engineUniformBuffers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _engineUniformWarnings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AutoUniformBuffer[]> _autoUniformBuffers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _autoUniformWarnings = new(StringComparer.Ordinal);
        private const string VertexUniformSuffix = "_VTX";

        private static bool IsStencilCapableFormat(Format format)
            => format is Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint;

        private readonly struct EngineUniformBuffer
        {
            public EngineUniformBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory, uint size)
            {
                Buffer = buffer;
                Memory = memory;
                Size = size;
            }

            public Silk.NET.Vulkan.Buffer Buffer { get; }
            public DeviceMemory Memory { get; }
            public uint Size { get; }
        }

        private readonly struct AutoUniformBuffer
        {
            public AutoUniformBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory, uint size)
            {
                Buffer = buffer;
                Memory = memory;
                Size = size;
            }

            public Silk.NET.Vulkan.Buffer Buffer { get; }
            public DeviceMemory Memory { get; }
            public uint Size { get; }
        }

        public XRMeshRenderer MeshRenderer => Data.Parent;
        public XRMesh? Mesh => MeshRenderer.Mesh;
        public override VkObjectType Type => VkObjectType.MeshRenderer;
        public override bool IsGenerated => true;

        protected override uint CreateObjectInternal() => CacheObject(this);

        protected override void DeleteObjectInternal()
        {
            DestroyPipelines();
            RemoveCachedObject(BindingId);
        }

        protected override void LinkData()
        {
            Data.RenderRequested += OnRenderRequested;
            MeshRenderer.PropertyChanged += OnMeshRendererPropertyChanged;

            if (Mesh is not null)
                Mesh.DataChanged += OnMeshChanged;

            CollectBuffers();
        }

        protected override void UnlinkData()
        {
            Data.RenderRequested -= OnRenderRequested;
            MeshRenderer.PropertyChanged -= OnMeshRendererPropertyChanged;

            if (Mesh is not null)
                Mesh.DataChanged -= OnMeshChanged;

            DestroyPipelines();
            _bufferCache.Clear();
            _triangleIndexBuffer = null;
            _lineIndexBuffer = null;
            _pointIndexBuffer = null;
        }

        private void OnMeshRendererPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(XRMeshRenderer.Mesh):
                    if (Mesh is not null)
                        Mesh.DataChanged -= OnMeshChanged;

                    if (MeshRenderer.Mesh is not null)
                        MeshRenderer.Mesh.DataChanged += OnMeshChanged;

                    _meshDirty = true;
                    _pipelineDirty = true;
                    _buffersDirty = true;
                    _descriptorDirty = true;
                    CollectBuffers();
                    break;
                case nameof(XRMeshRenderer.Material):
                    _pipelineDirty = true;
                    _descriptorDirty = true;
                    break;
            }
        }

        private void OnMeshChanged(XRMesh? mesh)
        {
            _meshDirty = true;
            _pipelineDirty = true;
            _buffersDirty = true;
            _descriptorDirty = true;
        }

        private void OnRenderRequested(Matrix4x4 modelMatrix, Matrix4x4 prevModelMatrix, XRMaterial? materialOverride, uint instances, EMeshBillboardMode billboardMode)
        {
            int passIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex;
            XRFrameBuffer? target = Renderer.GetCurrentDrawFrameBuffer();

            var draw = new PendingMeshDraw(
                this,
                Renderer.GetCurrentViewport(),
                Renderer.GetCurrentScissor(),
                Renderer.GetDepthTestEnabled(),
                Renderer.GetDepthWriteEnabled(),
                Renderer.GetDepthCompareOp(),
                Renderer.GetStencilTestEnabled(),
                Renderer.GetFrontStencilState(),
                Renderer.GetBackStencilState(),
                Renderer.GetStencilWriteMask(),
                Renderer.GetColorWriteMask(),
                Renderer.GetCullMode(),
                Renderer.GetFrontFace(),
                Renderer.GetBlendEnabled(),
                Renderer.GetColorBlendOp(),
                Renderer.GetAlphaBlendOp(),
                Renderer.GetSrcColorBlendFactor(),
                Renderer.GetDstColorBlendFactor(),
                Renderer.GetSrcAlphaBlendFactor(),
                Renderer.GetDstAlphaBlendFactor(),
                modelMatrix,
                prevModelMatrix,
                materialOverride,
                instances,
                billboardMode);

            Renderer.EnqueueFrameOp(new MeshDrawOp(
                Renderer.EnsureValidPassIndex(passIndex, "MeshDraw"),
                target,
                draw,
                Renderer.CaptureFrameOpContext()));
        }
    }
}

// Remaining VkMeshRenderer implementation lives in partial files:
// - VkMeshRenderer.Buffers.cs
// - VkMeshRenderer.Pipeline.cs
// - VkMeshRenderer.Drawing.cs
// - VkMeshRenderer.Descriptors.cs
// - VkMeshRenderer.Uniforms.cs
// - VkMeshRenderer.Cleanup.cs
