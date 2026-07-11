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
			var sourceShaders = new List<XRShader>();
			string? generatedVertexIdentity = null;

			foreach (var shader in material.Shaders)
			{
				if (shader is null)
					continue;
				sourceShaders.Add(shader);
			}

			bool hasNoVertexShaders = material.VertexShaders.Count == 0;
			XRShader? suppliedVertexShader = hasNoVertexShaders
				? null
				: FindVertexShader(sourceShaders, Data.VertexShaderSelector);

			XRShader vertexShader;
			if (suppliedVertexShader is not null)
			{
				vertexShader = suppliedVertexShader;
			}
			else
			{
				string? vsSource = Data.VertexShaderSource;
				if (string.IsNullOrWhiteSpace(vsSource))
				{
					Debug.RenderingWarningEvery(
						$"Vulkan.MeshRenderer.{GetHashCode()}.MissingVertexShader",
						TimeSpan.FromSeconds(2),
						"[Vulkan] MeshRenderer '{0}' cannot render: no compatible vertex shader. Material='{1}' Mesh='{2}' Version='{3}'",
						MeshRenderer?.Name ?? "<unnamed>",
						material?.Name ?? "<unnamed material>",
						Mesh?.Name ?? "<unnamed mesh>",
						Data.VersionKindLabel);
					return false;
				}

				generatedVertexIdentity = XRRenderProgramDescriptor.BuildGeneratedSourceIdentity(vsSource);
				vertexShader = GenerateVertexShader(vsSource);
			}

			List<XRShader> shaders = BuildCombinedShaderList(sourceShaders, vertexShader);
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
					vertexLayoutIdentity: BuildCombinedProgramVertexLayoutIdentity(generatedVertexIdentity),
					topologyKind: "VulkanCombinedMesh");

				XRRenderProgram generatedProgram = new(linkNow: false, separable: false, shaders)
				{
					Name = generatedProgramName,
					UsageTag = $"VulkanCombinedMeshProgram | variant={Data.VersionKindLabel} | material={material.Name ?? "<unnamed>"} | mesh={Mesh?.Name ?? "<unnamed>"} | renderer={MeshRenderer?.Name ?? "<unnamed>"} | axes={generatedProgramAxes}",
					Priority = Data.ProgramPriority,
					ProgramDescriptor = descriptor,
				};
				generatedProgram.SetShaderProgramDiagnosticMetadata(new XRRenderProgram.ShaderProgramDiagnosticMetadata(
					material.Name,
					MeshRenderer?.Name,
					Data.VersionKindLabel,
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
				_vertexInputStateDirty = true;
			}

			_generatedProgram = entry.Data;
			_program = entry.Program;
			_program.Generate();
			bool linked = _program.Link(MeshRenderer?.GenerateAsync ?? false);
			if (!linked)
			{
				XRRenderProgram.ShaderProgramBackendStatus backend = _program.Data.ShaderMetadata.Backend;
				if (backend.Stage == XRRenderProgram.EShaderProgramBackendStage.Failed)
				{
					Debug.VulkanWarningEvery(
						$"Vulkan.MeshRenderer.{GetHashCode()}.ProgramLinkFailed",
						TimeSpan.FromSeconds(2),
						"[Vulkan] MeshRenderer '{0}' program link failed. Program='{1}' reason='{2}' detail='{3}'",
						MeshRenderer?.Name ?? "<unnamed>",
						_generatedProgram?.Name ?? "<unnamed program>",
						backend.FailureReason ?? "<none>",
						backend.Detail ?? "<none>");
				}
			}

			return linked;
		}

		private static List<XRShader> BuildCombinedShaderList(IReadOnlyList<XRShader> sourceShaders, XRShader vertexShader)
		{
			List<XRShader> shaders = new(sourceShaders.Count + 1);
			foreach (XRShader shader in sourceShaders)
				if (shader.Type != EShaderType.Vertex)
					shaders.Add(shader);

			shaders.Add(vertexShader);
			return shaders;
		}

		private static XRShader? FindVertexShader(IEnumerable<XRShader> shaders, Func<XRShader, bool> vertexShaderSelector)
		{
			foreach (XRShader shader in shaders)
				if (shader.Type == EShaderType.Vertex && vertexShaderSelector(shader))
					return shader;

			return null;
		}

		private string BuildCombinedProgramVertexLayoutIdentity(string? generatedVertexIdentity)
			=> string.Concat(
				Data.GetType().Name,
				"|",
				Data.VersionKindLabel,
				"|generated=",
				generatedVertexIdentity ?? string.Empty);

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
				";shaderIdentity=",
				BuildShaderIdentityList(material, generatedVertexIdentity),
				";uberVariant=",
				material.ActiveUberVariant.VariantHash.ToString("X16"),
				";axes=",
				generatedProgramAxes,
				";stages=",
				shaderStageList,
				";generatedVertex=",
				generatedVertexIdentity ?? string.Empty);

		private static string BuildShaderIdentityList(XRMaterial material, string? generatedVertexIdentity)
		{
			if (material.Shaders.Count == 0)
				return generatedVertexIdentity ?? "no-shaders";

			string identity = string.Join(",", material.Shaders
				.Where(static shader => shader is not null)
				.Select(static shader => BuildShaderIdentity(shader)));

			return identity.Length == 0
				? generatedVertexIdentity ?? "no-shaders"
				: identity;
		}

		private static string BuildShaderIdentity(XRShader shader)
		{
			string sourcePath = shader.Source?.FilePath ?? shader.FilePath ?? string.Empty;
			string sourceText = shader.Source?.Text ?? string.Empty;
			int sourceTextHash = StringComparer.Ordinal.GetHashCode(sourceText);
			string variantHash = shader.GeneratedUberVariantHash != 0
				? shader.GeneratedUberVariantHash.ToString("X16")
				: string.Empty;

			return string.Concat(
				shader.Type,
				":",
				RuntimeHelpers.GetHashCode(shader).ToString("X8"),
				":",
				sourcePath,
				":len=",
				sourceText.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
				":src=",
				sourceTextHash.ToString("X8"),
				":var=",
				variantHash);
		}

		private string BuildGeneratedProgramName(XRMaterial material, IReadOnlyList<XRShader> shaders)
			=> $"VkCombined:{SanitizeProgramName(material.Name, "material")}:{SanitizeProgramName(Mesh?.Name, "mesh")}:{BuildGeneratedProgramAxes(material)}:{BuildShaderStageList(shaders)}";

		private string BuildGeneratedProgramAxes(XRMaterial material)
		{
			XRMesh? mesh = Mesh;
			bool useComputeSkinning = mesh?.HasSkinning == true &&
				RuntimeEngine.Rendering.Settings.AllowSkinning &&
				RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader &&
				!RuntimeEngine.Rendering.State.IsVulkan;
			bool useComputeBlendshapes = mesh?.BlendshapeCount > 0 &&
				RuntimeEngine.Rendering.Settings.AllowBlendshapes &&
				!RuntimeEngine.Rendering.State.IsVulkan &&
				(RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || useComputeSkinning);
			bool usePrecombinedBlendshapes = RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass &&
				!RuntimeEngine.Rendering.State.IsVulkan;

			return string.Join(";",
				$"shaderConfig={RuntimeEngine.Rendering.Settings.ShaderConfigVersion}",
				$"skinning={mesh?.HasSkinning == true}",
				$"computeSkinning={useComputeSkinning}",
				$"blendshapes={mesh?.BlendshapeCount > 0}",
				$"computeBlendshapes={useComputeBlendshapes}",
				$"precombineBlendshapes={usePrecombinedBlendshapes}",
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
			lock (_bufferStateSync)
			{
				if (!_vertexInputStateDirty)
					return;

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

				// A vertex stage that reflects zero input attributes (e.g. the fullscreen
				// triangle, which builds clip positions from gl_VertexID) consumes no vertex
				// buffers. Emitting bindings/attributes for the mesh's streams anyway makes the
				// validation layer flag "Vertex attribute at location 0 not consumed by vertex
				// shader" on every pipeline creation. Bind nothing and use the attribute-less
				// draw path instead.
				if (_program is not null
					&& _program.TryGetVertexStageInputCount(out int vertexStageInputCount)
					&& vertexStageInputCount == 0)
				{
					_vertexBindings = [];
					_vertexAttributes = [];
					_geometryLayoutSignature = MeshGeometryLayoutSignatureBuilder.Create(
						Mesh,
						MeshRenderer,
						layoutBuffers,
						ResolvePrimaryIndexSizeForLayout(out bool hasIndexBuffersNoInputs),
						hasIndexBuffersNoInputs,
						hasIndexBuffersNoInputs ? "IndexBuffer" : "VertexCount");
					_vertexInputStateDirty = false;
					return;
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

				_vertexInputStateDirty = false;
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
			DynamicRenderingFormatSignature dynamicRenderingFormats,
			int passIndex,
			IReadOnlyCollection<RenderPassMetadata>? passMetadata,
			bool depthStencilReadOnly,
			string pipelineName,
			out Pipeline pipeline)
		{
			pipeline = default;

			RefreshClipDepthPipelinePolicy();

			if (draw.PreparedProgram is { } preparedProgram)
				ActivateCapturedProgram(material, preparedProgram, draw.PreparedProgramIdentity);
			else if (!EnsureProgram(material))
				return false;

			bool pipelineInvalidated = _pipelineDirty;
			uint colorAttachmentCount = useDynamicRendering
				? dynamicRenderingFormats.ColorAttachmentCount
				: Renderer.GetRenderPassColorAttachmentCount(renderPass);
			PendingMeshDraw effectiveDraw = ResolveAttachmentCompatibleDrawState(
				draw,
				passIndex,
				passMetadata,
				depthStencilReadOnly,
				colorAttachmentCount);

			BuildVertexInputState();

			ulong programPipelineHash = _program!.ComputeGraphicsPipelineFingerprint();
			ulong vertexLayoutHash = ComputeVertexLayoutHash();
			ulong descriptorLayoutHash = ComputeDescriptorSchemaFingerprint(
				_program.DescriptorBindings,
				_program.DescriptorSetLayouts.Count);
			ulong passMetadataHash = ComputePassMetadataHash(passMetadata, passIndex);
			ulong featureProfileHash = ComputeFeatureProfileHash();
			bool useNativeNegativeOneToOneDepth = RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl;

			PipelineKey key = new(
				topology,
				useDynamicRendering,
				useDynamicRendering ? 0UL : renderPass.Handle,
				useDynamicRendering ? dynamicRenderingFormats : default,
				programPipelineHash,
				vertexLayoutHash,
				descriptorLayoutHash,
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
				Math.Max(effectiveDraw.ViewportScissorCount, 1u),
				useNativeNegativeOneToOneDepth);

			if (pipelineInvalidated && _pipelines.Count > 256)
			{
				// Graphics pipeline handles are renderer-cache owned. Trimming this local
				// lookup must not tear down descriptor/uniform generations: command buffers
				// for another output may already reference them, and the descriptors remain
				// structurally valid across a local pipeline lookup-cache trim.
				_pipelines.Clear();
			}

			// Check pipeline cache before creating a new pipeline object
			if (_pipelines.TryGetValue(key, out pipeline) && pipeline.Handle != 0)
			{
				RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheLookup(cacheHit: true);
				_pipelineDirty = false;
				return true;
			}

			if (Renderer.TryGetSharedGraphicsPipeline(key, out pipeline))
			{
				RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheLookup(cacheHit: true);
				_pipelines[key] = pipeline;
				_pipelineDirty = false;
				return true;
			}

			PipelineInputAssemblyStateCreateInfo inputAssembly = new()
			{
				SType = StructureType.PipelineInputAssemblyStateCreateInfo,
				Topology = topology,
				PrimitiveRestartEnable = Vk.False,
			};

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

			PipelineColorBlendAttachmentState[] blendAttachments = colorAttachmentCount == 0
				? Array.Empty<PipelineColorBlendAttachmentState>()
				: new PipelineColorBlendAttachmentState[colorAttachmentCount];

			for (int i = 0; i < blendAttachments.Length; i++)
			{
				PipelineColorBlendAttachmentState attachmentBlend = colorBlendAttachment;
				Format attachmentFormat = useDynamicRendering
					? dynamicRenderingFormats.GetColorAttachmentFormat((uint)i)
					: Renderer.GetRenderPassColorAttachmentFormat(renderPass, (uint)i);
				if (!Renderer.SupportsColorAttachmentBlend(attachmentFormat))
					attachmentBlend.BlendEnable = Vk.False;

				blendAttachments[i] = attachmentBlend;
			}

			DynamicState[] dynamicStates =
			[
				DynamicState.Viewport,
				DynamicState.Scissor,
			];

			VkRenderProgram program = _program ?? throw new InvalidOperationException("Graphics program was not initialized.");
			GraphicsPipelineBuildRequest request;
			try
			{
				request = CreateGraphicsPipelineBuildRequest(
					program,
					key,
					pipelineName,
					colorAttachmentCount,
					inputAssembly,
					Math.Max(effectiveDraw.ViewportScissorCount, 1u),
					useNativeNegativeOneToOneDepth,
					rasterizer,
					multisampling,
					depthStencil,
					blendAttachments,
					dynamicStates,
					renderPass,
					useDynamicRendering,
					dynamicRenderingFormats);
			}
			catch (InvalidOperationException ex)
			{
				ReportPipelineCreateFailure(program, material, pipelineName, passIndex, topology, ex);
				return false;
			}

			if (Renderer.IsVulkanPipelineAsyncCompilationEnabled)
			{
				if (Renderer.TryTakeCompletedVulkanGraphicsPipeline(request.CompileKey, out VulkanGraphicsPipelineCompileResult asyncResult))
				{
					if (!asyncResult.Success || asyncResult.Pipeline.Handle == 0)
					{
						Debug.VulkanWarningEvery(
							$"Vulkan.Pipeline.AsyncCreateFailed.{program.Data.Name ?? "UnknownProgram"}",
							TimeSpan.FromSeconds(5),
							"[Vulkan] Async pipeline creation failed for program '{0}' mesh='{1}' material='{2}' after {3:F2} ms: {4}",
							program.Data.Name ?? "<unnamed program>",
							Mesh?.Name ?? "<unnamed mesh>",
							material.Name ?? "<unnamed material>",
							asyncResult.CompileMilliseconds,
							asyncResult.ErrorMessage ?? "<no detail>");
						return false;
					}

					pipeline = Renderer.StoreOrRetireSharedGraphicsPipeline(key, asyncResult.Pipeline);
					_pipelines[key] = pipeline;
					_pipelineDirty = false;
					return true;
				}

				if (Renderer.IsVulkanGraphicsPipelineCompileInFlight(request.CompileKey))
				{
					_pipelineDirty = true;
					return false;
				}

				if (!Renderer.TryEnqueueVulkanGraphicsPipelineCompile(request, out string rejectReason))
				{
					Debug.VulkanEvery(
						$"Vulkan.Pipeline.AsyncEnqueueRejected.{program.Data.Name ?? "UnknownProgram"}",
						TimeSpan.FromSeconds(2),
						"[Vulkan] Async graphics pipeline enqueue skipped for program='{0}' pipeline='{1}': {2}",
						program.Data.Name ?? "<unnamed program>",
						pipelineName,
						rejectReason);
					_pipelineDirty = true;
					return false;
				}

				RecordGraphicsPipelineCacheMiss(
					passIndex,
					passMetadata,
					pipelineName,
					Mesh?.Name,
					material,
					program.Data?.Name,
					topology,
					useDynamicRendering,
					renderPass,
					dynamicRenderingFormats,
					programPipelineHash,
					vertexLayoutHash,
					descriptorLayoutHash,
					colorAttachmentCount,
					key,
					effectiveDraw);

				_pipelineDirty = true;
				return false;
			}

			RecordGraphicsPipelineCacheMiss(
				passIndex,
				passMetadata,
				pipelineName,
				Mesh?.Name,
				material,
				program.Data?.Name,
				topology,
				useDynamicRendering,
				renderPass,
				dynamicRenderingFormats,
				programPipelineHash,
				vertexLayoutHash,
				descriptorLayoutHash,
				colorAttachmentCount,
				key,
				effectiveDraw);

			try
			{
				pipeline = CreateGraphicsPipelineFromRequest(request, Renderer.ActivePipelineCache, backgroundCompile: false);
			}
			catch (InvalidOperationException ex)
			{
				ReportPipelineCreateFailure(program, material, pipelineName, passIndex, topology, ex);
				pipeline = default;
				return false;
			}

			pipeline = Renderer.StoreOrRetireSharedGraphicsPipeline(key, pipeline);
			_pipelines[key] = pipeline;
			_pipelineDirty = false;
			return pipeline.Handle != 0;
		}

		private void RecordGraphicsPipelineCacheMiss(
			int passIndex,
			IReadOnlyCollection<RenderPassMetadata>? passMetadata,
			string pipelineName,
			string? meshName,
			XRMaterial material,
			string? programName,
			PrimitiveTopology topology,
			bool useDynamicRendering,
			RenderPass renderPass,
			DynamicRenderingFormatSignature dynamicRenderingFormats,
			ulong programPipelineHash,
			ulong vertexLayoutHash,
			ulong descriptorLayoutHash,
			uint colorAttachmentCount,
			in PipelineKey key,
			in PendingMeshDraw effectiveDraw)
		{
			Renderer.RecordVulkanGraphicsPipelineCacheMiss(
				passIndex,
				passMetadata,
				pipelineName,
				meshName,
				material,
				programName,
				topology,
				useDynamicRendering,
				renderPass,
				dynamicRenderingFormats,
				programPipelineHash,
				vertexLayoutHash,
				effectiveDraw.RasterizationSamples,
				effectiveDraw.DepthTestEnabled,
				effectiveDraw.BlendEnabled,
				effectiveDraw.AlphaToCoverageEnabled,
				effectiveDraw.ColorWriteMask);

			uint keyHash = unchecked((uint)key.GetHashCode());
			Debug.VulkanEvery(
				$"Vulkan.Pipeline.CacheMiss.{programName ?? "Unknown"}.{keyHash:X8}",
				TimeSpan.FromSeconds(2),
				"[Vulkan] Pipeline cache miss: key=0x{0:X8} program='{1}' dynRendering={2} renderPass=0x{3:X} colorCount={4} programHash=0x{5:X16} vertexLayout=0x{6:X16} descriptorLayout=0x{7:X16} depthTest={8} depthWrite={9} depthCompare={10} blend={11} atc={12} cull={13}",
				keyHash,
				programName ?? "Unknown",
				useDynamicRendering,
				renderPass.Handle,
				colorAttachmentCount,
				programPipelineHash,
				vertexLayoutHash,
				descriptorLayoutHash,
				effectiveDraw.DepthTestEnabled,
				effectiveDraw.DepthWriteEnabled,
				effectiveDraw.DepthCompareOp,
				effectiveDraw.BlendEnabled,
				effectiveDraw.AlphaToCoverageEnabled,
				effectiveDraw.CullMode);
		}

		private bool ShouldUseGraphicsPipelineLibraries()
			=> RuntimeEngine.Rendering.Settings.AllowShaderPipelines &&
			   Data.AllowShaderPipelines &&
			   Renderer.SupportsGraphicsPipelineLibrary;

		private GraphicsPipelineBuildRequest CreateGraphicsPipelineBuildRequest(
			VkRenderProgram program,
			PipelineKey key,
			string pipelineName,
			uint colorAttachmentCount,
			PipelineInputAssemblyStateCreateInfo inputAssembly,
			uint viewportScissorCount,
			bool nativeNegativeOneToOneDepth,
			PipelineRasterizationStateCreateInfo rasterizer,
			PipelineMultisampleStateCreateInfo multisampling,
			PipelineDepthStencilStateCreateInfo depthStencil,
			PipelineColorBlendAttachmentState[] blendAttachments,
			DynamicState[] dynamicStates,
			RenderPass renderPass,
			bool useDynamicRendering,
			DynamicRenderingFormatSignature dynamicRenderingFormats)
		{
			PipelineShaderStageCreateInfo[] graphicsStages = GetGraphicsPipelineLibraryStages(
				program,
				EProgramStageMask.VertexShaderBit |
				EProgramStageMask.TessControlShaderBit |
				EProgramStageMask.TessEvaluationShaderBit |
				EProgramStageMask.GeometryShaderBit |
				EProgramStageMask.TaskShaderBit |
				EProgramStageMask.MeshShaderBit |
				EProgramStageMask.FragmentShaderBit,
				colorAttachmentCount);

			if (graphicsStages.Length == 0)
				throw new InvalidOperationException("graphics pipeline creation requires at least one graphics shader stage.");

			PipelineShaderStageCreateInfo[] preRasterStages = GetGraphicsPipelineLibraryStages(
				program,
				EProgramStageMask.VertexShaderBit |
				EProgramStageMask.TessControlShaderBit |
				EProgramStageMask.TessEvaluationShaderBit |
				EProgramStageMask.GeometryShaderBit |
				EProgramStageMask.TaskShaderBit |
				EProgramStageMask.MeshShaderBit,
				colorAttachmentCount);

			PipelineShaderStageCreateInfo[] fragmentStages = GetGraphicsPipelineLibraryStages(
				program,
				EProgramStageMask.FragmentShaderBit,
				colorAttachmentCount);

			return new GraphicsPipelineBuildRequest(
				this,
				program,
				key,
				pipelineName,
				colorAttachmentCount,
				program.PipelineLayout,
				[.. _vertexBindings],
				[.. _vertexAttributes],
				inputAssembly,
				viewportScissorCount,
				nativeNegativeOneToOneDepth,
				rasterizer,
				multisampling,
				depthStencil,
				[.. blendAttachments],
				[.. dynamicStates],
				useDynamicRendering ? default : renderPass,
				useDynamicRendering ? dynamicRenderingFormats : default,
				graphicsStages,
				preRasterStages,
				fragmentStages);
		}

		private void ReportPipelineCreateFailure(
			VkRenderProgram program,
			XRMaterial material,
			string pipelineName,
			int passIndex,
			PrimitiveTopology topology,
			InvalidOperationException ex)
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
		}

		internal Pipeline CreateGraphicsPipelineFromRequest(
			GraphicsPipelineBuildRequest request,
			PipelineCache pipelineCache,
			bool backgroundCompile)
		{
			PipelineVertexInputStateCreateInfo vertexInput = new()
			{
				SType = StructureType.PipelineVertexInputStateCreateInfo,
				VertexBindingDescriptionCount = (uint)request.VertexBindings.Length,
				VertexAttributeDescriptionCount = (uint)request.VertexAttributes.Length,
			};

			PipelineViewportStateCreateInfo viewportState = new()
			{
				SType = StructureType.PipelineViewportStateCreateInfo,
				ViewportCount = request.ViewportScissorCount,
				ScissorCount = request.ViewportScissorCount,
			};

			PipelineViewportDepthClipControlCreateInfoEXTNative depthClipControlInfo = new()
			{
				SType = VulkanDepthClipControlExt.PipelineViewportCreateInfoSType,
				PNext = null,
				NegativeOneToOne = request.NativeNegativeOneToOneDepth,
			};

			if (request.NativeNegativeOneToOneDepth)
				viewportState.PNext = &depthClipControlInfo;

			PipelineColorBlendStateCreateInfo colorBlending = new()
			{
				SType = StructureType.PipelineColorBlendStateCreateInfo,
				LogicOpEnable = Vk.False,
				LogicOp = LogicOp.Copy,
				AttachmentCount = (uint)request.BlendAttachments.Length,
			};

			fixed (VertexInputBindingDescription* bindingsPtr = request.VertexBindings)
			fixed (VertexInputAttributeDescription* attrsPtr = request.VertexAttributes)
			fixed (PipelineColorBlendAttachmentState* blendPtr = request.BlendAttachments)
			fixed (DynamicState* dynPtr = request.DynamicStates)
			{
				vertexInput.PVertexBindingDescriptions = request.VertexBindings.Length > 0 ? bindingsPtr : null;
				vertexInput.PVertexAttributeDescriptions = request.VertexAttributes.Length > 0 ? attrsPtr : null;
				colorBlending.PAttachments = request.BlendAttachments.Length > 0 ? blendPtr : null;

				PipelineDynamicStateCreateInfo dynamicState = new()
				{
					SType = StructureType.PipelineDynamicStateCreateInfo,
					DynamicStateCount = (uint)request.DynamicStates.Length,
					PDynamicStates = request.DynamicStates.Length > 0 ? dynPtr : null,
				};

				PipelineInputAssemblyStateCreateInfo inputAssembly = request.InputAssembly;
				PipelineRasterizationStateCreateInfo rasterizer = request.Rasterizer;
				PipelineMultisampleStateCreateInfo multisampling = request.Multisampling;
				PipelineDepthStencilStateCreateInfo depthStencil = request.DepthStencil;

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
					RenderPass = request.Key.UseDynamicRendering ? default : request.RenderPass,
					Subpass = 0,
				};

				if (request.Key.UseDynamicRendering)
				{
					Format* colorFormats = stackalloc Format[(int)request.ColorAttachmentCount];
					request.DynamicRenderingFormats.CopyColorAttachmentFormats(colorFormats, request.ColorAttachmentCount);

					PipelineRenderingCreateInfo renderingInfo = new()
					{
						SType = StructureType.PipelineRenderingCreateInfo,
						ViewMask = request.DynamicRenderingFormats.ViewMask,
						ColorAttachmentCount = request.ColorAttachmentCount,
						PColorAttachmentFormats = request.ColorAttachmentCount > 0 ? colorFormats : null,
						DepthAttachmentFormat = request.DynamicRenderingFormats.DepthAttachmentFormat,
						StencilAttachmentFormat = request.DynamicRenderingFormats.StencilAttachmentFormat,
					};

					pipelineInfo.PNext = &renderingInfo;
					return CreateGraphicsPipeline(request, ref pipelineInfo, pipelineCache, backgroundCompile);
				}

				return CreateGraphicsPipeline(request, ref pipelineInfo, pipelineCache, backgroundCompile);
			}
		}

		private Pipeline CreateGraphicsPipeline(
			GraphicsPipelineBuildRequest request,
			ref GraphicsPipelineCreateInfo pipelineInfo,
			PipelineCache pipelineCache,
			bool backgroundCompile)
		{
			if (request.Key.UseDynamicRendering && request.ColorAttachmentCount == 0)
			{
				Debug.VulkanWarningEvery(
					"Vulkan.PipelineLibrary.DepthOnlyMonolithic",
					TimeSpan.FromSeconds(5),
					"[Vulkan] Using monolithic dynamic-rendering pipeline for depth-only pass '{0}' program='{1}'; graphics pipeline libraries are bypassed for zero-color pipelines to keep depth/stencil validation correct.",
					request.PipelineName,
					request.Program.Data.Name ?? "<unnamed program>");
				return CreateMonolithicGraphicsPipeline(request, ref pipelineInfo, pipelineCache);
			}

			if (!ShouldUseGraphicsPipelineLibraries())
				return CreateMonolithicGraphicsPipeline(request, ref pipelineInfo, pipelineCache);

			try
			{
				return CreateGraphicsPipelineFromLibraries(request, ref pipelineInfo, pipelineCache);
			}
			catch (InvalidOperationException ex)
			{
				Debug.VulkanWarningEvery(
					$"Vulkan.PipelineLibrary.Fallback.{request.Program.Data.Name ?? "UnknownProgram"}",
					TimeSpan.FromSeconds(5),
					"[Vulkan] Graphics pipeline library creation failed for pipeline '{0}' program='{1}'; falling back to monolithic pipeline. {2}",
					request.PipelineName,
					request.Program.Data.Name ?? "<unnamed program>",
					ex.Message);
				return CreateMonolithicGraphicsPipeline(request, ref pipelineInfo, pipelineCache);
			}
		}

		private Pipeline CreateMonolithicGraphicsPipeline(
			GraphicsPipelineBuildRequest request,
			ref GraphicsPipelineCreateInfo pipelineInfo,
			PipelineCache pipelineCache)
		{
			if (request.GraphicsStages.Length == 0)
				throw new InvalidOperationException("graphics pipeline creation requires at least one graphics shader stage.");

			fixed (PipelineShaderStageCreateInfo* stagesPtr = request.GraphicsStages)
			{
				pipelineInfo.StageCount = (uint)request.GraphicsStages.Length;
				pipelineInfo.PStages = stagesPtr;
				pipelineInfo.Layout = request.PipelineLayout;

				Result result = Api!.CreateGraphicsPipelines(Device, pipelineCache, 1, ref pipelineInfo, null, out Pipeline pipeline);
				if (result != Result.Success)
					throw new InvalidOperationException($"failed to create graphics pipeline ({result}).");

				Renderer.RegisterVulkanPipeline(pipeline, "VkMeshRenderer.Graphics");
				Renderer.NotifyVulkanPipelineCreated("graphics");
				return pipeline;
			}
		}

		private Pipeline CreateGraphicsPipelineFromLibraries(
			GraphicsPipelineBuildRequest request,
			ref GraphicsPipelineCreateInfo pipelineInfo,
			PipelineCache pipelineCache)
		{
			if (request.PreRasterStages.Length == 0)
				throw new InvalidOperationException("graphics pipeline libraries require a pre-rasterization shader stage.");

			if (!request.PreRasterStages.Any(static stage => stage.Stage == ShaderStageFlags.VertexBit))
				throw new InvalidOperationException("graphics pipeline library path currently supports vertex-input mesh pipelines only.");

			Pipeline vertexInput = EnsureGraphicsPipelineLibrary(
				request,
				CreateGraphicsPipelineLibraryKey(GraphicsPipelineLibrarySubset.VertexInputInterface, request.Key),
				ref pipelineInfo,
				Array.Empty<PipelineShaderStageCreateInfo>(),
				GraphicsPipelineLibraryFlagsEXT.VertexInputInterfaceBitExt,
				pipelineCache);

			Pipeline preRasterization = EnsureGraphicsPipelineLibrary(
				request,
				CreateGraphicsPipelineLibraryKey(GraphicsPipelineLibrarySubset.PreRasterizationShaders, request.Key),
				ref pipelineInfo,
				request.PreRasterStages,
				GraphicsPipelineLibraryFlagsEXT.PreRasterizationShadersBitExt,
				pipelineCache);

			List<Pipeline> libraries =
			[
				vertexInput,
				preRasterization,
			];

			if (request.FragmentStages.Length > 0)
			{
				Pipeline fragmentShader = EnsureGraphicsPipelineLibrary(
					request,
					CreateGraphicsPipelineLibraryKey(GraphicsPipelineLibrarySubset.FragmentShader, request.Key),
					ref pipelineInfo,
					request.FragmentStages,
					GraphicsPipelineLibraryFlagsEXT.FragmentShaderBitExt,
					pipelineCache);
				libraries.Add(fragmentShader);
			}

			Pipeline fragmentOutput = EnsureGraphicsPipelineLibrary(
				request,
				CreateGraphicsPipelineLibraryKey(GraphicsPipelineLibrarySubset.FragmentOutputInterface, request.Key),
				ref pipelineInfo,
				Array.Empty<PipelineShaderStageCreateInfo>(),
				GraphicsPipelineLibraryFlagsEXT.FragmentOutputInterfaceBitExt,
				pipelineCache);
			libraries.Add(fragmentOutput);

			Pipeline[] libraryArray = [.. libraries];
			fixed (Pipeline* librariesPtr = libraryArray)
			{
				PipelineLibraryCreateInfoKHR libraryInfo = new()
				{
					SType = StructureType.PipelineLibraryCreateInfoKhr,
					LibraryCount = (uint)libraryArray.Length,
					PLibraries = librariesPtr,
				};

				bool linkUsesDynamicRenderingInfo =
					request.Key.UseDynamicRendering &&
					pipelineInfo.PNext != null &&
					((PipelineRenderingCreateInfo*)pipelineInfo.PNext)->SType == StructureType.PipelineRenderingCreateInfo;
				PipelineRenderingCreateInfo linkedRenderingInfo = default;
				if (linkUsesDynamicRenderingInfo)
				{
					linkedRenderingInfo = *((PipelineRenderingCreateInfo*)pipelineInfo.PNext);
					linkedRenderingInfo.PNext = &libraryInfo;
				}

				GraphicsPipelineCreateInfo linkedInfo = pipelineInfo;
				linkedInfo.PNext = &libraryInfo;
				if (linkUsesDynamicRenderingInfo)
					linkedInfo.PNext = &linkedRenderingInfo;
				linkedInfo.StageCount = 0;
				linkedInfo.PStages = null;
				linkedInfo.PVertexInputState = null;
				linkedInfo.PInputAssemblyState = null;
				linkedInfo.PViewportState = null;
				linkedInfo.PRasterizationState = null;
				linkedInfo.PDynamicState = null;
				linkedInfo.Layout = request.PipelineLayout;

				long linkStart = global::System.Diagnostics.Stopwatch.GetTimestamp();
				Result result = Api!.CreateGraphicsPipelines(Device, pipelineCache, 1, ref linkedInfo, null, out Pipeline pipeline);
				TimeSpan linkElapsed = global::System.Diagnostics.Stopwatch.GetElapsedTime(linkStart);
				if (result != Result.Success)
					throw new InvalidOperationException($"failed to link graphics pipeline libraries ({result}) after {linkElapsed.TotalMilliseconds:F2} ms.");

				Renderer.RegisterVulkanPipeline(pipeline, "VkMeshRenderer.GraphicsLibraryLink");
				if (linkElapsed.TotalMilliseconds >= 16.0)
				{
					Debug.VulkanWarningEvery(
						$"Vulkan.PipelineLibrary.LinkSlow.{request.Program.Data.Name ?? "UnknownProgram"}",
						TimeSpan.FromSeconds(2),
						"[Vulkan] Graphics pipeline library link took {0:F2} ms: program='{1}' libraries={2} dynamicRendering={3} renderPass=0x{4:X}",
						linkElapsed.TotalMilliseconds,
						request.Program.Data.Name ?? "<unnamed program>",
						libraryArray.Length,
						request.Key.UseDynamicRendering,
						request.Key.RenderPassHandle);
				}

				Renderer.NotifyVulkanPipelineCreated("graphics-library-linked");
				return pipeline;
			}
		}

		private static GraphicsPipelineLibraryKey CreateGraphicsPipelineLibraryKey(
			GraphicsPipelineLibrarySubset subset,
			in PipelineKey pipeline)
		{
			bool hasRenderPassIdentity = subset is
				GraphicsPipelineLibrarySubset.PreRasterizationShaders or
				GraphicsPipelineLibrarySubset.FragmentShader or
				GraphicsPipelineLibrarySubset.FragmentOutputInterface;
			bool usesDynamicRenderingIdentity = hasRenderPassIdentity && pipeline.UseDynamicRendering;
			DynamicRenderingFormatSignature dynamicRenderingFormats = CreateGraphicsPipelineLibraryDynamicRenderingFormatSignature(subset, pipeline);
			bool hasTopology = subset == GraphicsPipelineLibrarySubset.VertexInputInterface;
			bool hasProgram = subset is GraphicsPipelineLibrarySubset.PreRasterizationShaders or GraphicsPipelineLibrarySubset.FragmentShader;
			bool hasVertexLayout = subset == GraphicsPipelineLibrarySubset.VertexInputInterface;
			bool hasDepthStencil = subset is GraphicsPipelineLibrarySubset.FragmentShader or GraphicsPipelineLibrarySubset.FragmentOutputInterface;
			bool hasRasterState = subset == GraphicsPipelineLibrarySubset.PreRasterizationShaders;
			bool hasBlendState = subset == GraphicsPipelineLibrarySubset.FragmentOutputInterface;
			bool hasSampleState = subset is GraphicsPipelineLibrarySubset.FragmentShader or GraphicsPipelineLibrarySubset.FragmentOutputInterface;

			return new GraphicsPipelineLibraryKey(
				subset,
				usesDynamicRenderingIdentity,
				hasRenderPassIdentity && !pipeline.UseDynamicRendering ? pipeline.RenderPassHandle : 0UL,
				usesDynamicRenderingIdentity ? dynamicRenderingFormats : default,
				hasTopology ? pipeline.Topology : default,
				hasProgram ? pipeline.ProgramPipelineHash : 0UL,
				hasVertexLayout ? pipeline.VertexLayoutHash : 0UL,
				hasProgram ? pipeline.DescriptorLayoutHash : 0UL,
				hasProgram || hasVertexLayout || hasRasterState ? pipeline.FeatureProfileHash : 0UL,
				hasSampleState ? pipeline.RasterizationSamples : default,
				hasDepthStencil && pipeline.DepthTestEnabled,
				hasDepthStencil && pipeline.DepthWriteEnabled,
				hasDepthStencil ? pipeline.DepthCompareOp : default,
				hasDepthStencil && pipeline.StencilTestEnabled,
				hasDepthStencil ? pipeline.FrontStencilState : default,
				hasDepthStencil ? pipeline.BackStencilState : default,
				hasDepthStencil ? pipeline.StencilWriteMask : 0u,
				hasRasterState ? pipeline.CullMode : default,
				hasRasterState ? pipeline.FrontFace : default,
				hasBlendState && pipeline.BlendEnabled,
				hasBlendState && pipeline.AlphaToCoverageEnabled,
				hasBlendState ? pipeline.ColorBlendOp : default,
				hasBlendState ? pipeline.AlphaBlendOp : default,
				hasBlendState ? pipeline.SrcColorBlendFactor : default,
				hasBlendState ? pipeline.DstColorBlendFactor : default,
				hasBlendState ? pipeline.SrcAlphaBlendFactor : default,
				hasBlendState ? pipeline.DstAlphaBlendFactor : default,
				hasBlendState ? pipeline.ColorWriteMask : default,
				hasRasterState ? Math.Max(pipeline.ViewportScissorCount, 1u) : 1u,
				hasRasterState && pipeline.NativeNegativeOneToOneDepth);
		}

		private static DynamicRenderingFormatSignature CreateGraphicsPipelineLibraryDynamicRenderingFormatSignature(
			GraphicsPipelineLibrarySubset subset,
			in PipelineKey pipeline)
		{
			if (!pipeline.UseDynamicRendering)
				return default;

			return subset switch
			{
				GraphicsPipelineLibrarySubset.PreRasterizationShaders or
				GraphicsPipelineLibrarySubset.FragmentShader => new DynamicRenderingFormatSignature(
					ReadOnlySpan<Format>.Empty,
					Format.Undefined,
					Format.Undefined,
					pipeline.DynamicRenderingFormats.ViewMask,
					pipeline.DynamicRenderingFormats.LayerCount),
				GraphicsPipelineLibrarySubset.FragmentOutputInterface => pipeline.DynamicRenderingFormats,
				_ => default,
			};
		}

		private Pipeline EnsureGraphicsPipelineLibrary(
			GraphicsPipelineBuildRequest request,
			GraphicsPipelineLibraryKey key,
			ref GraphicsPipelineCreateInfo baseInfo,
			PipelineShaderStageCreateInfo[] stages,
			GraphicsPipelineLibraryFlagsEXT libraryFlags,
			PipelineCache pipelineCache)
		{
			if (Renderer.TryGetSharedGraphicsPipelineLibrary(key, out Pipeline cachedLibrary))
				return cachedLibrary;

			fixed (PipelineShaderStageCreateInfo* stagesPtr = stages)
			{
				bool includeDynamicRenderingInfo = key.UseDynamicRendering;
				PipelineRenderingCreateInfo libraryRenderingInfo = default;
				uint libraryColorAttachmentCount = includeDynamicRenderingInfo
					? key.DynamicRenderingFormats.ColorAttachmentCount
					: 0u;
				Format* libraryColorFormats = stackalloc Format[(int)Math.Max(libraryColorAttachmentCount, 1u)];
				if (libraryColorAttachmentCount > 0u)
					key.DynamicRenderingFormats.CopyColorAttachmentFormats(libraryColorFormats, libraryColorAttachmentCount);

				if (includeDynamicRenderingInfo)
				{
					libraryRenderingInfo = new PipelineRenderingCreateInfo
					{
						SType = StructureType.PipelineRenderingCreateInfo,
						ViewMask = key.DynamicRenderingFormats.ViewMask,
						ColorAttachmentCount = libraryColorAttachmentCount,
						PColorAttachmentFormats = libraryColorAttachmentCount > 0u ? libraryColorFormats : null,
						DepthAttachmentFormat = key.DynamicRenderingFormats.DepthAttachmentFormat,
						StencilAttachmentFormat = key.DynamicRenderingFormats.StencilAttachmentFormat,
					};
				}

				GraphicsPipelineLibraryCreateInfoEXT libraryInfo = new()
				{
					SType = StructureType.GraphicsPipelineLibraryCreateInfoExt,
					PNext = includeDynamicRenderingInfo ? &libraryRenderingInfo : null,
					Flags = libraryFlags,
				};

				GraphicsPipelineCreateInfo libraryPipelineInfo = baseInfo;
				libraryPipelineInfo.Flags |= PipelineCreateFlags.CreateLibraryBitKhr;
				libraryPipelineInfo.PNext = &libraryInfo;
				libraryPipelineInfo.StageCount = (uint)stages.Length;
				libraryPipelineInfo.PStages = stages.Length > 0 ? stagesPtr : null;
				libraryPipelineInfo.Layout = request.PipelineLayout;

				ApplyGraphicsPipelineLibrarySubset(ref libraryPipelineInfo, key.Subset);

				long createStart = global::System.Diagnostics.Stopwatch.GetTimestamp();
				Result result = Api!.CreateGraphicsPipelines(Device, pipelineCache, 1, ref libraryPipelineInfo, null, out Pipeline library);
				TimeSpan createElapsed = global::System.Diagnostics.Stopwatch.GetElapsedTime(createStart);
				if (result != Result.Success)
					throw new InvalidOperationException($"failed to create {key.Subset} graphics pipeline library ({result}) after {createElapsed.TotalMilliseconds:F2} ms.");

				Renderer.RegisterVulkanPipeline(library, $"VkMeshRenderer.GraphicsLibrary.{key.Subset}");
				Pipeline cachedOrCreated = Renderer.StoreSharedGraphicsPipelineLibrary(key, library);
				if (cachedOrCreated.Handle != library.Handle)
				{
					Renderer.RetirePipeline(library);
					return cachedOrCreated;
				}

				if (createElapsed.TotalMilliseconds >= 16.0)
				{
					Debug.VulkanWarningEvery(
						$"Vulkan.PipelineLibrary.CreateSlow.{key.Subset}.{request.Program.Data.Name ?? "UnknownProgram"}",
						TimeSpan.FromSeconds(2),
						"[Vulkan] Graphics pipeline library create took {0:F2} ms: subset={1} program='{2}' dynamicRendering={3} renderPass=0x{4:X} colors={5} depth={6} stencil={7}",
						createElapsed.TotalMilliseconds,
						key.Subset,
						request.Program.Data.Name ?? "<unnamed program>",
						key.UseDynamicRendering,
						key.RenderPassHandle,
						key.DynamicRenderingFormats.DescribeColorFormats(),
						key.DynamicRenderingFormats.DepthAttachmentFormat,
						key.DynamicRenderingFormats.StencilAttachmentFormat);
				}

				Renderer.NotifyVulkanPipelineCreated($"graphics-library:{key.Subset}");
				return library;
			}
		}

		private static PipelineShaderStageCreateInfo[] GetGraphicsPipelineLibraryStages(
			VkRenderProgram program,
			EProgramStageMask mask,
			uint colorAttachmentCount)
		{
			PipelineShaderStageCreateInfo[] stages = program.GetShaderStages(mask).ToArray();
			if (colorAttachmentCount == 0)
				stages = stages.Where(static stage => stage.Stage != ShaderStageFlags.FragmentBit).ToArray();

			return stages;
		}

		private static void ApplyGraphicsPipelineLibrarySubset(
			ref GraphicsPipelineCreateInfo pipelineInfo,
			GraphicsPipelineLibrarySubset subset)
		{
			switch (subset)
			{
				case GraphicsPipelineLibrarySubset.VertexInputInterface:
					pipelineInfo.PViewportState = null;
					pipelineInfo.PRasterizationState = null;
					pipelineInfo.PMultisampleState = null;
					pipelineInfo.PDepthStencilState = null;
					pipelineInfo.PColorBlendState = null;
					pipelineInfo.PDynamicState = null;
					break;
				case GraphicsPipelineLibrarySubset.PreRasterizationShaders:
					pipelineInfo.PVertexInputState = null;
					pipelineInfo.PInputAssemblyState = null;
					pipelineInfo.PDepthStencilState = null;
					pipelineInfo.PColorBlendState = null;
					break;
				case GraphicsPipelineLibrarySubset.FragmentShader:
					pipelineInfo.PVertexInputState = null;
					pipelineInfo.PInputAssemblyState = null;
					pipelineInfo.PViewportState = null;
					pipelineInfo.PRasterizationState = null;
					pipelineInfo.PColorBlendState = null;
					pipelineInfo.PDynamicState = null;
					break;
				case GraphicsPipelineLibrarySubset.FragmentOutputInterface:
					pipelineInfo.StageCount = 0;
					pipelineInfo.PStages = null;
					pipelineInfo.PVertexInputState = null;
					pipelineInfo.PInputAssemblyState = null;
					pipelineInfo.PViewportState = null;
					pipelineInfo.PRasterizationState = null;
					pipelineInfo.PDynamicState = null;
					break;
			}
		}

		private static PendingMeshDraw ResolveAttachmentCompatibleDrawState(
			in PendingMeshDraw draw,
			int passIndex,
			IReadOnlyCollection<RenderPassMetadata>? passMetadata,
			bool depthStencilReadOnly,
			uint colorAttachmentCount)
		{
			PendingMeshDraw effective = draw;
			if (colorAttachmentCount == 0 &&
				(draw.ColorWriteMask != 0 || draw.BlendEnabled || draw.AlphaToCoverageEnabled))
			{
				effective = effective with
				{
					ColorWriteMask = 0,
					BlendEnabled = false,
					AlphaToCoverageEnabled = false,
					ColorBlendOp = default,
					AlphaBlendOp = default,
					SrcColorBlendFactor = default,
					DstColorBlendFactor = default,
					SrcAlphaBlendFactor = default,
					DstAlphaBlendFactor = default,
				};
			}

			if (!depthStencilReadOnly && !PassUsesReadOnlyDepthStencil(passIndex, passMetadata))
				return effective;

			bool hasStencilWrites = effective.StencilTestEnabled &&
				(StencilStateWrites(effective.FrontStencilState) || StencilStateWrites(effective.BackStencilState) || effective.StencilWriteMask != 0);
			if (!effective.DepthWriteEnabled && !hasStencilWrites)
				return effective;

			return effective with
			{
				DepthWriteEnabled = false,
				FrontStencilState = MakeStencilReadOnly(effective.FrontStencilState),
				BackStencilState = MakeStencilReadOnly(effective.BackStencilState),
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

				bool hasDepthStencilUsage = false;
				bool hasDepthStencilWriteUsage = false;
				foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
				{
					if (usage.ResourceType is ERenderPassResourceType.DepthAttachment or ERenderPassResourceType.StencilAttachment)
					{
						hasDepthStencilUsage = true;
						if (usage.Access is ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite)
							hasDepthStencilWriteUsage = true;
					}
				}

				return hasDepthStencilUsage && !hasDepthStencilWriteUsage;
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
		/// Clears local pipeline references and destroys associated descriptor resources.
		/// Final graphics pipeline handles are owned by the renderer-level shared cache.
		/// Called when the program/material/mesh changes require a full rebuild.
		/// </summary>
		private void DestroyPipelines()
		{
			DestroyDescriptors();

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
