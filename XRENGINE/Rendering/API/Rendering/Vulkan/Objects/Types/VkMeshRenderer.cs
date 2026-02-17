// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.cs
//
// Vulkan mesh rendering implementation for the XR Engine. This file is part of
// the VulkanRenderer partial class and contains:
//
//   1. Frame operation record types (ClearOp, MeshDrawOp, BlitOp, etc.) used to
//      queue GPU work for deferred execution during frame submission.
//
//   2. The VkMeshRenderer inner class, which owns the full lifecycle of a single
//      mesh draw unit: buffer collection, shader program compilation, graphics
//      pipeline creation, descriptor set allocation, engine/auto uniform buffer
//      management, and final draw-command recording into Vulkan command buffers.
//
// Threading: Frame ops are enqueued from the game thread under _frameOpsLock and
// drained/recorded on the render thread. VkMeshRenderer methods are called
// exclusively on the render thread.
// ──────────────────────────────────────────────────────────────────────────────

// System namespaces
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

// Vulkan interop (Silk.NET)
using Silk.NET.Vulkan;

// Engine namespaces
using XREngine;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;

// Type alias to disambiguate Silk.NET Buffer from XREngine buffers
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
	// ══════════════════════════════════════════════════════════════════════════
	// SECTION: Frame Operation Queue
	//
	// Frame operations are the unit of deferred GPU work. Game-thread code
	// enqueues FrameOp records (clear, draw, blit, dispatch, etc.) and the
	// render thread drains them each frame to build Vulkan command buffers.
	// ══════════════════════════════════════════════════════════════════════════

	#region Frame Operation Queue

	/// <summary>Lock guarding concurrent access to the frame operation list.</summary>
	private readonly Lock _frameOpsLock = new();

	/// <summary>Pending frame operations awaiting drain and recording.</summary>
	private readonly List<FrameOp> _frameOps = [];

	// ── Frame Operation Record Types ────────────────────────────────────────

	/// <summary>Base record for all frame operations. Carries pass index, target framebuffer, and pipeline context.</summary>
	internal abstract record FrameOp(int PassIndex, XRFrameBuffer? Target, FrameOpContext Context);
	/// <summary>Clears color, depth, and/or stencil attachments within the specified rectangle.</summary>
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

	/// <summary>Draws a mesh with full pipeline state (depth, stencil, blend, cull, etc.).</summary>
	internal sealed record MeshDrawOp(int PassIndex, XRFrameBuffer? Target, PendingMeshDraw Draw, FrameOpContext Context) : FrameOp(PassIndex, Target, Context);

	/// <summary>Copies (blits) pixels between two framebuffers with optional filtering.</summary>
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

	/// <summary>Issues an indirect draw using GPU-driven parameters from a buffer.</summary>
	internal sealed record IndirectDrawOp(
		int PassIndex,
		VkDataBuffer IndirectBuffer,
		VkDataBuffer? ParameterBuffer,
		uint DrawCount,
		uint Stride,
		nuint ByteOffset,
		bool UseCount,
		FrameOpContext Context) : FrameOp(PassIndex, null, Context);

	/// <summary>Inserts a memory barrier to synchronize GPU memory access.</summary>
	internal sealed record MemoryBarrierOp(
		int PassIndex,
		EMemoryBarrierMask Mask,
		FrameOpContext Context) : FrameOp(PassIndex, null, Context);

	// ── Uniform / Resource Snapshot Types ──────────────────────────────────

	/// <summary>Captured value of a single shader uniform (type + boxed value + array flag).</summary>
	internal readonly record struct ProgramUniformValue(EShaderVarType Type, object Value, bool IsArray);

	/// <summary>Describes a single image binding for compute dispatch snapshotting.</summary>
	internal readonly record struct ProgramImageBinding(
		XRTexture Texture,
		int Level,
		bool Layered,
		int Layer,
		XRRenderProgram.EImageAccess Access,
		XRRenderProgram.EImageFormat Format);

	/// <summary>
	/// Frozen snapshot of all uniforms, samplers, images, and buffers bound at the
	/// time a compute dispatch is enqueued. This allows deferred execution on the
	/// render thread without referencing mutable material state.
	/// </summary>
	internal sealed record ComputeDispatchSnapshot(
		Dictionary<string, ProgramUniformValue> Uniforms,
		Dictionary<uint, XRTexture> Samplers,
		Dictionary<uint, ProgramImageBinding> Images,
		Dictionary<uint, XRDataBuffer> Buffers);

	/// <summary>Dispatches a compute shader with the given group dimensions and resource snapshot.</summary>
	internal sealed record ComputeDispatchOp(
		int PassIndex,
		VkRenderProgram Program,
		uint GroupsX,
		uint GroupsY,
		uint GroupsZ,
		ComputeDispatchSnapshot Snapshot,
		FrameOpContext Context) : FrameOp(PassIndex, null, Context);

	// ── Frame Operation Enqueue / Drain / Signature ────────────────────

	/// <summary>
	/// Enqueues a frame operation onto the pending list. Thread-safe.
	/// The pass index is validated/clamped before insertion.
	/// </summary>
	internal void EnqueueFrameOp(FrameOp op)
	{
		FrameOp validatedOp = EnsureValidFrameOpPassIndex(op);
		using (_frameOpsLock.EnterScope())
			_frameOps.Add(validatedOp);
	}

	/// <summary>
	/// Validates and corrects the pass index on a frame operation.
	/// Returns the original op if already valid, or a copy with the corrected index.
	/// </summary>
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

	/// <summary>Atomically drains all pending frame ops, returning them as an array.</summary>
	internal FrameOp[] DrainFrameOps()
	{
		return DrainFrameOps(out _);
	}

	/// <summary>
	/// Atomically drains all pending frame ops and computes a content-based
	/// signature hash that callers can use to detect frame-to-frame changes.
	/// </summary>
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

	/// <summary>
	/// Computes a deterministic hash over the full set of frame operations.
	/// Used to detect whether the render workload has changed between frames,
	/// enabling the renderer to skip redundant command buffer rebuilds.
	/// </summary>
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
					foreach (var pair in compute.Snapshot.Uniforms.OrderBy(static p => p.Key, StringComparer.Ordinal))
					{
						hash.Add(pair.Key, StringComparer.Ordinal);
						hash.Add((int)pair.Value.Type);
						hash.Add(pair.Value.IsArray);
						hash.Add(pair.Value.Value?.GetHashCode() ?? 0);
					}

					hash.Add(compute.Snapshot.Samplers.Count);
					foreach (var pair in compute.Snapshot.Samplers.OrderBy(static p => p.Key))
					{
						hash.Add(pair.Key);
						hash.Add(pair.Value?.GetHashCode() ?? 0);
					}

					hash.Add(compute.Snapshot.Images.Count);
					foreach (var pair in compute.Snapshot.Images.OrderBy(static p => p.Key))
					{
						hash.Add(pair.Key);
						hash.Add(pair.Value.Texture.GetHashCode());
						hash.Add(pair.Value.Level);
						hash.Add(pair.Value.Layered);
						hash.Add(pair.Value.Layer);
						hash.Add((int)pair.Value.Access);
						hash.Add((int)pair.Value.Format);
					}

					hash.Add(compute.Snapshot.Buffers.Count);
					foreach (var pair in compute.Snapshot.Buffers.OrderBy(static p => p.Key))
					{
						hash.Add(pair.Key);
						hash.Add(pair.Value?.GetHashCode() ?? 0);
					}
					break;
			}
		}

		return unchecked((ulong)hash.ToHashCode());
	}

	#endregion // Frame Operation Queue

	// ══════════════════════════════════════════════════════════════════════════
	// SECTION: Draw State Snapshot
	//
	// PendingMeshDraw captures the complete pipeline state required to record
	// a single mesh draw call. It is created on the game thread during render
	// request and consumed on the render thread during command buffer recording.
	// ══════════════════════════════════════════════════════════════════════════

	#region Draw State Snapshot

	/// <summary>
	/// Immutable snapshot of all state required to record a single mesh draw call:
	/// the VkMeshRenderer to draw, viewport/scissor rectangles, depth/stencil/blend
	/// configuration, model matrices, material override, and instancing parameters.
	/// </summary>
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

	/// <summary>Value-compares two Vulkan Viewport structs (no built-in equality).</summary>
	private static bool ViewportEquals(in Viewport a, in Viewport b)
		=> a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height && a.MinDepth == b.MinDepth && a.MaxDepth == b.MaxDepth;

	/// <summary>Value-compares two Vulkan Rect2D structs (no built-in equality).</summary>
	private static bool RectEquals(in Rect2D a, in Rect2D b)
		=> a.Offset.X == b.Offset.X && a.Offset.Y == b.Offset.Y && a.Extent.Width == b.Extent.Width && a.Extent.Height == b.Extent.Height;

	#endregion // Draw State Snapshot

	// ══════════════════════════════════════════════════════════════════════════
	// SECTION: VkMeshRenderer
	//
	// The primary Vulkan-side representation of an XRMeshRenderer. Manages
	// the complete rendering lifecycle: GPU buffer allocation, shader
	// compilation/linking, pipeline creation, descriptor set writes, engine
	// and auto uniform buffer uploads, and draw command recording.
	// ══════════════════════════════════════════════════════════════════════════

	/// <summary>
	/// Vulkan implementation for mesh rendering. This is intentionally conservative: it builds a basic
	/// graphics pipeline, binds mesh buffers, and emits draw commands into the Vulkan command buffer.
	/// </summary>
	public partial class VkMeshRenderer(VulkanRenderer api, XRMeshRenderer.BaseVersion data) : VkObject<XRMeshRenderer.BaseVersion>(api, data)
	{
		#region Fields & State

		// ── Buffer State ──────────────────────────────────────────────────────

		/// <summary>Named GPU data buffers collected from the mesh and mesh renderer.</summary>
		private readonly Dictionary<string, VkDataBuffer> _bufferCache = new(StringComparer.Ordinal);

		/// <summary>Index buffers for the three supported primitive types.</summary>
		private VkDataBuffer? _triangleIndexBuffer;
		private VkDataBuffer? _lineIndexBuffer;
		private VkDataBuffer? _pointIndexBuffer;

		/// <summary>Element size of each index buffer (Byte, TwoBytes, FourBytes).</summary>
		private IndexSize _triangleIndexSize;
		private IndexSize _lineIndexSize;
		private IndexSize _pointIndexSize;

		// ── Pipeline State ────────────────────────────────────────────────────

		/// <summary>Cache of compiled graphics pipelines keyed by their full state signature.</summary>
		private readonly Dictionary<PipelineKey, Pipeline> _pipelines = new();
		/// <summary>
		/// Composite key capturing every piece of state that affects Vulkan pipeline
		/// creation. Two draws with the same PipelineKey can share a pipeline object.
		/// </summary>
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

		// ── Shader Program State ──────────────────────────────────────────────

		/// <summary>Active Vulkan render program (compiled shaders + pipeline layout).</summary>
		private VkRenderProgram? _program;

		/// <summary>Engine-side program wrapper; kept alive so we can destroy it on recompile.</summary>
		private XRRenderProgram? _generatedProgram;

		// ── Vertex Input State ────────────────────────────────────────────────

		/// <summary>Vulkan vertex binding descriptions (stride, input rate) built from buffer cache.</summary>
		private VertexInputBindingDescription[] _vertexBindings = [];

		/// <summary>Vulkan vertex attribute descriptions (location, binding, format, offset).</summary>
		private VertexInputAttributeDescription[] _vertexAttributes = [];

		/// <summary>Maps binding index → VkDataBuffer for quick lookup during draw recording.</summary>
		private readonly Dictionary<uint, VkDataBuffer> _vertexBuffersByBinding = new();

		// ── Dirty Flags ───────────────────────────────────────────────────────
		/// <summary>True when GPU buffer handles need re-generation.</summary>
		private bool _buffersDirty = true;

		/// <summary>True when the graphics pipeline needs rebuild (material/mesh/state change).</summary>
		private bool _pipelineDirty = true;

		/// <summary>True when mesh geometry data has changed.</summary>
		private bool _meshDirty = true;

		// ── Descriptor State ─────────────────────────────────────────────────

		/// <summary>Vulkan descriptor pool backing all descriptor set allocations for this renderer.</summary>
		private DescriptorPool _descriptorPool;

		/// <summary>Per-swapchain-image descriptor sets: [frameIndex][setIndex].</summary>
		private DescriptorSet[][]? _descriptorSets;

		/// <summary>True when descriptor sets need to be reallocated or rewritten.</summary>
		private bool _descriptorDirty = true;

		/// <summary>Fingerprint of the descriptor layout schema; used to detect layout changes.</summary>
		private ulong _descriptorSchemaFingerprint;

		/// <summary>Tracks already-emitted descriptor warnings to avoid log spam.</summary>
		private readonly HashSet<string> _descriptorWarnings = new(StringComparer.OrdinalIgnoreCase);

		// ── Engine Uniform Buffer State ──────────────────────────────────────

		/// <summary>Per-frame host-visible UBOs for built-in engine uniforms (ModelMatrix, ViewMatrix, etc.).</summary>
		private readonly Dictionary<string, EngineUniformBuffer[]> _engineUniformBuffers = new(StringComparer.Ordinal);

		/// <summary>Tracks already-emitted engine uniform warnings.</summary>
		private readonly HashSet<string> _engineUniformWarnings = new(StringComparer.Ordinal);

		// ── Auto Uniform Buffer State ───────────────────────────────────────

		/// <summary>Per-frame host-visible UBOs for reflection-driven auto uniform blocks.</summary>
		private readonly Dictionary<string, AutoUniformBuffer[]> _autoUniformBuffers = new(StringComparer.Ordinal);

		/// <summary>Tracks already-emitted auto uniform warnings.</summary>
		private readonly HashSet<string> _autoUniformWarnings = new(StringComparer.Ordinal);

		/// <summary>Suffix appended to vertex-stage engine uniform names (stripped during resolution).</summary>
		private const string VertexUniformSuffix = "_VTX";

		/// <summary>Returns true if the given Vulkan format includes a stencil component.</summary>
		private static bool IsStencilCapableFormat(Format format)
			=> format is Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint;

		#endregion // Fields & State

		#region Nested Types

		/// <summary>
		/// Host-visible Vulkan buffer for a single engine uniform. One instance is
		/// allocated per swapchain frame to avoid write-after-read hazards.
		/// </summary>
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

		/// <summary>
		/// Host-visible Vulkan buffer for a single auto (reflection-driven) uniform
		/// block. Structurally identical to EngineUniformBuffer but semantically distinct.
		/// </summary>
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

		#endregion // Nested Types

		#region Properties

		/// <summary>The engine-side mesh renderer this Vulkan object wraps.</summary>
		public XRMeshRenderer MeshRenderer => Data.Parent;

		/// <summary>The mesh geometry currently assigned to the renderer (may be null).</summary>
		public XRMesh? Mesh => MeshRenderer.Mesh;

		/// <inheritdoc/>
		public override VkObjectType Type => VkObjectType.MeshRenderer;

		/// <inheritdoc/>
		public override bool IsGenerated => true;

		#endregion // Properties

		#region Object Lifecycle

		/// <summary>Registers this renderer in the API object cache.</summary>
		protected override uint CreateObjectInternal() => CacheObject(this);

		/// <summary>Tears down all pipelines and removes the object from the cache.</summary>
		protected override void DeleteObjectInternal()
		{
			DestroyPipelines();
			RemoveCachedObject(BindingId);
		}

		/// <summary>
		/// Subscribes to engine events (render requests, property changes, mesh data updates)
		/// and performs the initial buffer collection pass.
		/// </summary>
		protected override void LinkData()
		{
			Data.RenderRequested += OnRenderRequested;
			MeshRenderer.PropertyChanged += OnMeshRendererPropertyChanged;

			if (Mesh is not null)
				Mesh.DataChanged += OnMeshChanged;

			CollectBuffers();
		}

		/// <summary>
		/// Unsubscribes from all engine events and releases GPU resources
		/// (pipelines, buffers, index buffers).
		/// </summary>
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

		#endregion // Object Lifecycle

		#region Event Handlers

		/// <summary>
		/// Responds to property changes on the engine-side MeshRenderer.
		/// Mesh swaps trigger a full re-collection; material swaps invalidate
		/// the pipeline and descriptors.
		/// </summary>
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

		/// <summary>
		/// Called when the mesh's internal data changes (vertices, indices, etc.).
		/// Marks all caches dirty so they are rebuilt on the next draw.
		/// </summary>
		private void OnMeshChanged(XRMesh? mesh)
		{
			_meshDirty = true;
			_pipelineDirty = true;
			_buffersDirty = true;
			_descriptorDirty = true;
		}

		/// <summary>
		/// Called by the engine when a render is requested for this mesh.
		/// Captures the full pipeline state snapshot and enqueues a MeshDrawOp
		/// for deferred recording on the render thread.
		/// </summary>
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

		#endregion // Event Handlers
	}
}
// ── Remaining implementation is in partial class files: ──────────────────
// VkMeshRenderer.Buffers.cs      – Buffer Management & Material Resolution
// VkMeshRenderer.Pipeline.cs     – Shader Program, Vertex Input & Pipeline
// VkMeshRenderer.Drawing.cs      – Draw Command Recording
// VkMeshRenderer.Descriptors.cs  – Descriptor Set Management
// VkMeshRenderer.Uniforms.cs     – Uniform Buffer Allocation & Updates
// VkMeshRenderer.Cleanup.cs      – Resource Cleanup & Format Conversion
