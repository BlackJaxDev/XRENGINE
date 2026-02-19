// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.Pipeline.cs  – partial class: Shader Program, Vertex Input
//                               & Graphics Pipeline Management
//
// Compiles/links shader programs, builds vertex input state from buffer cache,
// and creates/caches Vulkan graphics pipelines keyed by full draw state.
// ──────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Linq;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
	public partial class VkMeshRenderer
	{
		#region Shader Program Management

		/// <summary>
		/// Ensures a compiled and linked VkRenderProgram exists for the given material.
		/// If the material lacks a vertex shader, one is auto-generated from
		/// <c>Data.VertexShaderSource</c>. Returns false if linking fails.
		/// </summary>
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

		#endregion // Shader Program Management

		#region Vertex Input State

		/// <summary>
		/// Builds Vulkan vertex input binding and attribute descriptions from the
		/// current buffer cache. Handles both interleaved and per-attribute layouts.
		/// Also populates <c>_vertexBuffersByBinding</c> for use during draw recording.
		/// </summary>
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

		#endregion // Vertex Input State

		#region Pipeline Management

		/// <summary>
		/// Ensures a valid Vulkan graphics pipeline for the given material, topology,
		/// and draw state. Pipelines are cached by <see cref="PipelineKey"/>. If no
		/// cached pipeline matches, a new one is created with the current shader
		/// program, vertex layout, and fixed-function state.
		/// </summary>
		private bool EnsurePipeline(
			XRMaterial material,
			PrimitiveTopology topology,
			in PendingMeshDraw draw,
			RenderPass renderPass,
			bool useDynamicRendering,
			Format colorAttachmentFormat,
			Format depthAttachmentFormat,
			out Pipeline pipeline)
		{
			pipeline = default;

			if (_pipelineDirty)
				DestroyPipelines();

			_descriptorDirty = true; // Pipeline/program changes always invalidate descriptor sets

			if (!EnsureProgram(material))
				return false;

			if (useDynamicRendering && colorAttachmentFormat == Format.Undefined && draw.ColorWriteMask != 0)
			{
				Debug.VulkanWarningEvery(
					$"Vulkan.MeshRenderer.SkipDraw.NoColorAttachment.{_program?.Data?.Name ?? "UnknownProgram"}",
					TimeSpan.FromSeconds(2),
					"[Vulkan] Skipping pipeline creation for program '{0}': dynamic rendering has undefined color attachment format while color writes are enabled.",
					_program?.Data?.Name ?? "UnknownProgram");
				return false;
			}

			BuildVertexInputState();

			ulong programPipelineHash = _program!.ComputeGraphicsPipelineFingerprint();
			ulong vertexLayoutHash = ComputeVertexLayoutHash();

			PipelineKey key = new(
				topology,
				useDynamicRendering,
				useDynamicRendering ? 0UL : renderPass.Handle,
				useDynamicRendering ? colorAttachmentFormat : Format.Undefined,
				useDynamicRendering ? depthAttachmentFormat : Format.Undefined,
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

			// Check pipeline cache before creating a new pipeline object
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

				uint colorAttachmentCount = useDynamicRendering
					? (colorAttachmentFormat != Format.Undefined ? 1u : 0u)
					: Renderer.GetRenderPassColorAttachmentCount(renderPass);
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
							RenderPass = useDynamicRendering ? default : renderPass,
							Subpass = 0,
						};

						if (useDynamicRendering)
						{
							Format* colorFormats = stackalloc Format[(int)colorAttachmentCount];
							if (colorAttachmentCount > 0)
								colorFormats[0] = colorAttachmentFormat;

							PipelineRenderingCreateInfo renderingInfo = new()
							{
								SType = StructureType.PipelineRenderingCreateInfo,
								ColorAttachmentCount = colorAttachmentCount,
								PColorAttachmentFormats = colorAttachmentCount > 0 ? colorFormats : null,
								DepthAttachmentFormat = depthAttachmentFormat,
								StencilAttachmentFormat = IsStencilCapableFormat(depthAttachmentFormat)
									? depthAttachmentFormat
									: Format.Undefined,
							};

							pipelineInfo.PNext = &renderingInfo;
						}

						try
						{
							pipeline = _program!.CreateGraphicsPipeline(ref pipelineInfo, Renderer.ActivePipelineCache);
						}
						catch (InvalidOperationException ex)
						{
							Debug.VulkanWarningEvery(
								$"Vulkan.Pipeline.CreateFailed.{_program!.Data.Name}",
								TimeSpan.FromSeconds(5),
								"[Vulkan] Pipeline creation failed for program '{0}': {1}",
								_program.Data.Name ?? "UnnamedProgram",
								ex.Message);
							pipeline = default;
							return false;
						}
						_pipelines[key] = pipeline;
						_pipelineDirty = false;
						_meshDirty = false;
						success = pipeline.Handle != 0;
					}
				}
			}

			return success;
		}

		/// <summary>
		/// Destroys all cached pipelines and associated descriptor resources.
		/// Called when the program/material/mesh changes require a full rebuild.
		/// </summary>
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

		#endregion // Pipeline Management
	}
}
