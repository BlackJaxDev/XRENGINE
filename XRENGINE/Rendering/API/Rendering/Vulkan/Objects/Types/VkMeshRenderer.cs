using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using XREngine;
using XREngine.Data.Core;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
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
		MarkCommandBuffersDirty();
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

	internal FrameOp[] DrainFrameOps()
	{
		using (_frameOpsLock.EnterScope())
		{
			if (_frameOps.Count == 0)
				return Array.Empty<FrameOp>();
			var ops = _frameOps.ToArray();
			_frameOps.Clear();
			return ops;
		}
	}

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

	/// <summary>
	/// Vulkan implementation for mesh rendering. This is intentionally conservative: it builds a basic
	/// graphics pipeline, binds mesh buffers, and emits draw commands into the Vulkan command buffer.
	/// </summary>
	public class VkMeshRenderer(VulkanRenderer api, XRMeshRenderer.BaseVersion data) : VkObject<XRMeshRenderer.BaseVersion>(api, data)
	{
		private readonly Dictionary<string, VkDataBuffer> _bufferCache = new(StringComparer.OrdinalIgnoreCase);
		private VkDataBuffer? _triangleIndexBuffer;
		private VkDataBuffer? _lineIndexBuffer;
		private VkDataBuffer? _pointIndexBuffer;
		private IndexSize _triangleIndexSize;
		private IndexSize _lineIndexSize;
		private IndexSize _pointIndexSize;
		private readonly Dictionary<PipelineKey, Pipeline> _pipelines = new();
		private readonly record struct PipelineKey(
					PrimitiveTopology Topology,
					ulong RenderPassHandle,
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
		private EngineUniformBuffer _fallbackStorageBuffer;
		private const string VertexUniformSuffix = "_VTX";

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

		private void CollectBuffers()
		{
			_bufferCache.Clear();

			var meshBuffers = Mesh?.Buffers as IEventDictionary<string, XRDataBuffer>;
			if (meshBuffers is not null)
			{
				foreach (var pair in meshBuffers)
				{
					if (Renderer.GenericToAPI<VkDataBuffer>(pair.Value) is { } vkBuffer)
						_bufferCache[pair.Key] = vkBuffer;
				}
			}

			var rendererBuffers = MeshRenderer.Buffers as IEventDictionary<string, XRDataBuffer>;
			if (rendererBuffers is not null)
			{
				foreach (var pair in rendererBuffers)
				{
					if (Renderer.GenericToAPI<VkDataBuffer>(pair.Value) is { } vkBuffer)
						_bufferCache[pair.Key] = vkBuffer;
				}
			}

			_buffersDirty = true;
			_descriptorDirty = true;
		}

		private void EnsureBuffers()
		{
			if (!_buffersDirty)
				return;

			foreach (var buffer in _bufferCache.Values)
				buffer.Generate();

			if (Mesh is not null)
			{
				var tri = Mesh.GetIndexBuffer(EPrimitiveType.Triangles, out _triangleIndexSize);
				_triangleIndexBuffer = tri is not null ? Renderer.GenericToAPI<VkDataBuffer>(tri) : null;
				_triangleIndexBuffer?.Generate();

				var line = Mesh.GetIndexBuffer(EPrimitiveType.Lines, out _lineIndexSize);
				_lineIndexBuffer = line is not null ? Renderer.GenericToAPI<VkDataBuffer>(line) : null;
				_lineIndexBuffer?.Generate();

				var point = Mesh.GetIndexBuffer(EPrimitiveType.Points, out _pointIndexSize);
				_pointIndexBuffer = point is not null ? Renderer.GenericToAPI<VkDataBuffer>(point) : null;
				_pointIndexBuffer?.Generate();
			}

			_buffersDirty = false;
		}

		/// <summary>
		/// Overrides the triangle index buffer binding used for indexed draws.
		/// This is used by indirect-renderer atlas sync paths where indices are provided externally.
		/// </summary>
		internal void SetTriangleIndexBuffer(VkDataBuffer? buffer, IndexSize elementType)
		{
			_triangleIndexBuffer = buffer;
			_triangleIndexSize = elementType;
			_triangleIndexBuffer?.Generate();
		}

		/// <summary>
		/// Returns the first available index buffer in primitive priority order:
		/// triangles, then lines, then points.
		/// </summary>
		internal bool TryGetPrimaryIndexBufferInfo(out IndexSize indexElementSize, out uint indexCount)
		{
			EnsureBuffers();

			if (HasIndexData(_triangleIndexBuffer))
			{
				indexElementSize = _triangleIndexSize;
				indexCount = _triangleIndexBuffer!.Data.ElementCount;
				return true;
			}

			if (HasIndexData(_lineIndexBuffer))
			{
				indexElementSize = _lineIndexSize;
				indexCount = _lineIndexBuffer!.Data.ElementCount;
				return true;
			}

			if (HasIndexData(_pointIndexBuffer))
			{
				indexElementSize = _pointIndexSize;
				indexCount = _pointIndexBuffer!.Data.ElementCount;
				return true;
			}

			indexElementSize = IndexSize.FourBytes;
			indexCount = 0;
			return false;
		}

		internal bool TryGetPrimaryIndexBinding(out VkBufferHandle handle, out IndexType indexType, out uint indexCount)
		{
			EnsureBuffers();

			if (TryResolveIndexBinding(_triangleIndexBuffer, _triangleIndexSize, out handle, out indexType, out indexCount))
				return true;

			if (TryResolveIndexBinding(_lineIndexBuffer, _lineIndexSize, out handle, out indexType, out indexCount))
				return true;

			if (TryResolveIndexBinding(_pointIndexBuffer, _pointIndexSize, out handle, out indexType, out indexCount))
				return true;

			handle = default;
			indexType = IndexType.Uint32;
			indexCount = 0;
			return false;
		}

		private static bool TryResolveIndexBinding(VkDataBuffer? buffer, IndexSize size, out VkBufferHandle handle, out IndexType indexType, out uint indexCount)
		{
			handle = default;
			indexType = IndexType.Uint32;
			indexCount = 0;

			if (!HasIndexData(buffer))
				return false;

			buffer!.Generate();
			if (buffer.BufferHandle is not { } bufferHandle)
				return false;

			handle = bufferHandle;
			indexType = ToVkIndexType(size);
			indexCount = buffer.Data.ElementCount;
			return true;
		}

		private static bool HasIndexData(VkDataBuffer? buffer)
			=> buffer is not null && buffer.Data.ElementCount > 0;

		private XRMaterial ResolveMaterial(XRMaterial? localOverride)
		{
			var renderState = Engine.Rendering.State.RenderingPipelineState;
			var globalMaterialOverride = renderState?.GlobalMaterialOverride;
			var pipelineOverride = renderState?.OverrideMaterial;

			return (globalMaterialOverride
					?? pipelineOverride
					?? localOverride
					?? MeshRenderer.Material
					?? Engine.Rendering.State.CurrentRenderingPipeline?.InvalidMaterial
					?? XRMaterial.InvalidMaterial)
				   ?? new XRMaterial();
		}

		private bool EnsureProgram(XRMaterial material)
		{
			if (!_pipelineDirty && _program is not null)
				return true;

			var shaders = new List<XRShader>();
			bool hasVertex = false;

			foreach (var shader in material.Shaders)
			{
				if (shader is null)
					continue;
				shaders.Add(shader);
				hasVertex |= shader.Type == EShaderType.Vertex;
			}

			if (!hasVertex)
			{
				string? vsSource = Data.VertexShaderSource;
				if (string.IsNullOrWhiteSpace(vsSource))
				{
					Debug.RenderingWarningEvery(
						$"Vulkan.MeshRenderer.{GetHashCode()}.MissingVertexShader",
						TimeSpan.FromSeconds(2),
						"[Vulkan] MeshRenderer '{0}' cannot render: no vertex shader. Material='{1}' Mesh='{2}'",
						MeshRenderer?.Name ?? "<unnamed>",
						material?.Name ?? "<unnamed material>",
						Mesh?.Name ?? "<unnamed mesh>");
					return false;
				}

				shaders.Add(new XRShader(EShaderType.Vertex, vsSource));
			}

			_generatedProgram?.Destroy();
			_generatedProgram = new XRRenderProgram(linkNow: false, separable: false, shaders);
			_generatedProgram.AllowLink();
			_program = Renderer.GenericToAPI<VkRenderProgram>(_generatedProgram);

			if (_program is null)
			{
				Debug.VulkanWarningEvery(
					$"Vulkan.MeshRenderer.{GetHashCode()}.ProgramWrapperNull",
					TimeSpan.FromSeconds(2),
					"[Vulkan] MeshRenderer '{0}' cannot render: failed to create VkRenderProgram wrapper.",
					MeshRenderer?.Name ?? "<unnamed>");
				return false;
			}

			_program.Generate();
			bool linked = _program.Link();
			if (!linked)
			{
				Debug.VulkanWarningEvery(
					$"Vulkan.MeshRenderer.{GetHashCode()}.ProgramLinkFailed",
					TimeSpan.FromSeconds(2),
					"[Vulkan] MeshRenderer '{0}' program link failed. Program='{1}'",
					MeshRenderer?.Name ?? "<unnamed>",
					_generatedProgram?.Name ?? "<unnamed program>");
			}

			return linked;
		}

		private void BuildVertexInputState()
		{
			List<VertexInputBindingDescription> bindings = [];
			List<VertexInputAttributeDescription> attributes = [];
			_vertexBuffersByBinding.Clear();

			uint nextBinding = 0;
			uint nextLocation = 0;

			foreach (var buffer in _bufferCache.Values.OrderBy(b => b.Data.BindingIndexOverride ?? uint.MaxValue))
			{
				uint binding = buffer.Data.BindingIndexOverride ?? nextBinding++;
				bool interleaved = buffer.Data.InterleavedAttributes is { Length: > 0 };
				uint stride = interleaved && Mesh is not null ? Mesh.InterleavedStride : buffer.Data.ElementSize;

				bindings.Add(new VertexInputBindingDescription
				{
					Binding = binding,
					Stride = stride,
					InputRate = buffer.Data.InstanceDivisor > 0 ? VertexInputRate.Instance : VertexInputRate.Vertex
				});
				_vertexBuffersByBinding[binding] = buffer;

				if (interleaved)
				{
					foreach (var attr in buffer.Data.InterleavedAttributes)
					{
						uint location = attr.AttribIndexOverride ?? nextLocation++;
						attributes.Add(new VertexInputAttributeDescription
						{
							Location = location,
							Binding = binding,
							Format = ToFormat(attr.Type, attr.Count, attr.Integral),
							Offset = attr.Offset
						});
					}
				}
				else
				{
					uint location = nextLocation++;
					attributes.Add(new VertexInputAttributeDescription
					{
						Location = location,
						Binding = binding,
						Format = ToFormat(buffer.Data.ComponentType, buffer.Data.ComponentCount, buffer.Data.Integral),
						Offset = 0
					});
				}
			}

			_vertexBindings = [.. bindings];
			_vertexAttributes = [.. attributes];
		}

		private bool EnsurePipeline(XRMaterial material, PrimitiveTopology topology, in PendingMeshDraw draw, RenderPass renderPass, out Pipeline pipeline)
		{
			pipeline = default;

			if (_pipelineDirty)
				DestroyPipelines();

			_descriptorDirty = true; // Pipeline/program changes invalidate descriptor sets

			if (!EnsureProgram(material))
				return false;

			BuildVertexInputState();

			ulong programPipelineHash = _program!.ComputeGraphicsPipelineFingerprint();
			ulong vertexLayoutHash = ComputeVertexLayoutHash();

			PipelineKey key = new(
				topology,
				renderPass.Handle,
				programPipelineHash,
				vertexLayoutHash,
				draw.DepthTestEnabled,
				draw.DepthWriteEnabled,
				draw.DepthCompareOp,
				draw.StencilTestEnabled,
				draw.FrontStencilState,
				draw.BackStencilState,
				draw.StencilWriteMask,
				draw.CullMode,
				draw.FrontFace,
				draw.BlendEnabled,
				draw.ColorBlendOp,
				draw.AlphaBlendOp,
				draw.SrcColorBlendFactor,
				draw.DstColorBlendFactor,
				draw.SrcAlphaBlendFactor,
				draw.DstAlphaBlendFactor,
				draw.ColorWriteMask);

			if (_pipelines.TryGetValue(key, out pipeline) && pipeline.Handle != 0 && !_pipelineDirty)
			{
				Engine.Rendering.Stats.RecordVulkanPipelineCacheLookup(cacheHit: true);
				return true;
			}

			Engine.Rendering.Stats.RecordVulkanPipelineCacheLookup(cacheHit: false);

			var vertexInput = new PipelineVertexInputStateCreateInfo
			{
				SType = StructureType.PipelineVertexInputStateCreateInfo,
				VertexBindingDescriptionCount = (uint)_vertexBindings.Length,
				VertexAttributeDescriptionCount = (uint)_vertexAttributes.Length
			};

			bool success = false;

			fixed (VertexInputBindingDescription* bindingsPtr = _vertexBindings)
			fixed (VertexInputAttributeDescription* attrsPtr = _vertexAttributes)
			{
				vertexInput.PVertexBindingDescriptions = bindingsPtr;
				vertexInput.PVertexAttributeDescriptions = attrsPtr;

				PipelineInputAssemblyStateCreateInfo inputAssembly = new()
				{
					SType = StructureType.PipelineInputAssemblyStateCreateInfo,
					Topology = topology,
					PrimitiveRestartEnable = Vk.False,
				};

				PipelineViewportStateCreateInfo viewportState = new()
				{
					SType = StructureType.PipelineViewportStateCreateInfo,
					ViewportCount = 1,
					ScissorCount = 1,
				};

				PipelineRasterizationStateCreateInfo rasterizer = new()
				{
					SType = StructureType.PipelineRasterizationStateCreateInfo,
					DepthClampEnable = Vk.False,
					RasterizerDiscardEnable = Vk.False,
					PolygonMode = PolygonMode.Fill,
					CullMode = draw.CullMode,
					FrontFace = draw.FrontFace,
					DepthBiasEnable = Vk.False,
					LineWidth = 1.0f,
				};

				PipelineMultisampleStateCreateInfo multisampling = new()
				{
					SType = StructureType.PipelineMultisampleStateCreateInfo,
					RasterizationSamples = SampleCountFlags.Count1Bit,
					SampleShadingEnable = Vk.False,
				};

				PipelineDepthStencilStateCreateInfo depthStencil = new()
				{
					SType = StructureType.PipelineDepthStencilStateCreateInfo,
					DepthTestEnable = draw.DepthTestEnabled ? Vk.True : Vk.False,
					DepthWriteEnable = draw.DepthWriteEnabled ? Vk.True : Vk.False,
					DepthCompareOp = draw.DepthCompareOp,
					DepthBoundsTestEnable = Vk.False,
					StencilTestEnable = draw.StencilTestEnabled ? Vk.True : Vk.False,
					Front = draw.FrontStencilState,
					Back = draw.BackStencilState,
				};

				PipelineColorBlendAttachmentState colorBlendAttachment = new()
				{
					ColorWriteMask = draw.ColorWriteMask,
					BlendEnable = draw.BlendEnabled ? Vk.True : Vk.False,
					ColorBlendOp = draw.ColorBlendOp,
					AlphaBlendOp = draw.AlphaBlendOp,
					SrcColorBlendFactor = draw.SrcColorBlendFactor,
					DstColorBlendFactor = draw.DstColorBlendFactor,
					SrcAlphaBlendFactor = draw.SrcAlphaBlendFactor,
					DstAlphaBlendFactor = draw.DstAlphaBlendFactor,
				};

				uint colorAttachmentCount = Renderer.GetRenderPassColorAttachmentCount(renderPass);
				PipelineColorBlendAttachmentState[] blendAttachments = colorAttachmentCount == 0
					? Array.Empty<PipelineColorBlendAttachmentState>()
					: new PipelineColorBlendAttachmentState[colorAttachmentCount];

				for (int i = 0; i < blendAttachments.Length; i++)
					blendAttachments[i] = colorBlendAttachment;

				PipelineColorBlendStateCreateInfo colorBlending = new()
				{
					SType = StructureType.PipelineColorBlendStateCreateInfo,
					LogicOpEnable = Vk.False,
					LogicOp = LogicOp.Copy,
					AttachmentCount = (uint)blendAttachments.Length,
				};

				fixed (PipelineColorBlendAttachmentState* blendPtr = blendAttachments)
				{
					colorBlending.PAttachments = blendAttachments.Length > 0 ? blendPtr : null;

					DynamicState[] dynamicStates =
					[
						DynamicState.Viewport,
						DynamicState.Scissor,
					];

					fixed (DynamicState* dynPtr = dynamicStates)
					{
						PipelineDynamicStateCreateInfo dynamicState = new()
						{
							SType = StructureType.PipelineDynamicStateCreateInfo,
							DynamicStateCount = (uint)dynamicStates.Length,
							PDynamicStates = dynPtr,
						};

						GraphicsPipelineCreateInfo pipelineInfo = new()
						{
							SType = StructureType.GraphicsPipelineCreateInfo,
							PVertexInputState = &vertexInput,
							PInputAssemblyState = &inputAssembly,
							PViewportState = &viewportState,
							PRasterizationState = &rasterizer,
							PMultisampleState = &multisampling,
							PDepthStencilState = &depthStencil,
							PColorBlendState = &colorBlending,
							PDynamicState = &dynamicState,
								RenderPass = renderPass,
							Subpass = 0,
						};

							pipeline = _program!.CreateGraphicsPipeline(ref pipelineInfo, Renderer.ActivePipelineCache);
							_pipelines[key] = pipeline;
						_pipelineDirty = false;
						_meshDirty = false;
						success = pipeline.Handle != 0;
					}
				}
			}

			return success;
		}

		private void DestroyPipelines()
		{
			DestroyDescriptors();

			foreach (var pipe in _pipelines.Values)
			{
				if (pipe.Handle != 0)
					Api!.DestroyPipeline(Device, pipe, null);
			}

			_pipelines.Clear();
		}

		internal void RecordDraw(CommandBuffer commandBuffer, in PendingMeshDraw draw, RenderPass renderPass)
		{
			EnsureBuffers();

			var material = ResolveMaterial(draw.MaterialOverride);
			var drawCopy = draw; // copy to allow usage in lambdas/local functions
			uint drawInstances = draw.Instances;

			// Push skinning and blendshape data to GPU before drawing (matches OpenGL behavior)
			MeshRenderer.PushBoneMatricesToGPU();
			MeshRenderer.PushBlendshapeWeightsToGPU();

			bool uniformsNotified = false;

			bool DrawIndexed(VkDataBuffer? indexBuffer, IndexSize size, PrimitiveTopology topology, Action<uint> onStats)
			{
				if (indexBuffer?.BufferHandle is not { } indexHandle)
					return false;

				if (size == IndexSize.Byte && !Renderer.SupportsIndexTypeUint8)
				{
					WarnOnce("Skipping indexed draw using byte-sized indices because Vulkan indexTypeUint8 is not enabled.");
					return false;
				}

				uint indexCount = indexBuffer.Data.ElementCount;
				if (indexCount == 0)
					return false;

				if (!EnsurePipeline(material, topology, drawCopy, renderPass, out var pipeline))
					return false;

				Renderer.BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, pipeline);

				if (!BindVertexBuffersForCurrentPipeline(commandBuffer))
					return false;

				if (!uniformsNotified && _program?.Data is { } programData)
				{
					MeshRenderer.OnSettingUniforms(programData, programData);
					uniformsNotified = true;
				}

				if (!BindDescriptorsIfAvailable(commandBuffer, material, drawCopy))
					return false;

				Renderer.BindIndexBufferTracked(commandBuffer, indexHandle, 0, ToVkIndexType(size));
				Api!.CmdDrawIndexed(commandBuffer, indexCount, drawInstances, 0, 0, 0);

				Engine.Rendering.Stats.IncrementDrawCalls();
				onStats(indexCount);
				return true;
			}

			bool drew = false;
			drew |= DrawIndexed(_triangleIndexBuffer, _triangleIndexSize, PrimitiveTopology.TriangleList, count => Engine.Rendering.Stats.AddTrianglesRendered((int)(count / 3 * drawInstances)));
			drew |= DrawIndexed(_lineIndexBuffer, _lineIndexSize, PrimitiveTopology.LineList, _ => { });
			drew |= DrawIndexed(_pointIndexBuffer, _pointIndexSize, PrimitiveTopology.PointList, _ => { });

			if (!drew && Mesh is not null)
			{
				uint vertexCount = (uint)Math.Max(Mesh.VertexCount, 0);
				if (vertexCount > 0 && EnsurePipeline(material, PrimitiveTopology.TriangleList, drawCopy, renderPass, out var pipeline))
				{
					Renderer.BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, pipeline);

					if (!BindVertexBuffersForCurrentPipeline(commandBuffer))
						return;

					if (!uniformsNotified && _program?.Data is { } programData)
					{
						MeshRenderer.OnSettingUniforms(programData, programData);
						uniformsNotified = true;
					}

					if (!BindDescriptorsIfAvailable(commandBuffer, material, drawCopy))
						return;

					Api!.CmdDraw(commandBuffer, vertexCount, drawInstances, 0, 0);
					Engine.Rendering.Stats.IncrementDrawCalls();
					Engine.Rendering.Stats.AddTrianglesRendered((int)(vertexCount / 3 * drawInstances));
				}
			}
		}

		private bool BindVertexBuffersForCurrentPipeline(CommandBuffer commandBuffer)
		{
			if (_vertexBindings.Length == 0)
				return true;

            foreach (VertexInputBindingDescription binding in _vertexBindings.OrderBy(b => b.Binding))
			{
				if (!_vertexBuffersByBinding.TryGetValue(binding.Binding, out VkDataBuffer? sourceBuffer))
				{
					WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because vertex binding {binding.Binding} has no backing buffer.");
					return false;
				}

				if (!sourceBuffer.IsActive)
					sourceBuffer.Generate();
				if (sourceBuffer.BufferHandle is not { } handle || handle.Handle == 0)
				{
					WarnOnce($"Skipping draw for mesh '{Mesh?.Name ?? "UnnamedMesh"}' because vertex binding {binding.Binding} buffer is not allocated.");
					return false;
				}

				Renderer.BindVertexBuffersTracked(commandBuffer, binding.Binding, [handle], [0UL]);
			}

			return true;
		}

		private bool BindDescriptorsIfAvailable(CommandBuffer commandBuffer, XRMaterial material, in PendingMeshDraw draw)
		{
			if (_program is null)
				return true;

			bool requiresDescriptors = _program.DescriptorSetLayouts.Count > 0 && _program.DescriptorBindings.Count > 0;
			if (!requiresDescriptors)
				return true;

			int imageIndex = ResolveCommandBufferIndex(commandBuffer);
			if (imageIndex < 0)
				imageIndex = 0;

			if (Renderer.GetOrCreateAPIRenderObject(material, generateNow: true) is VkMaterial vkMaterial &&
				vkMaterial.TryBindDescriptorSets(commandBuffer, _program, imageIndex))
				return true;

			if (!EnsureDescriptorSets(material))
				return false;

			if (_descriptorSets is null || _descriptorSets.Length == 0)
				return false;

			if (imageIndex >= _descriptorSets.Length)
				imageIndex = 0;

			UpdateEngineUniformBuffersForDraw(imageIndex, draw);
			UpdateAutoUniformBuffersForDraw(imageIndex, material, draw);

			DescriptorSet[] sets = _descriptorSets[imageIndex];
			if (sets.Length == 0)
				return false;

			Renderer.BindDescriptorSetsTracked(commandBuffer, PipelineBindPoint.Graphics, _program.PipelineLayout, 0, sets);
			return true;
		}

		private ulong ComputeVertexLayoutHash()
		{
			HashCode hash = new();
			hash.Add(_vertexBindings.Length);
			hash.Add(_vertexAttributes.Length);

			for (int i = 0; i < _vertexBindings.Length; i++)
			{
				hash.Add(_vertexBindings[i].Binding);
				hash.Add(_vertexBindings[i].Stride);
				hash.Add((int)_vertexBindings[i].InputRate);
			}

			for (int i = 0; i < _vertexAttributes.Length; i++)
			{
				hash.Add(_vertexAttributes[i].Location);
				hash.Add(_vertexAttributes[i].Binding);
				hash.Add((int)_vertexAttributes[i].Format);
				hash.Add(_vertexAttributes[i].Offset);
			}

			return unchecked((ulong)hash.ToHashCode());
		}

		private int ResolveCommandBufferIndex(CommandBuffer commandBuffer)
		{
			if (Renderer._commandBuffers is null)
				return -1;

			for (int i = 0; i < Renderer._commandBuffers.Length; i++)
			{
				if (Renderer._commandBuffers[i].Handle == commandBuffer.Handle)
					return i;
			}

			return -1;
		}

		private bool EnsureDescriptorSets(XRMaterial material)
		{
			if (_program is null)
				return false;

			var layouts = _program.DescriptorSetLayouts;
			var bindings = _program.DescriptorBindings;
			if (layouts is null || layouts.Count == 0 || bindings.Count == 0)
			{
				_descriptorDirty = false;
				return true;
			}

			if (Renderer.swapChainImages is null || Renderer.swapChainImages.Length == 0)
				return false;

			int frameCount = Renderer.swapChainImages.Length;
			int setCount = layouts.Count;
			ulong schemaFingerprint = ComputeDescriptorSchemaFingerprint(bindings, setCount, frameCount);

			bool canReuseDescriptorAllocation =
				_descriptorSets is { Length: > 0 } &&
				_descriptorPool.Handle != 0 &&
				_descriptorSchemaFingerprint == schemaFingerprint &&
				_descriptorSets.Length == frameCount &&
				_descriptorSets.All(s => s.Length == setCount);

			if (!_descriptorDirty && canReuseDescriptorAllocation)
				return true;

			_descriptorWarnings.Clear();

			if (canReuseDescriptorAllocation)
			{
				for (int frame = 0; frame < frameCount; frame++)
				{
					if (!WriteDescriptorSets(_descriptorSets![frame], bindings, material, frame))
						return false;
				}

				_descriptorDirty = false;
				return true;
			}

			DestroyDescriptors();

			var poolSizes = BuildDescriptorPoolSizes(bindings, frameCount);
			if (poolSizes.Length == 0)
				return false;

			fixed (DescriptorPoolSize* poolSizesPtr = poolSizes)
			{
				DescriptorPoolCreateInfo poolInfo = new()
				{
					SType = StructureType.DescriptorPoolCreateInfo,
					Flags = _program.DescriptorSetsRequireUpdateAfterBind
						? DescriptorPoolCreateFlags.UpdateAfterBindBit
						: 0,
					PoolSizeCount = (uint)poolSizes.Length,
					PPoolSizes = poolSizesPtr,
					MaxSets = (uint)(setCount * frameCount),
				};

				if (Api!.CreateDescriptorPool(Device, ref poolInfo, null, out _descriptorPool) != Result.Success)
				{
					Debug.VulkanWarning("Failed to create Vulkan descriptor pool for mesh renderer.");
					return false;
				}
			}

			DescriptorSetLayout[] layoutArray = [.. layouts];
			_descriptorSets = new DescriptorSet[frameCount][];

			for (int frame = 0; frame < frameCount; frame++)
			{
				DescriptorSet[] frameSets = new DescriptorSet[layoutArray.Length];

				fixed (DescriptorSetLayout* layoutPtr = layoutArray)
				fixed (DescriptorSet* setPtr = frameSets)
				{
					DescriptorSetAllocateInfo allocInfo = new()
					{
						SType = StructureType.DescriptorSetAllocateInfo,
						DescriptorPool = _descriptorPool,
						DescriptorSetCount = (uint)layoutArray.Length,
						PSetLayouts = layoutPtr,
					};

					if (Api!.AllocateDescriptorSets(Device, ref allocInfo, setPtr) != Result.Success)
					{
						Debug.VulkanWarning("Failed to allocate Vulkan descriptor sets for mesh renderer.");
						return false;
					}
				}

				if (!WriteDescriptorSets(frameSets, bindings, material, frame))
					return false;

				_descriptorSets[frame] = frameSets;
			}

			_descriptorSchemaFingerprint = schemaFingerprint;
			_descriptorDirty = false;
			return true;
		}

		private static ulong ComputeDescriptorSchemaFingerprint(IReadOnlyList<DescriptorBindingInfo> bindings, int setCount, int frameCount)
		{
			HashCode hash = new();
			hash.Add(setCount);
			hash.Add(frameCount);

			foreach (DescriptorBindingInfo binding in bindings.OrderBy(b => b.Set).ThenBy(b => b.Binding))
			{
				hash.Add(binding.Set);
				hash.Add(binding.Binding);
				hash.Add((int)binding.DescriptorType);
				hash.Add(binding.Count);
				hash.Add((int)binding.StageFlags);
				hash.Add(binding.Name);
			}

			return unchecked((ulong)hash.ToHashCode());
		}

		private bool TryResolveBuffers(DescriptorBindingInfo binding, int frameIndex, uint descriptorCount, List<DescriptorBufferInfo> bufferInfos, out int bufferStart)
		{
			bufferStart = bufferInfos.Count;
			if (!TryResolveBuffer(binding, frameIndex, out DescriptorBufferInfo bufferInfo))
				return false;

			for (int i = 0; i < descriptorCount; i++)
				bufferInfos.Add(bufferInfo);

			return true;
		}

		private bool TryResolveImages(DescriptorBindingInfo binding, XRMaterial material, uint descriptorCount, List<DescriptorImageInfo> imageInfos, out int imageStart)
		{
			imageStart = imageInfos.Count;
			for (int i = 0; i < descriptorCount; i++)
			{
				if (!TryResolveImage(binding, material, binding.DescriptorType, out DescriptorImageInfo info, i))
					return false;

				imageInfos.Add(info);
			}

			return true;
		}

		private bool TryResolveTexelBuffers(DescriptorBindingInfo binding, XRMaterial material, uint descriptorCount, List<BufferView> texelBufferViews, out int texelStart)
		{
			texelStart = texelBufferViews.Count;
			for (int i = 0; i < descriptorCount; i++)
			{
				if (!TryResolveTexelBuffer(binding, material, out BufferView view, i))
					return false;

				texelBufferViews.Add(view);
			}

			return true;
		}

		private static DescriptorPoolSize[] BuildDescriptorPoolSizes(IReadOnlyList<DescriptorBindingInfo> bindings, int frameCount)
		{
			Dictionary<DescriptorType, uint> counts = [];
			foreach (DescriptorBindingInfo binding in bindings)
			{
				uint count = Math.Max(binding.Count, 1u) * (uint)frameCount;
				if (counts.TryGetValue(binding.DescriptorType, out uint existing))
					counts[binding.DescriptorType] = existing + count;
				else
					counts[binding.DescriptorType] = count;
			}

			DescriptorPoolSize[] poolSizes = new DescriptorPoolSize[counts.Count];
			int i = 0;
			foreach (var pair in counts)
				poolSizes[i++] = new DescriptorPoolSize { Type = pair.Key, DescriptorCount = pair.Value };

			return poolSizes;
		}

		private bool WriteDescriptorSets(DescriptorSet[] frameSets, IReadOnlyList<DescriptorBindingInfo> bindings, XRMaterial material, int frameIndex)
		{
			List<WriteDescriptorSet> writes = [];
			List<DescriptorBufferInfo> bufferInfos = [];
			List<DescriptorImageInfo> imageInfos = [];
			List<BufferView> texelBufferViews = [];
			List<(int writeIndex, int bufferIndex)> bufferMap = [];
			List<(int writeIndex, int imageIndex)> imageMap = [];
			List<(int writeIndex, int texelIndex)> texelMap = [];

			foreach (DescriptorBindingInfo binding in bindings)
			{
				if (binding.Set >= frameSets.Length)
				{
					WarnOnce($"Descriptor set {binding.Set} is not available for pipeline layout.");
					return false;
				}

				uint descriptorCount = Math.Max(binding.Count, 1u);

				switch (binding.DescriptorType)
				{
					case DescriptorType.UniformBuffer:
					case DescriptorType.StorageBuffer:
						if (!TryResolveBuffers(binding, frameIndex, descriptorCount, bufferInfos, out int bufferStart))
							return false;

						bufferMap.Add((writes.Count, bufferStart));
						writes.Add(new WriteDescriptorSet
						{
							SType = StructureType.WriteDescriptorSet,
							DstSet = frameSets[binding.Set],
							DstBinding = binding.Binding,
							DescriptorCount = descriptorCount,
							DescriptorType = binding.DescriptorType,
						});
						break;

					case DescriptorType.CombinedImageSampler:
					case DescriptorType.SampledImage:
					case DescriptorType.Sampler:
					case DescriptorType.StorageImage:
						if (!TryResolveImages(binding, material, descriptorCount, imageInfos, out int imageStart))
							return false;

						imageMap.Add((writes.Count, imageStart));
						writes.Add(new WriteDescriptorSet
						{
							SType = StructureType.WriteDescriptorSet,
							DstSet = frameSets[binding.Set],
							DstBinding = binding.Binding,
							DescriptorCount = descriptorCount,
							DescriptorType = binding.DescriptorType,
						});
						break;

					case DescriptorType.UniformTexelBuffer:
					case DescriptorType.StorageTexelBuffer:
						if (!TryResolveTexelBuffers(binding, material, descriptorCount, texelBufferViews, out int texelStart))
							return false;

						texelMap.Add((writes.Count, texelStart));
						writes.Add(new WriteDescriptorSet
						{
							SType = StructureType.WriteDescriptorSet,
							DstSet = frameSets[binding.Set],
							DstBinding = binding.Binding,
							DescriptorCount = descriptorCount,
							DescriptorType = binding.DescriptorType,
						});
						break;

					default:
						WarnOnce($"Unsupported descriptor type '{binding.DescriptorType}' for binding '{binding.Name}'.");
						return false;
				}
			}

			DescriptorBufferInfo[] bufferArray = bufferInfos.Count == 0 ? [] : [.. bufferInfos];
			DescriptorImageInfo[] imageArray = imageInfos.Count == 0 ? [] : [.. imageInfos];
			BufferView[] texelArray = texelBufferViews.Count == 0 ? [] : [.. texelBufferViews];
			WriteDescriptorSet[] writeArray = writes.Count == 0 ? [] : [.. writes];

			fixed (DescriptorBufferInfo* bufferPtr = bufferArray)
			fixed (DescriptorImageInfo* imagePtr = imageArray)
			fixed (BufferView* texelPtr = texelArray)
			fixed (WriteDescriptorSet* writePtr = writeArray)
			{
				foreach (var (writeIndex, bufferIndex) in bufferMap)
					writePtr[writeIndex].PBufferInfo = bufferPtr + bufferIndex;

				foreach (var (writeIndex, imageIndex) in imageMap)
					writePtr[writeIndex].PImageInfo = imagePtr + imageIndex;

				foreach (var (writeIndex, texelIndex) in texelMap)
					writePtr[writeIndex].PTexelBufferView = texelPtr + texelIndex;

				if (writeArray.Length > 0)
					Api!.UpdateDescriptorSets(Device, (uint)writeArray.Length, writePtr, 0, null);
			}

			return true;
		}

		private bool TryResolveBuffer(DescriptorBindingInfo binding, int frameIndex, out DescriptorBufferInfo bufferInfo)
		{
			bufferInfo = default;

			VkDataBuffer? buffer = null;
			if (!string.IsNullOrWhiteSpace(binding.Name))
			{
				if (_bufferCache.TryGetValue(binding.Name, out buffer))
				{
					// found by name
				}
				else
				{
					string normalizedName = NormalizeEngineUniformName(binding.Name);
					if (!string.Equals(normalizedName, binding.Name, StringComparison.Ordinal) && _bufferCache.TryGetValue(normalizedName, out buffer))
					{
						// found by normalized name
					}
				}
			}

			if (buffer is null)
				buffer = TryResolveBufferByBindingSlot(binding.Binding, binding.DescriptorType, requireCompatibleTarget: true);

			if (buffer is null)
			{
				buffer = TryResolveBufferByBindingSlot(binding.Binding, binding.DescriptorType, requireCompatibleTarget: false);
				if (buffer is not null)
				{
					WarnOnce($"Using buffer '{buffer.Data.AttributeName}' for descriptor set {binding.Set}, binding {binding.Binding} despite non-standard target '{buffer.Data.Target}'.");
				}
			}

			buffer ??= binding.DescriptorType switch
			{
				DescriptorType.UniformBuffer => _bufferCache.Values.FirstOrDefault(b => b.Data.Target == EBufferTarget.UniformBuffer),
				DescriptorType.StorageBuffer => _bufferCache.Values.FirstOrDefault(b => b.Data.Target == EBufferTarget.ShaderStorageBuffer),
				_ => _bufferCache.Values.FirstOrDefault(b => b.Data.Target is EBufferTarget.UniformBuffer or EBufferTarget.ShaderStorageBuffer),
			};

			if (buffer is null)
			{
				if (binding.DescriptorType == DescriptorType.UniformBuffer)
				{
					if (TryResolveAutoUniformBuffer(binding, frameIndex, out bufferInfo))
						return true;

					return TryResolveEngineUniformBuffer(binding, frameIndex, out bufferInfo);
				}

				if (binding.DescriptorType == DescriptorType.StorageBuffer && TryResolveFallbackStorageBuffer(out bufferInfo))
					return true;

				WarnOnce($"No storage buffer available for descriptor binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}).");
				return false;
			}

			buffer.Generate();
			if (buffer.BufferHandle is not { } bufferHandle || bufferHandle.Handle == 0)
			{
				WarnOnce($"Buffer for descriptor binding '{binding.Name}' is not allocated.");
				return false;
			}

			bufferInfo = new DescriptorBufferInfo
			{
				Buffer = bufferHandle,
				Offset = 0,
				Range = Math.Max(buffer.Data.Length, 1u),
			};

			return true;
		}

		private bool TryResolveFallbackStorageBuffer(out DescriptorBufferInfo bufferInfo)
		{
			bufferInfo = default;

			if (_fallbackStorageBuffer.Buffer.Handle == 0)
			{
				const uint fallbackSize = 16u;
				if (!CreateHostVisibleBuffer(fallbackSize, BufferUsageFlags.StorageBufferBit, out Silk.NET.Vulkan.Buffer buffer, out DeviceMemory memory))
					return false;

				_fallbackStorageBuffer = new EngineUniformBuffer(buffer, memory, fallbackSize);
			}

			bufferInfo = new DescriptorBufferInfo
			{
				Buffer = _fallbackStorageBuffer.Buffer,
				Offset = 0,
				Range = _fallbackStorageBuffer.Size,
			};

			return true;
		}

		private VkDataBuffer? TryResolveBufferByBindingSlot(uint bindingSlot, DescriptorType descriptorType, bool requireCompatibleTarget)
		{
			foreach (VkDataBuffer candidate in _bufferCache.Values)
			{
				if (candidate.Data.BindingIndexOverride != bindingSlot)
					continue;

				if (requireCompatibleTarget && !IsCompatibleBufferTarget(candidate.Data.Target, descriptorType))
					continue;

				return candidate;
			}

			return null;
		}

		private static bool IsCompatibleBufferTarget(EBufferTarget target, DescriptorType descriptorType)
			=> descriptorType switch
			{
				DescriptorType.UniformBuffer => target is EBufferTarget.UniformBuffer or EBufferTarget.ParameterBuffer,
				DescriptorType.StorageBuffer => target is EBufferTarget.ShaderStorageBuffer or EBufferTarget.AtomicCounterBuffer or EBufferTarget.TransformFeedbackBuffer,
				_ => target is EBufferTarget.UniformBuffer or EBufferTarget.ShaderStorageBuffer,
			};

		private bool TryResolveImage(DescriptorBindingInfo binding, XRMaterial material, DescriptorType descriptorType, out DescriptorImageInfo imageInfo, int arrayIndex = 0)
		{
			imageInfo = default;
			int preferredIndex = (int)binding.Binding + arrayIndex;
			if (!TryResolveCompatibleImageSource(material, descriptorType, preferredIndex, out IVkImageDescriptorSource? source))
			{
				string requirement = descriptorType == DescriptorType.StorageImage ? "storage" : "sampled";
				WarnOnce($"No compatible {requirement} texture available for descriptor binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}).");
				return false;
			}

			bool requiresSampledUsage = descriptorType is DescriptorType.CombinedImageSampler or DescriptorType.SampledImage;
			if (requiresSampledUsage && (source.DescriptorUsage & ImageUsageFlags.SampledBit) == 0)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' is missing VK_IMAGE_USAGE_SAMPLED_BIT.");
				return false;
			}

			if (descriptorType == DescriptorType.StorageImage && (source.DescriptorUsage & ImageUsageFlags.StorageBit) == 0)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' is missing VK_IMAGE_USAGE_STORAGE_BIT.");
				return false;
			}

			imageInfo = new DescriptorImageInfo
			{
				ImageLayout = descriptorType == DescriptorType.StorageImage ? ImageLayout.General : ImageLayout.ShaderReadOnlyOptimal,
				ImageView = source.DescriptorView,
				Sampler = descriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler ? source.DescriptorSampler : default,
			};

			if (descriptorType == DescriptorType.Sampler)
				return imageInfo.Sampler.Handle != 0;

			return imageInfo.ImageView.Handle != 0;
		}

		private bool TryResolveCompatibleImageSource(XRMaterial material, DescriptorType descriptorType, int preferredIndex, [NotNullWhen(true)] out IVkImageDescriptorSource? source)
		{
			source = null;
			if (material.Textures is not { Count: > 0 })
				return false;

			if (preferredIndex >= 0 && preferredIndex < material.Textures.Count &&
				TryResolveImageSource(material.Textures[preferredIndex], descriptorType, out source))
			{
				return true;
			}

			foreach (XRTexture? texture in material.Textures)
			{
				if (TryResolveImageSource(texture, descriptorType, out source))
					return true;
			}

			return false;
		}

		private bool TryResolveImageSource(XRTexture? texture, DescriptorType descriptorType, [NotNullWhen(true)] out IVkImageDescriptorSource? source)
		{
			source = null;
			if (texture is null)
				return false;

			if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkImageDescriptorSource candidate)
				return false;

			if (descriptorType == DescriptorType.StorageImage)
			{
				if ((candidate.DescriptorUsage & ImageUsageFlags.StorageBit) == 0)
					return false;
			}
			else if (descriptorType is DescriptorType.CombinedImageSampler or DescriptorType.SampledImage)
			{
				if ((candidate.DescriptorUsage & ImageUsageFlags.SampledBit) == 0)
					return false;
			}

			source = candidate;
			return true;
		}

		private bool TryResolveTexelBuffer(DescriptorBindingInfo binding, XRMaterial material, out BufferView texelView, int arrayIndex = 0)
		{
			texelView = default;
			XRTexture? texture = null;

			if (material.Textures is { Count: > 0 })
			{
				int idx = (int)binding.Binding + arrayIndex;
				if (idx >= 0 && idx < material.Textures.Count)
					texture = material.Textures[idx];

				texture ??= material.Textures.FirstOrDefault(t => t is not null);
			}

			if (texture is null)
			{
				WarnOnce($"No texture available for texel descriptor binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}).");
				return false;
			}

			if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkTexelBufferDescriptorSource source)
			{
				WarnOnce($"Texture for texel descriptor binding '{binding.Name}' is not a Vulkan texel-buffer texture.");
				return false;
			}

			texelView = source.DescriptorBufferView;
			return texelView.Handle != 0;
		}

			private bool TryResolveEngineUniformBuffer(DescriptorBindingInfo binding, int frameIndex, out DescriptorBufferInfo bufferInfo)
			{
				bufferInfo = default;
				string name = binding.Name ?? string.Empty;
				if (string.IsNullOrWhiteSpace(name))
				{
					if (TryResolveAutoUniformBuffer(binding, frameIndex, out bufferInfo))
						return true;

					WarnOnce($"Descriptor binding at set {binding.Set}, binding {binding.Binding} has no reflected name and could not be resolved as an auto uniform block.");
					return false;
				}

				uint size = GetEngineUniformSize(name);
				if (size == 0)
				{
					WarnOnce($"Descriptor binding '{name}' could not be matched to an engine uniform.");
					return false;
				}

				if (!EnsureEngineUniformBuffer(name, size))
					return false;

				if (!_engineUniformBuffers.TryGetValue(name, out EngineUniformBuffer[]? buffers) || buffers.Length == 0)
					return false;

				int idx = Math.Clamp(frameIndex, 0, buffers.Length - 1);
				EngineUniformBuffer target = buffers[idx];
				if (target.Buffer.Handle == 0)
					return false;

				bufferInfo = new DescriptorBufferInfo
				{
					Buffer = target.Buffer,
					Offset = 0,
					Range = size,
				};

				return true;
			}

			private bool TryResolveAutoUniformBuffer(DescriptorBindingInfo binding, int frameIndex, out DescriptorBufferInfo bufferInfo)
			{
				bufferInfo = default;
				if (_program is null)
					return false;

				AutoUniformBlockInfo? block = null;
				if (!string.IsNullOrWhiteSpace(binding.Name) && _program.TryGetAutoUniformBlock(binding.Name, out AutoUniformBlockInfo namedBlock))
					block = namedBlock;

				block ??= _program.AutoUniformBlocks.Values.FirstOrDefault(x => x.Set == binding.Set && x.Binding == binding.Binding);
				if (block is null)
					return false;

				uint size = Math.Max(block.Size, 1u);
				if (!EnsureAutoUniformBuffer(block.InstanceName, size))
					return false;

				if (!_autoUniformBuffers.TryGetValue(block.InstanceName, out AutoUniformBuffer[]? buffers) || buffers.Length == 0)
					return false;

				int idx = Math.Clamp(frameIndex, 0, buffers.Length - 1);
				AutoUniformBuffer target = buffers[idx];
				if (target.Buffer.Handle == 0)
					return false;

				bufferInfo = new DescriptorBufferInfo
				{
					Buffer = target.Buffer,
					Offset = 0,
					Range = size,
				};

				return true;
			}

			private bool EnsureEngineUniformBuffer(string name, uint size)
			{
				int frames = Renderer.swapChainImages?.Length ?? 1;
				if (_engineUniformBuffers.TryGetValue(name, out EngineUniformBuffer[]? existing))
				{
					bool valid = existing.Length == frames && existing.All(e => e.Buffer.Handle != 0 && e.Size >= size);
					if (valid)
						return true;

					DestroyEngineUniformBuffers(name);
				}

				EngineUniformBuffer[] buffers = new EngineUniformBuffer[frames];
				for (int i = 0; i < frames; i++)
				{
					if (!CreateHostVisibleBuffer(size, BufferUsageFlags.UniformBufferBit, out var buffer, out var memory))
						return false;

					buffers[i] = new EngineUniformBuffer(buffer, memory, size);
				}

				_engineUniformBuffers[name] = buffers;
				return true;
			}

			private bool EnsureAutoUniformBuffer(string name, uint size)
			{
				int frames = Renderer.swapChainImages?.Length ?? 1;
				if (_autoUniformBuffers.TryGetValue(name, out AutoUniformBuffer[]? existing))
				{
					bool valid = existing.Length == frames && existing.All(e => e.Buffer.Handle != 0 && e.Size >= size);
					if (valid)
						return true;

					DestroyAutoUniformBuffers(name);
				}

				AutoUniformBuffer[] buffers = new AutoUniformBuffer[frames];
				for (int i = 0; i < frames; i++)
				{
					if (!CreateHostVisibleBuffer(size, BufferUsageFlags.UniformBufferBit, out var buffer, out var memory))
						return false;

					buffers[i] = new AutoUniformBuffer(buffer, memory, size);
				}

				_autoUniformBuffers[name] = buffers;
				return true;
			}

			private bool CreateHostVisibleBuffer(uint size, BufferUsageFlags usage, out Silk.NET.Vulkan.Buffer buffer, out DeviceMemory memory)
			{
				buffer = default;
				memory = default;
				size = Math.Max(size, 1u);

				BufferCreateInfo bufferInfo = new()
				{
					SType = StructureType.BufferCreateInfo,
					Size = size,
					Usage = usage,
					SharingMode = SharingMode.Exclusive,
				};

				if (Api!.CreateBuffer(Device, ref bufferInfo, null, out buffer) != Result.Success)
				{
					WarnOnce($"Failed to create engine uniform buffer '{size}' bytes.");
					return false;
				}

				Api.GetBufferMemoryRequirements(Device, buffer, out MemoryRequirements memReqs);

				MemoryAllocateInfo allocInfo = new()
				{
					SType = StructureType.MemoryAllocateInfo,
					AllocationSize = memReqs.Size,
					MemoryTypeIndex = Renderer.FindMemoryType(memReqs.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
				};

				if (Api.AllocateMemory(Device, ref allocInfo, null, out memory) != Result.Success)
				{
					Api.DestroyBuffer(Device, buffer, null);
					WarnOnce("Failed to allocate memory for engine uniform buffer.");
					buffer = default;
					return false;
				}

				Api.BindBufferMemory(Device, buffer, memory, 0);
				return true;
			}

			private void UpdateEngineUniformBuffersForDraw(int frameIndex, in PendingMeshDraw draw)
			{
				if (_engineUniformBuffers.Count == 0)
					return;

				foreach (var pair in _engineUniformBuffers)
				{
					EngineUniformBuffer[] buffers = pair.Value;
					if (buffers.Length == 0)
						continue;

					int idx = Math.Clamp(frameIndex, 0, buffers.Length - 1);
					EngineUniformBuffer buffer = buffers[idx];
					if (buffer.Buffer.Handle == 0)
						continue;

					TryWriteEngineUniform(pair.Key, draw, buffer);
				}
			}

			private void UpdateAutoUniformBuffersForDraw(int frameIndex, XRMaterial material, in PendingMeshDraw draw)
			{
				if (_program is null || _autoUniformBuffers.Count == 0)
					return;

				foreach (var pair in _program.AutoUniformBlocks)
				{
					string name = pair.Key;
					AutoUniformBlockInfo block = pair.Value;
					if (!_autoUniformBuffers.TryGetValue(name, out AutoUniformBuffer[]? buffers) || buffers.Length == 0)
						continue;

					int idx = Math.Clamp(frameIndex, 0, buffers.Length - 1);
					AutoUniformBuffer buffer = buffers[idx];
					if (buffer.Buffer.Handle == 0)
						continue;

					TryWriteAutoUniformBlock(block, buffer, material, draw);
				}
			}

			private bool TryWriteAutoUniformBlock(AutoUniformBlockInfo block, AutoUniformBuffer buffer, XRMaterial material, in PendingMeshDraw draw)
			{
				void* mapped;
				if (Api!.MapMemory(Device, buffer.Memory, 0, buffer.Size, 0, &mapped) != Result.Success)
					return false;

				try
				{
					Span<byte> data = new(mapped, (int)buffer.Size);
					data.Clear();

					foreach (AutoUniformMember member in block.Members)
					{
						if (member.Offset + member.Size > buffer.Size)
							continue;

						if (TryWriteAutoUniformMember(data, member, material, draw))
							continue;
					}
				}
				finally
				{
					Api.UnmapMemory(Device, buffer.Memory);
				}

				return true;
			}

			private bool TryWriteAutoUniformMember(Span<byte> data, AutoUniformMember member, XRMaterial material, in PendingMeshDraw draw)
			{
				if (member.EngineType is null)
					return false;

				if (TryResolveEngineUniformValue(member.Name, draw, out object? engineValue, out EShaderVarType engineType))
					return engineValue is not null && TryWriteAutoUniformValue(data, member, engineValue, engineType);

				if (_program is not null && _program.TryGetUniformValue(member.Name, out ProgramUniformValue programValue))
					return TryWriteProgramUniformValue(data, member, programValue);

				ShaderVar? parameter = material.Parameter<ShaderVar>(member.Name);
				if (parameter is not null)
				{
					if (member.IsArray)
						return TryWriteAutoUniformArray(data, member, parameter);

					return TryWriteAutoUniformValue(data, member, parameter.GenericValue, parameter.TypeName);
				}

				if (member.IsArray && member.DefaultArrayValues is { Count: > 0 })
					return TryWriteAutoUniformArrayDefaults(data, member);

				if (member.DefaultValue is { } defaultValue)
					return TryWriteAutoUniformValue(data, member, defaultValue.Value, defaultValue.Type);

				return false;
			}

			private bool TryWriteProgramUniformValue(Span<byte> data, AutoUniformMember member, ProgramUniformValue value)
			{
				if (member.IsArray)
				{
					if (!value.IsArray || value.Value is not Array array || member.ArrayStride == 0 || member.ArrayLength == 0)
						return false;

					int max = Math.Min(array.Length, (int)member.ArrayLength);
					for (int i = 0; i < max; i++)
					{
						object? element = array.GetValue(i);
						if (element is null)
							continue;

						uint elementOffset = member.Offset + (uint)i * member.ArrayStride;
						AutoUniformMember elementMember = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
                    TryWriteAutoUniformValue(data, elementMember, element, value.Type);
					}

					return true;
				}

				return TryWriteAutoUniformValue(data, member, value.Value, value.Type);
			}

			private bool TryWriteAutoUniformArray(Span<byte> data, AutoUniformMember member, ShaderVar parameter)
			{
				if (!member.IsArray || member.ArrayStride == 0 || member.ArrayLength == 0)
					return false;

				if (parameter.GenericValue is not IUniformableArray array)
					return false;

				var valuesProp = array.GetType().GetProperty("Values");
				if (valuesProp?.GetValue(array) is not Array values)
					return false;

				uint stride = member.ArrayStride;
				uint baseOffset = member.Offset;
				int max = (int)Math.Min((uint)values.Length, member.ArrayLength);

				for (int i = 0; i < max; i++)
				{
					if (values.GetValue(i) is not ShaderVar element)
						continue;

					uint elementOffset = baseOffset + (uint)i * stride;
					AutoUniformMember elementMember = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
					TryWriteAutoUniformValue(data, elementMember, element.GenericValue, element.TypeName);
				}

				return true;
			}

			private bool TryWriteAutoUniformArrayDefaults(Span<byte> data, AutoUniformMember member)
			{
				if (!member.IsArray || member.ArrayStride == 0 || member.ArrayLength == 0)
					return false;

				if (member.DefaultArrayValues is null || member.DefaultArrayValues.Count == 0)
					return false;

				uint stride = member.ArrayStride;
				uint baseOffset = member.Offset;
				int max = (int)Math.Min((uint)member.DefaultArrayValues.Count, member.ArrayLength);

				for (int i = 0; i < max; i++)
				{
					AutoUniformDefaultValue def = member.DefaultArrayValues[i];
					uint elementOffset = baseOffset + (uint)i * stride;
					AutoUniformMember elementMember = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
					TryWriteAutoUniformValue(data, elementMember, def.Value, def.Type);
				}

				return true;
			}

			private bool TryResolveEngineUniformValue(string name, in PendingMeshDraw draw, out object? value, out EShaderVarType type)
			{
				value = null;
				type = EShaderVarType._float;
				string normalized = NormalizeEngineUniformName(name);
				XRCamera? camera = Engine.Rendering.State.RenderingCamera;
				XRCamera? rightEyeCamera = Engine.Rendering.State.RenderingStereoRightEyeCamera;
				bool stereoPass = Engine.Rendering.State.IsStereoPass;
				bool useUnjittered = Engine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;

				Matrix4x4 prevModelMatrix = draw.PreviousModelMatrix;
				if (IsApproximatelyIdentity(prevModelMatrix) && !IsApproximatelyIdentity(draw.ModelMatrix))
					prevModelMatrix = draw.ModelMatrix;

				Matrix4x4 inverseModel = Matrix4x4.Identity;
				Matrix4x4.Invert(draw.ModelMatrix, out inverseModel);

				Matrix4x4 viewMatrix = camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
				Matrix4x4 inverseViewMatrix = camera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
				Matrix4x4 projMatrix = useUnjittered && camera is not null
					? camera.ProjectionMatrixUnjittered
					: camera?.ProjectionMatrix ?? Matrix4x4.Identity;

				if (_program is not null && _program.TryGetUniformValue(normalized, out ProgramUniformValue programValue))
				{
					value = programValue.Value;
					type = programValue.Type;
					return true;
				}

				switch (normalized)
				{
					case nameof(EEngineUniform.UpdateDelta):
						value = Engine.Time.Timer.Update.Delta;
						type = EShaderVarType._float;
						return true;
					case nameof(EEngineUniform.ModelMatrix):
						value = draw.ModelMatrix;
						type = EShaderVarType._mat4;
						return true;
					case nameof(EEngineUniform.PrevModelMatrix):
						value = prevModelMatrix;
						type = EShaderVarType._mat4;
						return true;
					case nameof(EEngineUniform.RootInvModelMatrix):
						value = inverseModel;
						type = EShaderVarType._mat4;
						return true;
					case nameof(EEngineUniform.ViewMatrix):
					case nameof(EEngineUniform.PrevViewMatrix):
					case nameof(EEngineUniform.PrevLeftEyeViewMatrix):
					case nameof(EEngineUniform.PrevRightEyeViewMatrix):
						value = viewMatrix;
						type = EShaderVarType._mat4;
						return true;
					case nameof(EEngineUniform.InverseViewMatrix):
						value = inverseViewMatrix;
						type = EShaderVarType._mat4;
						return true;
					case nameof(EEngineUniform.ProjMatrix):
					case nameof(EEngineUniform.PrevProjMatrix):
					case nameof(EEngineUniform.PrevLeftEyeProjMatrix):
					case nameof(EEngineUniform.PrevRightEyeProjMatrix):
						value = projMatrix;
						type = EShaderVarType._mat4;
						return true;
					case nameof(EEngineUniform.LeftEyeInverseViewMatrix):
						value = inverseViewMatrix;
						type = EShaderVarType._mat4;
						return true;
					case nameof(EEngineUniform.RightEyeInverseViewMatrix):
						value = rightEyeCamera?.Transform.RenderMatrix ?? inverseViewMatrix;
						type = EShaderVarType._mat4;
						return true;
					case nameof(EEngineUniform.LeftEyeProjMatrix):
						value = projMatrix;
						type = EShaderVarType._mat4;
						return true;
					case nameof(EEngineUniform.RightEyeProjMatrix):
						value = rightEyeCamera?.ProjectionMatrix ?? projMatrix;
						type = EShaderVarType._mat4;
						return true;
					case nameof(EEngineUniform.CameraPosition):
						value = ToVector4(camera?.Transform.RenderTranslation ?? Vector3.Zero);
						type = EShaderVarType._vec4;
						return true;
					case nameof(EEngineUniform.CameraForward):
						value = ToVector4(camera?.Transform.RenderForward ?? Vector3.UnitZ);
						type = EShaderVarType._vec4;
						return true;
					case nameof(EEngineUniform.CameraUp):
						value = ToVector4(camera?.Transform.RenderUp ?? Vector3.UnitY);
						type = EShaderVarType._vec4;
						return true;
					case nameof(EEngineUniform.CameraRight):
						value = ToVector4(camera?.Transform.RenderRight ?? Vector3.UnitX);
						type = EShaderVarType._vec4;
						return true;
					case nameof(EEngineUniform.CameraNearZ):
						value = camera?.NearZ ?? 0f;
						type = EShaderVarType._float;
						return true;
					case nameof(EEngineUniform.CameraFarZ):
						value = camera?.FarZ ?? 0f;
						type = EShaderVarType._float;
						return true;
					case nameof(EEngineUniform.CameraFovX):
						value = camera?.Parameters is XRPerspectiveCameraParameters persp ? persp.HorizontalFieldOfView : 0f;
						type = EShaderVarType._float;
						return true;
					case nameof(EEngineUniform.CameraFovY):
						value = camera?.Parameters is XRPerspectiveCameraParameters perspY ? perspY.VerticalFieldOfView : 0f;
						type = EShaderVarType._float;
						return true;
					case nameof(EEngineUniform.CameraAspect):
						value = camera?.Parameters is XRPerspectiveCameraParameters perspA ? perspA.AspectRatio : 0f;
						type = EShaderVarType._float;
						return true;
					case nameof(EEngineUniform.DepthMode):
						value = (int)(camera?.DepthMode ?? XRCamera.EDepthMode.Normal);
						type = EShaderVarType._int;
						return true;
					case nameof(EEngineUniform.ScreenWidth):
					case nameof(EEngineUniform.ScreenHeight):
						var area = Engine.Rendering.State.RenderArea;
						value = normalized.Equals(nameof(EEngineUniform.ScreenWidth), StringComparison.Ordinal) ? (float)area.Width : (float)area.Height;
						type = EShaderVarType._float;
						return true;
					case nameof(EEngineUniform.ScreenOrigin):
						value = new Vector2(0f, 0f);
						type = EShaderVarType._vec2;
						return true;
					case nameof(EEngineUniform.BillboardMode):
						value = (int)draw.BillboardMode;
						type = EShaderVarType._int;
						return true;
					case nameof(EEngineUniform.VRMode):
						value = stereoPass ? 1 : 0;
						type = EShaderVarType._int;
						return true;
					case nameof(EEngineUniform.UIXYWH):
						if (_program is not null && _program.TryGetUniformValue(nameof(EEngineUniform.UIXYWH), out ProgramUniformValue bounds))
						{
							value = bounds.Value;
							type = bounds.Type;
							return true;
						}
						value = Vector4.Zero;
						type = EShaderVarType._vec4;
						return true;
					case nameof(EEngineUniform.UIWidth):
					case nameof(EEngineUniform.UIHeight):
					case nameof(EEngineUniform.UIX):
					case nameof(EEngineUniform.UIY):
						if (_program is not null && _program.TryGetUniformValue(normalized, out ProgramUniformValue uiScalar))
						{
							value = uiScalar.Value;
							type = uiScalar.Type;
							return true;
						}
						if (_program is not null && _program.TryGetUniformValue(nameof(EEngineUniform.UIXYWH), out ProgramUniformValue uiBounds) &&
						    uiBounds.Value is Vector4 b)
						{
							value = normalized switch
							{
								nameof(EEngineUniform.UIX) => b.X,
								nameof(EEngineUniform.UIY) => b.Y,
								nameof(EEngineUniform.UIWidth) => b.Z,
								nameof(EEngineUniform.UIHeight) => b.W,
								_ => 0f
							};
							type = EShaderVarType._float;
							return true;
						}
						value = 0f;
						type = EShaderVarType._float;
						return true;
				}

				return false;
			}

			private static bool TryWriteAutoUniformValue(Span<byte> data, AutoUniformMember member, object value, EShaderVarType valueType)
			{
				if (member.EngineType is null)
					return false;

				if (!AreCompatible(member.EngineType.Value, valueType))
					return false;

				uint offset = member.Offset;
				return (object)member.EngineType.Value switch
				{
					EShaderVarType._float => TryWriteScalar(data, offset, value, Convert.ToSingle),
					EShaderVarType._int => TryWriteScalar(data, offset, value, Convert.ToInt32),
					EShaderVarType._uint => TryWriteScalar(data, offset, value, Convert.ToUInt32),
					EShaderVarType._bool => TryWriteScalar(data, offset, value, v => Convert.ToBoolean(v) ? 1 : 0),
					EShaderVarType._vec2 => TryWriteVector2(data, offset, value),
					EShaderVarType._vec3 => TryWriteVector3(data, offset, value),
					EShaderVarType._vec4 => TryWriteVector4(data, offset, value),
					EShaderVarType._ivec2 => TryWriteIVector2(data, offset, value),
					EShaderVarType._ivec3 => TryWriteIVector3(data, offset, value),
					EShaderVarType._ivec4 => TryWriteIVector4(data, offset, value),
					EShaderVarType._uvec2 => TryWriteUVector2(data, offset, value),
					EShaderVarType._uvec3 => TryWriteUVector3(data, offset, value),
					EShaderVarType._uvec4 => TryWriteUVector4(data, offset, value),
					EShaderVarType._mat4 => TryWriteMatrix4(data, offset, value),
					_ => false,
				};
			}

			private static bool AreCompatible(EShaderVarType expected, EShaderVarType actual)
			{
				if (expected == actual)
					return true;

				return (expected, actual) switch
				{
					(EShaderVarType._vec4, EShaderVarType._vec3) => true,
					(EShaderVarType._vec3, EShaderVarType._vec4) => true,
					(EShaderVarType._int, EShaderVarType._bool) => true,
					(EShaderVarType._uint, EShaderVarType._bool) => true,
					(EShaderVarType._bool, EShaderVarType._int) => true,
					(EShaderVarType._bool, EShaderVarType._uint) => true,
					_ => false
				};
			}

			private static bool TryWriteScalar<T>(Span<byte> data, uint offset, object value, Func<object, T> converter) where T : unmanaged
			{
				object candidate = value;
				if (candidate is Array array)
				{
					if (array.Length == 0)
						return false;

					candidate = array.GetValue(0)!;
					if (candidate is null)
						return false;
				}

				if (candidate is not IConvertible && candidate is not T)
					return false;

				try
				{
					T converted = converter(candidate);
					Unsafe.WriteUnaligned(ref data[(int)offset], converted);
					return true;
				}
				catch
				{
					return false;
				}
			}

			private static bool TryWriteVector2(Span<byte> data, uint offset, object value)
			{
				if (value is Vector2 v)
				{
					Unsafe.WriteUnaligned(ref data[(int)offset], v);
					return true;
				}
				return false;
			}

			private static bool TryWriteVector3(Span<byte> data, uint offset, object value)
			{
				if (value is Vector3 v3)
				{
					Vector4 v4 = new(v3, 0f);
					Unsafe.WriteUnaligned(ref data[(int)offset], v4);
					return true;
				}
				if (value is Vector4 v4b)
				{
					Unsafe.WriteUnaligned(ref data[(int)offset], v4b);
					return true;
				}
				return false;
			}

			private static bool TryWriteVector4(Span<byte> data, uint offset, object value)
			{
				if (value is Vector4 v)
				{
					Unsafe.WriteUnaligned(ref data[(int)offset], v);
					return true;
				}
				if (value is Vector3 v3)
				{
					Vector4 v4 = new(v3, 0f);
					Unsafe.WriteUnaligned(ref data[(int)offset], v4);
					return true;
				}
				return false;
			}

			private static bool TryWriteIVector2(Span<byte> data, uint offset, object value)
			{
				if (value is IVector2 v)
				{
					Unsafe.WriteUnaligned(ref data[(int)offset], v);
					return true;
				}
				return false;
			}

			private static bool TryWriteIVector3(Span<byte> data, uint offset, object value)
			{
				if (value is IVector3 v3)
				{
					IVector4 v4 = new(v3.X, v3.Y, v3.Z, 0);
					Unsafe.WriteUnaligned(ref data[(int)offset], v4);
					return true;
				}
				if (value is IVector4 v4b)
				{
					Unsafe.WriteUnaligned(ref data[(int)offset], v4b);
					return true;
				}
				return false;
			}

			private static bool TryWriteIVector4(Span<byte> data, uint offset, object value)
			{
				if (value is IVector4 v)
				{
					Unsafe.WriteUnaligned(ref data[(int)offset], v);
					return true;
				}
				return false;
			}

			private static bool TryWriteUVector2(Span<byte> data, uint offset, object value)
			{
				if (value is UVector2 v)
				{
					Unsafe.WriteUnaligned(ref data[(int)offset], v);
					return true;
				}
				return false;
			}

			private static bool TryWriteUVector3(Span<byte> data, uint offset, object value)
			{
				if (value is UVector3 v3)
				{
					UVector4 v4 = new(v3.X, v3.Y, v3.Z, 0);
					Unsafe.WriteUnaligned(ref data[(int)offset], v4);
					return true;
				}
				if (value is UVector4 v4b)
				{
					Unsafe.WriteUnaligned(ref data[(int)offset], v4b);
					return true;
				}
				return false;
			}

			private static bool TryWriteUVector4(Span<byte> data, uint offset, object value)
			{
				if (value is UVector4 v)
				{
					Unsafe.WriteUnaligned(ref data[(int)offset], v);
					return true;
				}
				return false;
			}

			private static bool TryWriteMatrix4(Span<byte> data, uint offset, object value)
			{
				if (value is Matrix4x4 m)
				{
					Unsafe.WriteUnaligned(ref data[(int)offset], m);
					return true;
				}
				return false;
			}

			private bool TryWriteEngineUniform(string name, in PendingMeshDraw draw, EngineUniformBuffer buffer)
			{
				string normalized = NormalizeEngineUniformName(name);
				XRCamera? camera = Engine.Rendering.State.RenderingCamera;
				XRCamera? rightEyeCamera = Engine.Rendering.State.RenderingStereoRightEyeCamera;
				bool stereoPass = Engine.Rendering.State.IsStereoPass;
				bool useUnjittered = Engine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;

				// Fallback: if previous model matrix was never captured, assume static
				// to avoid injecting false motion into the velocity buffer (causes
				// diagonal blur on objects that are actually still). Use a loose
				// comparison to tolerate floating-point jitter around identity.
				Matrix4x4 prevModelMatrix = draw.PreviousModelMatrix;
				if (IsApproximatelyIdentity(prevModelMatrix) && !IsApproximatelyIdentity(draw.ModelMatrix))
					prevModelMatrix = draw.ModelMatrix;

				Matrix4x4 inverseModel = Matrix4x4.Identity;
				Matrix4x4.Invert(draw.ModelMatrix, out inverseModel);

				Matrix4x4 viewMatrix = camera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
				Matrix4x4 inverseViewMatrix = camera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
				Matrix4x4 projMatrix = useUnjittered && camera is not null
					? camera.ProjectionMatrixUnjittered
					: camera?.ProjectionMatrix ?? Matrix4x4.Identity;

				if (_program is not null && _program.TryGetUniformValue(normalized, out ProgramUniformValue programValue))
					return UploadProgramUniform(buffer, programValue);

				switch (normalized)
				{
					case nameof(EEngineUniform.UpdateDelta):
						return UploadUniform(buffer, Engine.Time.Timer.Update.Delta);
					case nameof(EEngineUniform.ModelMatrix):
						return UploadUniform(buffer, draw.ModelMatrix);
					case nameof(EEngineUniform.PrevModelMatrix):
						return UploadUniform(buffer, prevModelMatrix);
					case nameof(EEngineUniform.RootInvModelMatrix):
						return UploadUniform(buffer, inverseModel);
					case nameof(EEngineUniform.ViewMatrix):
					case nameof(EEngineUniform.PrevViewMatrix):
					case nameof(EEngineUniform.PrevLeftEyeViewMatrix):
					case nameof(EEngineUniform.PrevRightEyeViewMatrix):
						return UploadUniform(buffer, viewMatrix);
					case nameof(EEngineUniform.InverseViewMatrix):
						return UploadUniform(buffer, inverseViewMatrix);
					case nameof(EEngineUniform.ProjMatrix):
					case nameof(EEngineUniform.PrevProjMatrix):
					case nameof(EEngineUniform.PrevLeftEyeProjMatrix):
					case nameof(EEngineUniform.PrevRightEyeProjMatrix):
						return UploadUniform(buffer, projMatrix);
					case nameof(EEngineUniform.LeftEyeInverseViewMatrix):
						return UploadUniform(buffer, inverseViewMatrix);
					case nameof(EEngineUniform.RightEyeInverseViewMatrix):
						return UploadUniform(buffer, rightEyeCamera?.Transform.RenderMatrix ?? inverseViewMatrix);
					case nameof(EEngineUniform.LeftEyeProjMatrix):
						return UploadUniform(buffer, projMatrix);
					case nameof(EEngineUniform.RightEyeProjMatrix):
						return UploadUniform(buffer, rightEyeCamera?.ProjectionMatrix ?? projMatrix);
					case nameof(EEngineUniform.CameraPosition):
						return UploadUniform(buffer, ToVector4(camera?.Transform.RenderTranslation ?? Vector3.Zero));
					case nameof(EEngineUniform.CameraForward):
						return UploadUniform(buffer, ToVector4(camera?.Transform.RenderForward ?? Vector3.UnitZ));
					case nameof(EEngineUniform.CameraUp):
						return UploadUniform(buffer, ToVector4(camera?.Transform.RenderUp ?? Vector3.UnitY));
					case nameof(EEngineUniform.CameraRight):
						return UploadUniform(buffer, ToVector4(camera?.Transform.RenderRight ?? Vector3.UnitX));
					case nameof(EEngineUniform.CameraNearZ):
						return UploadUniform(buffer, camera?.NearZ ?? 0f);
					case nameof(EEngineUniform.CameraFarZ):
						return UploadUniform(buffer, camera?.FarZ ?? 0f);
					case nameof(EEngineUniform.CameraFovX):
						return UploadUniform(buffer, camera?.Parameters is XRPerspectiveCameraParameters persp ? persp.HorizontalFieldOfView : 0f);
					case nameof(EEngineUniform.CameraFovY):
						return UploadUniform(buffer, camera?.Parameters is XRPerspectiveCameraParameters perspY ? perspY.VerticalFieldOfView : 0f);
					case nameof(EEngineUniform.CameraAspect):
						return UploadUniform(buffer, camera?.Parameters is XRPerspectiveCameraParameters perspA ? perspA.AspectRatio : 0f);
					case nameof(EEngineUniform.DepthMode):
						return UploadUniform(buffer, (int)(camera?.DepthMode ?? XRCamera.EDepthMode.Normal));
					case nameof(EEngineUniform.ScreenWidth):
					case nameof(EEngineUniform.ScreenHeight):
						var area = Engine.Rendering.State.RenderArea;
						return UploadUniform(buffer, normalized.Equals(nameof(EEngineUniform.ScreenWidth), StringComparison.Ordinal) ? (float)area.Width : (float)area.Height);
					case nameof(EEngineUniform.ScreenOrigin):
						return UploadUniform(buffer, new Vector2(0f, 0f));
					case nameof(EEngineUniform.BillboardMode):
						return UploadUniform(buffer, (int)draw.BillboardMode);
					case nameof(EEngineUniform.VRMode):
						return UploadUniform(buffer, stereoPass ? 1 : 0);
					case nameof(EEngineUniform.UIXYWH):
						if (_program is not null && _program.TryGetUniformValue(nameof(EEngineUniform.UIXYWH), out ProgramUniformValue uiBounds))
							return UploadProgramUniform(buffer, uiBounds);
						return UploadUniform(buffer, Vector4.Zero);
					case nameof(EEngineUniform.UIX):
					case nameof(EEngineUniform.UIY):
					case nameof(EEngineUniform.UIWidth):
					case nameof(EEngineUniform.UIHeight):
						if (_program is not null && _program.TryGetUniformValue(normalized, out ProgramUniformValue uiScalar))
							return UploadProgramUniform(buffer, uiScalar);
						if (_program is not null && _program.TryGetUniformValue(nameof(EEngineUniform.UIXYWH), out ProgramUniformValue packedBounds) && packedBounds.Value is Vector4 b)
						{
							float scalar = normalized switch
							{
								nameof(EEngineUniform.UIX) => b.X,
								nameof(EEngineUniform.UIY) => b.Y,
								nameof(EEngineUniform.UIWidth) => b.Z,
								nameof(EEngineUniform.UIHeight) => b.W,
								_ => 0f
							};
							return UploadUniform(buffer, scalar);
						}
						return UploadUniform(buffer, 0f);
				}

				if (_engineUniformWarnings.Add(normalized))
					Debug.VulkanWarning($"Unhandled engine uniform '{normalized}' for Vulkan descriptors.");

				return false;
			}

			private static string NormalizeEngineUniformName(string name)
			{
				if (string.IsNullOrWhiteSpace(name))
					return string.Empty;

				return name.EndsWith(VertexUniformSuffix, StringComparison.Ordinal)
					? name[..^VertexUniformSuffix.Length]
					: name;
			}

			private static uint GetEngineUniformSize(string name)
			{
				string normalized = NormalizeEngineUniformName(name);
				return normalized switch
				{
					nameof(EEngineUniform.ModelMatrix) or nameof(EEngineUniform.PrevModelMatrix) or nameof(EEngineUniform.ViewMatrix) or nameof(EEngineUniform.InverseViewMatrix) or nameof(EEngineUniform.ProjMatrix) or nameof(EEngineUniform.LeftEyeInverseViewMatrix) or nameof(EEngineUniform.RightEyeInverseViewMatrix) or nameof(EEngineUniform.LeftEyeProjMatrix) or nameof(EEngineUniform.RightEyeProjMatrix) or nameof(EEngineUniform.PrevViewMatrix) or nameof(EEngineUniform.PrevLeftEyeViewMatrix) or nameof(EEngineUniform.PrevRightEyeViewMatrix) or nameof(EEngineUniform.PrevProjMatrix) or nameof(EEngineUniform.PrevLeftEyeProjMatrix) or nameof(EEngineUniform.PrevRightEyeProjMatrix) or nameof(EEngineUniform.RootInvModelMatrix) => (uint)Unsafe.SizeOf<Matrix4x4>(),
					nameof(EEngineUniform.CameraPosition) or nameof(EEngineUniform.CameraForward) or nameof(EEngineUniform.CameraUp) or nameof(EEngineUniform.CameraRight) => 16u,
					nameof(EEngineUniform.CameraNearZ) or nameof(EEngineUniform.CameraFarZ) or nameof(EEngineUniform.CameraFovX) or nameof(EEngineUniform.CameraFovY) or nameof(EEngineUniform.CameraAspect) or nameof(EEngineUniform.ScreenWidth) or nameof(EEngineUniform.ScreenHeight) or nameof(EEngineUniform.UpdateDelta) or nameof(EEngineUniform.DepthMode) or nameof(EEngineUniform.UIX) or nameof(EEngineUniform.UIY) or nameof(EEngineUniform.UIWidth) or nameof(EEngineUniform.UIHeight) => 4u,
					nameof(EEngineUniform.ScreenOrigin) => 8u,
					nameof(EEngineUniform.BillboardMode) or nameof(EEngineUniform.VRMode) => 4u,
					nameof(EEngineUniform.UIXYWH) => 16u,
					_ => 0u,
				};
			}

			private static Vector4 ToVector4(in Vector3 v) => new(v, 0f);

			private static bool IsApproximatelyIdentity(in Matrix4x4 m)
			{
				const float eps = 1e-4f;
				return MathF.Abs(m.M11 - 1f) < eps && MathF.Abs(m.M22 - 1f) < eps && MathF.Abs(m.M33 - 1f) < eps && MathF.Abs(m.M44 - 1f) < eps
					&& MathF.Abs(m.M12) < eps && MathF.Abs(m.M13) < eps && MathF.Abs(m.M14) < eps
					&& MathF.Abs(m.M21) < eps && MathF.Abs(m.M23) < eps && MathF.Abs(m.M24) < eps
					&& MathF.Abs(m.M31) < eps && MathF.Abs(m.M32) < eps && MathF.Abs(m.M34) < eps
					&& MathF.Abs(m.M41) < eps && MathF.Abs(m.M42) < eps && MathF.Abs(m.M43) < eps;
			}

			private bool UploadUniform<T>(EngineUniformBuffer buffer, in T value) where T : unmanaged
			{
				uint size = (uint)Unsafe.SizeOf<T>();
				uint copySize = Math.Min(buffer.Size, size);

				void* mapped;
				if (Api!.MapMemory(Device, buffer.Memory, 0, buffer.Size, 0, &mapped) != Result.Success)
					return false;

				T localValue = value;
				Unsafe.CopyBlock(mapped, Unsafe.AsPointer(ref localValue), copySize);
				Api.UnmapMemory(Device, buffer.Memory);
				return true;
			}

			private bool UploadProgramUniform(EngineUniformBuffer buffer, ProgramUniformValue value)
			{
				return value.Type switch
				{
					EShaderVarType._float => UploadUniform(buffer, Convert.ToSingle(value.Value)),
					EShaderVarType._int => UploadUniform(buffer, Convert.ToInt32(value.Value)),
					EShaderVarType._uint => UploadUniform(buffer, Convert.ToUInt32(value.Value)),
					EShaderVarType._bool => UploadUniform(buffer, Convert.ToBoolean(value.Value) ? 1 : 0),
					EShaderVarType._vec2 when value.Value is Vector2 v2 => UploadUniform(buffer, v2),
					EShaderVarType._vec3 when value.Value is Vector3 v3 => UploadUniform(buffer, new Vector4(v3, 0f)),
					EShaderVarType._vec4 when value.Value is Vector4 v4 => UploadUniform(buffer, v4),
					EShaderVarType._ivec2 when value.Value is IVector2 iv2 => UploadUniform(buffer, iv2),
					EShaderVarType._ivec3 when value.Value is IVector3 iv3 => UploadUniform(buffer, new IVector4(iv3.X, iv3.Y, iv3.Z, 0)),
					EShaderVarType._ivec4 when value.Value is IVector4 iv4 => UploadUniform(buffer, iv4),
					EShaderVarType._uvec2 when value.Value is UVector2 uv2 => UploadUniform(buffer, uv2),
					EShaderVarType._uvec3 when value.Value is UVector3 uv3 => UploadUniform(buffer, new UVector4(uv3.X, uv3.Y, uv3.Z, 0)),
					EShaderVarType._uvec4 when value.Value is UVector4 uv4 => UploadUniform(buffer, uv4),
					EShaderVarType._mat4 when value.Value is Matrix4x4 mat => UploadUniform(buffer, mat),
					_ => false
				};
			}

			private void RetireUniformBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
			{
				if (buffer.Handle != 0)
					Renderer.RetireBuffer(buffer, memory);
				else if (memory.Handle != 0)
					Api!.FreeMemory(Device, memory, null);
			}

			private void DestroyEngineUniformBuffers(string? singleName = null)
			{
				if (singleName is not null)
				{
					if (_engineUniformBuffers.TryGetValue(singleName, out EngineUniformBuffer[]? toDestroy))
					{
						foreach (EngineUniformBuffer buf in toDestroy)
							RetireUniformBuffer(buf.Buffer, buf.Memory);
					}

					_engineUniformBuffers.Remove(singleName);
					return;
				}

				foreach (EngineUniformBuffer[] buffers in _engineUniformBuffers.Values)
				{
					foreach (EngineUniformBuffer buf in buffers)
						RetireUniformBuffer(buf.Buffer, buf.Memory);
				}

				_engineUniformBuffers.Clear();
			}

			private void DestroyAutoUniformBuffers(string? singleName = null)
			{
				if (singleName is not null)
				{
					if (_autoUniformBuffers.TryGetValue(singleName, out AutoUniformBuffer[]? toDestroy))
					{
						foreach (AutoUniformBuffer buf in toDestroy)
							RetireUniformBuffer(buf.Buffer, buf.Memory);
					}

				_autoUniformBuffers.Remove(singleName);
				return;
			}

			foreach (AutoUniformBuffer[] buffers in _autoUniformBuffers.Values)
			{
				foreach (AutoUniformBuffer buf in buffers)
					RetireUniformBuffer(buf.Buffer, buf.Memory);
			}

			_autoUniformBuffers.Clear();
		}

		private void DestroyDescriptors()
		{
			if (_descriptorSets is not null)
				_descriptorSets = null;
			_descriptorSchemaFingerprint = 0;

			DestroyEngineUniformBuffers();
			DestroyAutoUniformBuffers();

			if (_fallbackStorageBuffer.Buffer.Handle != 0 || _fallbackStorageBuffer.Memory.Handle != 0)
			{
				RetireUniformBuffer(_fallbackStorageBuffer.Buffer, _fallbackStorageBuffer.Memory);
				_fallbackStorageBuffer = default;
			}

			if (_descriptorPool.Handle != 0)
			{
				Api!.DestroyDescriptorPool(Device, _descriptorPool, null);
				_descriptorPool = default;
			}
		}

		private void WarnOnce(string message)
		{
			if (_descriptorWarnings.Add(message))
				Debug.VulkanWarning(message);
		}

		private static Format ToFormat(EComponentType type, uint count, bool integral)
		{
			return (type, count, integral) switch
			{
				(EComponentType.SByte, 1, _) => Format.R8Sint,
				(EComponentType.SByte, 2, _) => Format.R8G8Sint,
				(EComponentType.SByte, 3, _) => Format.R8G8B8Sint,
				(EComponentType.SByte, 4, _) => Format.R8G8B8A8Sint,
				(EComponentType.Byte, 1, _) => Format.R8Uint,
				(EComponentType.Byte, 2, _) => Format.R8G8Uint,
				(EComponentType.Byte, 3, _) => Format.R8G8B8Uint,
				(EComponentType.Byte, 4, _) => Format.R8G8B8A8Uint,
				(EComponentType.Short, 1, true) => Format.R16Sint,
				(EComponentType.Short, 2, true) => Format.R16G16Sint,
				(EComponentType.Short, 3, true) => Format.R16G16B16Sint,
				(EComponentType.Short, 4, true) => Format.R16G16B16A16Sint,
				(EComponentType.UShort, 1, _) => Format.R16Uint,
				(EComponentType.UShort, 2, _) => Format.R16G16Uint,
				(EComponentType.UShort, 3, _) => Format.R16G16B16Uint,
				(EComponentType.UShort, 4, _) => Format.R16G16B16A16Uint,
				(EComponentType.Int, 1, _) => Format.R32Sint,
				(EComponentType.Int, 2, _) => Format.R32G32Sint,
				(EComponentType.Int, 3, _) => Format.R32G32B32Sint,
				(EComponentType.Int, 4, _) => Format.R32G32B32A32Sint,
				(EComponentType.UInt, 1, _) => Format.R32Uint,
				(EComponentType.UInt, 2, _) => Format.R32G32Uint,
				(EComponentType.UInt, 3, _) => Format.R32G32B32Uint,
				(EComponentType.UInt, 4, _) => Format.R32G32B32A32Uint,
				(EComponentType.Double, 2, _) => Format.R64G64Sfloat,
				(EComponentType.Double, 3, _) => Format.R64G64B64Sfloat,
				(EComponentType.Double, 4, _) => Format.R64G64B64A64Sfloat,
				_ => count switch
				{
					1 => Format.R32Sfloat,
					2 => Format.R32G32Sfloat,
					3 => Format.R32G32B32Sfloat,
					4 => Format.R32G32B32A32Sfloat,
					_ => Format.Undefined
				}
			};
		}

		private static IndexType ToVkIndexType(IndexSize size)
			=> size switch
			{
				IndexSize.Byte => IndexType.Uint8Ext,
				IndexSize.TwoBytes => IndexType.Uint16,
				IndexSize.FourBytes => IndexType.Uint32,
				_ => IndexType.Uint16
			};
	}
}
