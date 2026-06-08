// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.Pipeline.cs  – partial class: Shader Program, Vertex Input
//                               & Graphics Pipeline Management
//
// Compiles/links shader programs, builds vertex input state from buffer cache,
// and creates/caches Vulkan graphics pipelines keyed by full draw state.
// ──────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

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
			var shaders = new List<XRShader>();
			bool hasVertex = false;
			string? generatedVertexIdentity = null;

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

				generatedVertexIdentity = XRRenderProgramDescriptor.BuildGeneratedSourceIdentity(vsSource);
				shaders.Add(GenerateVertexShader(vsSource));
			}

			string generatedProgramName = BuildGeneratedProgramName(material, shaders);
			string generatedProgramAxes = BuildGeneratedProgramAxes(material);
			string shaderStageList = BuildShaderStageList(shaders);
			string programIdentity = BuildGeneratedProgramIdentity(material, generatedProgramAxes, shaderStageList, generatedVertexIdentity);
			if (!_programCache.TryGetValue(programIdentity, out GeneratedProgramCacheEntry? entry))
			{
				XRRenderProgramDescriptor descriptor = XRRenderProgramDescriptor.FromShaders(
					shaders,
					separable: false,
					renderSettingsVersion: RuntimeEngine.Rendering.Settings.ShaderConfigVersion,
					generatedVertexIdentity: generatedVertexIdentity,
					materialVariantKind: material.ActiveUberVariant.IsEmpty ? null : "MaterialVariant",
					materialVariantHash: material.ActiveUberVariant.VariantHash,
					vertexLayoutIdentity: Data.GetType().Name,
					topologyKind: "VulkanCombinedMesh");

				XRRenderProgram generatedProgram = new(linkNow: false, separable: false, shaders)
				{
					Name = generatedProgramName,
					UsageTag = $"VulkanCombinedMeshProgram | material={material.Name ?? "<unnamed>"} | mesh={Mesh?.Name ?? "<unnamed>"} | renderer={MeshRenderer?.Name ?? "<unnamed>"} | axes={generatedProgramAxes}",
					ProgramDescriptor = descriptor,
				};
				generatedProgram.SetShaderProgramDiagnosticMetadata(new XRRenderProgram.ShaderProgramDiagnosticMetadata(
					material.Name,
					MeshRenderer?.Name,
					Data.GetType().Name,
					"VulkanCombinedMesh",
					Mesh?.Name,
					shaderStageList));
				generatedProgram.AllowLink();

				VkRenderProgram? vkProgram = Renderer.GenericToAPI<VkRenderProgram>(generatedProgram);
				if (vkProgram is null)
				{
					generatedProgram.Destroy();
					Debug.VulkanWarningEvery(
						$"Vulkan.MeshRenderer.{GetHashCode()}.ProgramWrapperNull",
						TimeSpan.FromSeconds(2),
						"[Vulkan] MeshRenderer '{0}' cannot render: failed to create VkRenderProgram wrapper.",
						MeshRenderer?.Name ?? "<unnamed>");
					return false;
				}

				entry = new GeneratedProgramCacheEntry
				{
					Data = generatedProgram,
					Program = vkProgram,
				};
				_programCache[programIdentity] = entry;
			}

			if (!string.Equals(_activeProgramIdentity, programIdentity, StringComparison.Ordinal))
			{
				_activeProgramIdentity = programIdentity;
				_pipelineDirty = true;
				_descriptorDirty = true;
			}

			_generatedProgram = entry.Data;
			_program = entry.Program;
			_program.Generate();
			bool linked = _program.Link(MeshRenderer?.GenerateAsync ?? false);
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

		private static readonly ConcurrentDictionary<string, XRShader> _generatedVertexShaderCache = new(StringComparer.Ordinal);

		private static XRShader GenerateVertexShader(string source)
			=> _generatedVertexShaderCache.GetOrAdd(source ?? string.Empty, static src => new XRShader(EShaderType.Vertex, src));

		private string BuildGeneratedProgramIdentity(
			XRMaterial material,
			string generatedProgramAxes,
			string shaderStageList,
			string? generatedVertexIdentity)
			=> string.Concat(
				"material=",
				RuntimeHelpers.GetHashCode(material).ToString("X8"),
				";shaderRevision=",
				material.ShaderStateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
				";axes=",
				generatedProgramAxes,
				";stages=",
				shaderStageList,
				";generatedVertex=",
				generatedVertexIdentity ?? string.Empty);

		private string BuildGeneratedProgramName(XRMaterial material, IReadOnlyList<XRShader> shaders)
			=> $"VkCombined:{SanitizeProgramName(material.Name, "material")}:{SanitizeProgramName(Mesh?.Name, "mesh")}:{BuildGeneratedProgramAxes(material)}:{BuildShaderStageList(shaders)}";

		private string BuildGeneratedProgramAxes(XRMaterial material)
		{
			XRMesh? mesh = Mesh;
			bool useComputeSkinning = mesh?.HasSkinning == true &&
				RuntimeEngine.Rendering.Settings.AllowSkinning &&
				RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader;
			bool useComputeBlendshapes = mesh?.BlendshapeCount > 0 &&
				RuntimeEngine.Rendering.Settings.AllowBlendshapes &&
				(RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || useComputeSkinning);

			return string.Join(";",
				$"shaderConfig={RuntimeEngine.Rendering.Settings.ShaderConfigVersion}",
				$"skinning={mesh?.HasSkinning == true}",
				$"computeSkinning={useComputeSkinning}",
				$"blendshapes={mesh?.BlendshapeCount > 0}",
				$"computeBlendshapes={useComputeBlendshapes}",
				$"precombineBlendshapes={RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass}",
				$"meshDeform={MeshRenderer.MeshDeformEnabled}",
				$"directionalShadow={material.DirectionalCascadeShadowMaterialKind}",
				$"pointShadow={material.PointShadowMaterialKind}",
				$"depthNormal={RuntimeEngine.Rendering.State.RenderingPipelineState?.UseDepthNormalMaterialVariants ?? false}",
				$"clipDepth={RuntimeEngine.Rendering.EffectiveClipDepthRange}",
				$"clipY={RuntimeEngine.Rendering.Settings.ClipSpaceYDirection}");
		}

		private static string BuildShaderStageList(IReadOnlyList<XRShader> shaders)
		{
			if (shaders.Count == 0)
				return "no-shaders";

			return string.Join(", ", shaders.Select(static shader => $"{shader.Type}:{ResolveShaderLabel(shader)}"));
		}

		private static string ResolveShaderLabel(XRShader shader)
		{
			if (!string.IsNullOrWhiteSpace(shader.Source?.FilePath))
				return Path.GetFileName(shader.Source.FilePath!);
			if (!string.IsNullOrWhiteSpace(shader.FilePath))
				return Path.GetFileName(shader.FilePath!);
			if (!string.IsNullOrWhiteSpace(shader.Name))
				return shader.Name!;

			return shader.Type.ToString();
		}

		private static string SanitizeProgramName(string? value, string fallback)
			=> string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

		#endregion // Shader Program Management

		#region Vertex Input State

		/// <summary>
		/// Builds Vulkan vertex input binding and attribute descriptions from the
		/// current buffer cache. Handles both interleaved and per-attribute layouts.
		/// Also populates <c>_vertexBuffersByBinding</c> for use during draw recording.
		/// </summary>
		/// <remarks>
		/// Attribute locations are resolved by semantic name from the vertex shader's
		/// reflected inputs (mirroring the OpenGL by-name binding path) rather than by
		/// buffer enumeration order. Enumeration order does not match the shader's
		/// declared <c>layout(location = N)</c> order, so the legacy sequential scheme
		/// bound the wrong vertex stream to each location, corrupting positions/normals.
		/// </remarks>
		private void BuildVertexInputState()
		{
			List<VertexInputBindingDescription> bindings = [];
			List<VertexInputAttributeDescription> attributes = [];
			List<KeyValuePair<string, VkDataBuffer>> vertexBuffers = [];
			List<KeyValuePair<string, XRDataBuffer>> layoutBuffers = [];
			_vertexBuffersByBinding.Clear();

			bool resolveByName = _program is not null && _program.HasReflectedVertexInputs;

			uint nextBinding = 0;
			uint nextLocation = 0;
			HashSet<uint> usedBindings = [];

			foreach (var pair in _bufferCache)
			{
				layoutBuffers.Add(new(pair.Key, pair.Value.Data));
				if (pair.Value.Data.Target == EBufferTarget.ArrayBuffer)
					vertexBuffers.Add(pair);
			}

			vertexBuffers.Sort(static (a, b) =>
			{
				uint aBinding = a.Value.Data.BindingIndexOverride ?? uint.MaxValue;
				uint bBinding = b.Value.Data.BindingIndexOverride ?? uint.MaxValue;
				int bindingCompare = aBinding.CompareTo(bBinding);
				return bindingCompare != 0
					? bindingCompare
					: string.Compare(a.Key, b.Key, StringComparison.Ordinal);
			});

			foreach (var pair in vertexBuffers)
			{
				string bufferName = pair.Key;
				VkDataBuffer buffer = pair.Value;

				uint binding = buffer.Data.BindingIndexOverride ?? AllocateNextVertexBinding(usedBindings, ref nextBinding);
				if (!usedBindings.Add(binding))
				{
					WarnOnce($"Skipping duplicate Vulkan vertex binding {binding} for buffer '{bufferName}' on mesh '{Mesh?.Name ?? "UnnamedMesh"}'.");
					continue;
				}

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
						if (!TryResolveVertexAttributeLocation(attr.AttributeName, attr.AttribIndexOverride, resolveByName, ref nextLocation, out uint location))
						{
							WarnMissingVertexAttribute(buffer, attr.AttributeName, attr.AttribIndexOverride, buffer.Data.Normalize, interleaved: true);
							continue;
						}

						attributes.Add(new VertexInputAttributeDescription
						{
							Location = location,
							Binding = binding,
							Format = ToFormat(attr.Type, attr.Count, attr.Integral, buffer.Data.Normalize),
							Offset = attr.Offset
						});
					}
				}
				else
				{
					if (!TryResolveVertexAttributeLocation(bufferName, null, resolveByName, ref nextLocation, out uint location))
					{
						WarnMissingVertexAttribute(buffer, bufferName, null, buffer.Data.Normalize, interleaved: false);
						continue;
					}

					attributes.Add(new VertexInputAttributeDescription
					{
						Location = location,
						Binding = binding,
						Format = ToFormat(buffer.Data.ComponentType, buffer.Data.ComponentCount, buffer.Data.Integral, buffer.Data.Normalize),
						Offset = 0
					});
				}
			}

			_vertexBindings = [.. bindings];
			_vertexAttributes = [.. attributes];
			_geometryLayoutSignature = MeshGeometryLayoutSignatureBuilder.Create(
				Mesh,
				MeshRenderer,
				layoutBuffers,
				ResolvePrimaryIndexSizeForLayout(out bool hasIndexBuffers),
				hasIndexBuffers,
				hasIndexBuffers ? "IndexBuffer" : "VertexCount");

			if (_vertexBindings.Length > 0 && _vertexAttributes.Length == 0)
			{
				Debug.VulkanWarningEvery(
					$"Vulkan.VertexInput.NoAttributes.{_program?.Data?.Name ?? "UnknownProgram"}.{Mesh?.Name ?? "UnnamedMesh"}",
					TimeSpan.FromSeconds(2),
					"[Vulkan] No vertex attributes were bound for program='{0}' mesh='{1}'. layout={2}",
					_program?.Data?.Name ?? "<unnamed program>",
					Mesh?.Name ?? "<unnamed mesh>",
					_geometryLayoutSignature.DebugSummary);
			}
		}

		private static uint AllocateNextVertexBinding(HashSet<uint> usedBindings, ref uint nextBinding)
		{
			while (usedBindings.Contains(nextBinding))
				nextBinding++;

			return nextBinding++;
		}

		private IndexSize ResolvePrimaryIndexSizeForLayout(out bool hasIndexBuffers)
		{
			if (HasIndexData(_triangleIndexBuffer))
			{
				hasIndexBuffers = true;
				return _triangleIndexSize;
			}

			if (HasIndexData(_lineIndexBuffer))
			{
				hasIndexBuffers = true;
				return _lineIndexSize;
			}

			if (HasIndexData(_pointIndexBuffer))
			{
				hasIndexBuffers = true;
				return _pointIndexSize;
			}

			hasIndexBuffers = false;
			return IndexSize.FourBytes;
		}

		private static ulong ComputeMaterialLayoutHash(XRMaterial material)
		{
			HashCode hash = new();
			hash.Add(material.BindingLayoutVersion);
			hash.Add(material.Textures.Count);
			hash.Add(material.Parameters.Length);
			hash.Add(material.ShaderPipelineProgram?.GetHashCode() ?? 0);
			hash.Add((int)material.DirectionalCascadeShadowMaterialKind);
			hash.Add((int)material.PointShadowMaterialKind);
			hash.Add(material.ShadowUniformSourceMaterial?.BindingLayoutVersion ?? 0UL);
			hash.Add(material.RenderOptions?.GetHashCode() ?? 0);
			return unchecked((ulong)hash.ToHashCode());
		}

		private static ulong ComputePassMetadataHash(IReadOnlyCollection<RenderPassMetadata>? passMetadata, int passIndex)
		{
			HashCode hash = new();
			hash.Add(passIndex);
			hash.Add(passMetadata?.Count ?? 0);
			if (passMetadata is not null)
			{
				foreach (RenderPassMetadata metadata in passMetadata)
				{
					hash.Add(metadata.PassIndex);
					hash.Add(metadata.Name, StringComparer.Ordinal);
					hash.Add((int)metadata.Stage);
					hash.Add(metadata.DescriptorSchemas.Count);
					foreach (string schema in metadata.DescriptorSchemas)
						hash.Add(schema, StringComparer.Ordinal);
				}
			}

			hash.Add(RuntimeEngine.Rendering.State.RenderingPipelineState?.ShadowPass ?? false);
			hash.Add(RuntimeEngine.Rendering.State.RenderingPipelineState?.UseDepthNormalMaterialVariants ?? false);
			hash.Add(RuntimeEngine.Rendering.State.RenderingPipelineState?.DirectionalCascadeLayeredShadowPass ?? false);
			hash.Add(RuntimeEngine.Rendering.State.RenderingPipelineState?.PointLightLayeredShadowPass ?? false);
			return unchecked((ulong)hash.ToHashCode());
		}

		private ulong ComputeFeatureProfileHash()
		{
			HashCode hash = new();
			hash.Add(_pipelineShaderConfigVersion);
			hash.Add(_pipelineUsesShaderClipDepthRemap);
			hash.Add(_pipelineUsesNativeDepthClipControl);
			hash.Add(RuntimeEngine.Rendering.EffectiveClipDepthRange);
			hash.Add(RuntimeEngine.Rendering.Settings.ClipSpaceYDirection);
			hash.Add(RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl);
			hash.Add(Renderer.SupportsIndexTypeUint8);
			return unchecked((ulong)hash.ToHashCode());
		}


		/// <summary>
		/// Resolves the vertex attribute location for a named buffer/attribute.
		/// Precedence: explicit override &#8594; vertex-shader reflection by name &#8594;
		/// (legacy) sequential allocation when the shader exposes no reflected inputs.
		/// When reflection is available but the name is not consumed, the attribute is
		/// skipped (return false) instead of being bound to a guessed, colliding slot.
		/// </summary>
		private bool TryResolveVertexAttributeLocation(string? attributeName, uint? attribIndexOverride, bool resolveByName, ref uint nextLocation, out uint location)
		{
			if (attribIndexOverride.HasValue)
			{
				location = attribIndexOverride.Value;
				return true;
			}

			if (resolveByName)
				return _program!.TryGetVertexInputLocation(attributeName ?? string.Empty, out location);

			location = nextLocation++;
			return true;
		}

		private void WarnMissingVertexAttribute(
			VkDataBuffer buffer,
			string? attributeName,
			uint? attributeIndexOverride,
			bool normalized,
			bool interleaved)
		{
			string name = string.IsNullOrWhiteSpace(attributeName) ? "<unnamed>" : attributeName;
			Debug.VulkanWarningEvery(
				$"Vulkan.VertexAttribute.Missing.{_program?.Data?.Name ?? "UnknownProgram"}.{name}",
				TimeSpan.FromSeconds(2),
				"[Vulkan] Missing vertex attribute '{0}' for program='{1}' shader='{2}' mesh='{3}' renderer='{4}' buffer='{5}' bindingOverride={6} attribOverride={7} interleaved={8} componentType={9} componentCount={10} integral={11} normalized={12} instanceDivisor={13} layout={14}.",
				name,
				_program?.Data?.Name ?? "<unnamed program>",
				_program?.Data?.UsageTag ?? "<unknown shader>",
				Mesh?.Name ?? "<unnamed mesh>",
				MeshRenderer?.Name ?? "<unnamed renderer>",
				buffer.Data.AttributeName,
				buffer.Data.BindingIndexOverride?.ToString() ?? "<auto>",
				attributeIndexOverride?.ToString() ?? "<auto>",
				interleaved,
				buffer.Data.ComponentType,
				buffer.Data.ComponentCount,
				buffer.Data.Integral,
				normalized,
				buffer.Data.InstanceDivisor,
				_geometryLayoutSignature.DebugSummary);
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
			int passIndex,
			IReadOnlyCollection<RenderPassMetadata>? passMetadata,
			bool depthStencilReadOnly,
			string pipelineName,
			out Pipeline pipeline)
		{
			pipeline = default;

			RefreshClipDepthPipelinePolicy();

			if (!EnsureProgram(material))
				return false;

			bool pipelineInvalidated = _pipelineDirty;
			PendingMeshDraw effectiveDraw = ResolveAttachmentCompatibleDrawState(draw, passIndex, passMetadata, depthStencilReadOnly);

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
			ulong descriptorLayoutHash = ComputeDescriptorSchemaFingerprint(
				_program.DescriptorBindings,
				_program.DescriptorSetLayouts.Count,
				Renderer.swapChainImages?.Length ?? 0);
			ulong materialLayoutHash = ComputeMaterialLayoutHash(material);
			ulong passMetadataHash = ComputePassMetadataHash(passMetadata, passIndex);
			ulong featureProfileHash = ComputeFeatureProfileHash();
			bool useNativeNegativeOneToOneDepth = RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl;

			PipelineKey key = new(
				topology,
				useDynamicRendering,
				useDynamicRendering ? 0UL : renderPass.Handle,
				useDynamicRendering ? colorAttachmentFormat : Format.Undefined,
				useDynamicRendering ? depthAttachmentFormat : Format.Undefined,
				programPipelineHash,
				vertexLayoutHash,
				descriptorLayoutHash,
				materialLayoutHash,
				passMetadataHash,
				featureProfileHash,
				effectiveDraw.RasterizationSamples,
				effectiveDraw.DepthTestEnabled,
				effectiveDraw.DepthWriteEnabled,
				effectiveDraw.DepthCompareOp,
				effectiveDraw.StencilTestEnabled,
				effectiveDraw.FrontStencilState,
				effectiveDraw.BackStencilState,
				effectiveDraw.StencilWriteMask,
				effectiveDraw.CullMode,
				effectiveDraw.FrontFace,
				effectiveDraw.BlendEnabled,
				effectiveDraw.AlphaToCoverageEnabled,
				effectiveDraw.ColorBlendOp,
				effectiveDraw.AlphaBlendOp,
				effectiveDraw.SrcColorBlendFactor,
				effectiveDraw.DstColorBlendFactor,
				effectiveDraw.SrcAlphaBlendFactor,
				effectiveDraw.DstAlphaBlendFactor,
				effectiveDraw.ColorWriteMask,
				useNativeNegativeOneToOneDepth);

			if (pipelineInvalidated && _pipelines.Count > 256)
				DestroyPipelines();

			// Check pipeline cache before creating a new pipeline object
			if (_pipelines.TryGetValue(key, out pipeline) && pipeline.Handle != 0)
			{
				RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheLookup(cacheHit: true);
				_pipelineDirty = false;
				return true;
			}

			Renderer.RecordVulkanGraphicsPipelineCacheMiss(
				passIndex,
				passMetadata,
				pipelineName,
				Mesh?.Name,
				material,
				_program!.Data?.Name,
				topology,
				useDynamicRendering,
				renderPass,
				colorAttachmentFormat,
				depthAttachmentFormat,
				programPipelineHash,
				vertexLayoutHash,
				effectiveDraw.RasterizationSamples,
				effectiveDraw.DepthTestEnabled,
				effectiveDraw.BlendEnabled,
				effectiveDraw.AlphaToCoverageEnabled,
				effectiveDraw.ColorWriteMask);

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

				PipelineViewportDepthClipControlCreateInfoEXTNative depthClipControlInfo = new()
				{
					SType = VulkanDepthClipControlExt.PipelineViewportCreateInfoSType,
					PNext = null,
					NegativeOneToOne = useNativeNegativeOneToOneDepth,
				};

				if (useNativeNegativeOneToOneDepth)
					viewportState.PNext = &depthClipControlInfo;

				PipelineRasterizationStateCreateInfo rasterizer = new()
				{
					SType = StructureType.PipelineRasterizationStateCreateInfo,
					DepthClampEnable = Vk.False,
					RasterizerDiscardEnable = Vk.False,
					PolygonMode = PolygonMode.Fill,
					CullMode = effectiveDraw.CullMode,
					FrontFace = effectiveDraw.FrontFace,
					DepthBiasEnable = Vk.False,
					LineWidth = 1.0f,
				};

				PipelineMultisampleStateCreateInfo multisampling = new()
				{
					SType = StructureType.PipelineMultisampleStateCreateInfo,
					RasterizationSamples = effectiveDraw.RasterizationSamples,
					SampleShadingEnable = Vk.False,
					AlphaToCoverageEnable = effectiveDraw.AlphaToCoverageEnabled ? Vk.True : Vk.False,
				};

				PipelineDepthStencilStateCreateInfo depthStencil = new()
				{
					SType = StructureType.PipelineDepthStencilStateCreateInfo,
					DepthTestEnable = effectiveDraw.DepthTestEnabled ? Vk.True : Vk.False,
					DepthWriteEnable = effectiveDraw.DepthWriteEnabled ? Vk.True : Vk.False,
					DepthCompareOp = effectiveDraw.DepthCompareOp,
					DepthBoundsTestEnable = Vk.False,
					StencilTestEnable = effectiveDraw.StencilTestEnabled ? Vk.True : Vk.False,
					Front = effectiveDraw.FrontStencilState,
					Back = effectiveDraw.BackStencilState,
				};

				PipelineColorBlendAttachmentState colorBlendAttachment = new()
				{
					ColorWriteMask = effectiveDraw.ColorWriteMask,
					BlendEnable = effectiveDraw.BlendEnabled ? Vk.True : Vk.False,
					ColorBlendOp = effectiveDraw.ColorBlendOp,
					AlphaBlendOp = effectiveDraw.AlphaBlendOp,
					SrcColorBlendFactor = effectiveDraw.SrcColorBlendFactor,
					DstColorBlendFactor = effectiveDraw.DstColorBlendFactor,
					SrcAlphaBlendFactor = effectiveDraw.SrcAlphaBlendFactor,
					DstAlphaBlendFactor = effectiveDraw.DstAlphaBlendFactor,
				};

				uint colorAttachmentCount = useDynamicRendering
					? (colorAttachmentFormat != Format.Undefined ? 1u : 0u)
					: Renderer.GetRenderPassColorAttachmentCount(renderPass);

				Debug.VulkanEvery(
					$"Vulkan.Pipeline.CacheMiss.{_program!.Data?.Name ?? "Unknown"}.{renderPass.Handle:X}.{colorAttachmentCount}",
					TimeSpan.FromSeconds(2),
					"[Vulkan] Pipeline cache miss: program='{0}' dynRendering={1} renderPass=0x{2:X} colorCount={3}",
					_program!.Data?.Name ?? "Unknown",
					useDynamicRendering,
					renderPass.Handle,
					colorAttachmentCount);

				PipelineColorBlendAttachmentState[] blendAttachments = colorAttachmentCount == 0
					? Array.Empty<PipelineColorBlendAttachmentState>()
					: new PipelineColorBlendAttachmentState[colorAttachmentCount];

				for (int i = 0; i < blendAttachments.Length; i++)
				{
					PipelineColorBlendAttachmentState attachmentBlend = colorBlendAttachment;
					Format attachmentFormat = useDynamicRendering
						? colorAttachmentFormat
						: Renderer.GetRenderPassColorAttachmentFormat(renderPass, (uint)i);
					if (!Renderer.SupportsColorAttachmentBlend(attachmentFormat))
						attachmentBlend.BlendEnable = Vk.False;

					blendAttachments[i] = attachmentBlend;
				}

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

						VkRenderProgram program = _program ?? throw new InvalidOperationException("Graphics program was not initialized.");

						try
						{
							pipeline = program.CreateGraphicsPipeline(ref pipelineInfo, Renderer.ActivePipelineCache);
						}
						catch (InvalidOperationException ex)
						{
							string programName = program.Data.Name ?? "UnnamedProgram";
							string shaderStages = program.DescribeShaderStages();
							program.WriteShaderDiagnostics($"pipelineName='{pipelineName}' passIndex={passIndex} topology={topology} failed: {ex.Message}");
							Debug.VulkanWarningEvery(
								$"Vulkan.Pipeline.CreateFailed.{programName}",
								TimeSpan.FromSeconds(5),
								"[Vulkan] Pipeline creation failed for program '{0}' mesh='{1}' material='{2}' stages=[{3}]: {4}",
								programName,
								Mesh?.Name ?? "<unnamed mesh>",
								material.Name ?? "<unnamed material>",
								shaderStages,
								ex.Message);
							pipeline = default;
							return false;
						}
						_pipelines[key] = pipeline;
						_pipelineDirty = false;
						success = pipeline.Handle != 0;
					}
				}
			}

			return success;
		}

		private static PendingMeshDraw ResolveAttachmentCompatibleDrawState(
			in PendingMeshDraw draw,
			int passIndex,
			IReadOnlyCollection<RenderPassMetadata>? passMetadata,
			bool depthStencilReadOnly)
		{
			if (!depthStencilReadOnly && !PassUsesReadOnlyDepthStencil(passIndex, passMetadata))
				return draw;

			bool hasStencilWrites = draw.StencilTestEnabled &&
				(StencilStateWrites(draw.FrontStencilState) || StencilStateWrites(draw.BackStencilState) || draw.StencilWriteMask != 0);
			if (!draw.DepthWriteEnabled && !hasStencilWrites)
				return draw;

			return draw with
			{
				DepthWriteEnabled = false,
				FrontStencilState = MakeStencilReadOnly(draw.FrontStencilState),
				BackStencilState = MakeStencilReadOnly(draw.BackStencilState),
				StencilWriteMask = 0,
			};
		}

		private void RefreshClipDepthPipelinePolicy()
		{
			int shaderConfigVersion = RuntimeEngine.Rendering.Settings.ShaderConfigVersion;
			bool usesShaderClipDepthRemap = RuntimeEngine.Rendering.ShouldUseVulkanShaderClipDepthRemap;
			bool usesNativeDepthClipControl = RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl;
			if (_pipelineShaderConfigVersion == shaderConfigVersion &&
				_pipelineUsesShaderClipDepthRemap == usesShaderClipDepthRemap &&
				_pipelineUsesNativeDepthClipControl == usesNativeDepthClipControl)
				return;

			_pipelineShaderConfigVersion = shaderConfigVersion;
			_pipelineUsesShaderClipDepthRemap = usesShaderClipDepthRemap;
			_pipelineUsesNativeDepthClipControl = usesNativeDepthClipControl;
			_pipelineDirty = true;
			_descriptorDirty = true;
		}

		private static bool PassUsesReadOnlyDepthStencil(
			int passIndex,
			IReadOnlyCollection<RenderPassMetadata>? passMetadata)
		{
			if (passMetadata is null || passIndex < 0)
				return false;

			foreach (RenderPassMetadata pass in passMetadata)
			{
				if (pass.PassIndex != passIndex)
					continue;

				foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
				{
					if (usage.Access != ERenderGraphAccess.Read)
						continue;

					if (usage.ResourceType is ERenderPassResourceType.DepthAttachment or ERenderPassResourceType.StencilAttachment)
						return true;
				}
			}

			return false;
		}

		private static bool StencilStateWrites(StencilOpState state)
			=> state.WriteMask != 0 &&
			   (state.FailOp != Silk.NET.Vulkan.StencilOp.Keep ||
			    state.PassOp != Silk.NET.Vulkan.StencilOp.Keep ||
			    state.DepthFailOp != Silk.NET.Vulkan.StencilOp.Keep);

		private static StencilOpState MakeStencilReadOnly(StencilOpState state)
			=> new()
			{
				FailOp = Silk.NET.Vulkan.StencilOp.Keep,
				PassOp = Silk.NET.Vulkan.StencilOp.Keep,
				DepthFailOp = Silk.NET.Vulkan.StencilOp.Keep,
				CompareOp = state.CompareOp,
				CompareMask = state.CompareMask,
				WriteMask = 0,
				Reference = state.Reference,
			};

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
					Renderer.RetirePipeline(pipe);
			}

			_pipelines.Clear();
		}

		private void DestroyGeneratedPrograms()
		{
			foreach (GeneratedProgramCacheEntry entry in _programCache.Values)
				entry.Data.Destroy();

			_programCache.Clear();
			_program = null;
			_generatedProgram = null;
			_activeProgramIdentity = null;
		}

		#endregion // Pipeline Management
	}
}
