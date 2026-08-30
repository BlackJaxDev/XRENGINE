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

internal unsafe partial class VkMeshRenderer
{
	private static long _nextPipelineCompileOwnerId;
	internal long PipelineCompileOwnerId { get; } =
		Interlocked.Increment(ref _nextPipelineCompileOwnerId);

	#region Shader Program Management

	/// <summary>
	/// Ensures a compiled and linked VkRenderProgram exists for the given material.
	/// If the material lacks a vertex shader, one is auto-generated from
	/// <c>Data.VertexShaderSource</c>. Returns false if linking fails.
	/// </summary>
	private bool EnsureProgram(XRMaterial material)
	{
		GeneratedProgramState programState = CaptureGeneratedProgramState(material);
		if (_programStateCache.TryGetValue(programState, out GeneratedProgramCacheEntry? cachedEntry))
			return ActivateGeneratedProgram(cachedEntry);

		var sourceShaders = new List<XRShader>(material.Shaders.Count);
		string? generatedVertexIdentity = null;

		for (int i = 0; i < material.Shaders.Count; i++)
		{
			XRShader? shader = material.Shaders[i];
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
			string? vsSource = programState.GeneratedVertexSource;
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
		string generatedProgramAxes = BuildGeneratedProgramAxes(programState);
		string shaderStageList = BuildShaderStageList(shaders);
		string generatedProgramName = BuildGeneratedProgramName(programState, generatedProgramAxes, shaderStageList);
		string programIdentity = BuildGeneratedProgramIdentity(programState, generatedProgramAxes, shaderStageList, generatedVertexIdentity);
		if (!_programCache.TryGetValue(programIdentity, out GeneratedProgramCacheEntry? entry))
		{
			XRRenderProgramDescriptor descriptor = XRRenderProgramDescriptor.FromShaders(
				shaders,
				separable: false,
				renderSettingsVersion: programState.ShaderConfigVersion,
				generatedVertexIdentity: generatedVertexIdentity,
				materialVariantKind: programState.MaterialVariantIsEmpty ? null : "MaterialVariant",
				materialVariantHash: programState.MaterialVariantHash,
				vertexLayoutIdentity: BuildCombinedProgramVertexLayoutIdentity(generatedVertexIdentity),
				topologyKind: "VulkanCombinedMesh");

			XRRenderProgram generatedProgram = new(linkNow: false, separable: false, shaders)
			{
				Name = generatedProgramName,
				UsageTag = $"VulkanCombinedMeshProgram | variant={programState.VersionKindLabel} | material={programState.MaterialName ?? "<unnamed>"} | mesh={programState.MeshName ?? "<unnamed>"} | renderer={programState.RendererName ?? "<unnamed>"} | axes={generatedProgramAxes}",
				Priority = programState.ProgramPriority,
				ProgramDescriptor = descriptor,
			};
			generatedProgram.SetShaderProgramDiagnosticMetadata(new XRRenderProgram.ShaderProgramDiagnosticMetadata(
				programState.MaterialName,
				programState.RendererName,
				programState.VersionKindLabel,
				"VulkanCombinedMesh",
				programState.MeshName,
				shaderStageList));
			generatedProgram.AllowLink();

            VkRenderProgram? vkProgram = WrapperLookup
                .GetOrCreate(generatedProgram, generateNow: true) as VkRenderProgram;
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
				Identity = programIdentity,
				Data = generatedProgram,
				Program = vkProgram,
			};
			_programCache[programIdentity] = entry;
		}

		_programStateCache[programState] = entry;
		return ActivateGeneratedProgram(entry);
	}

	private bool ActivateGeneratedProgram(GeneratedProgramCacheEntry entry)
	{
		bool replacingSameInterface =
			_program is not null &&
			!ReferenceEquals(_program, entry.Program) &&
			string.Equals(
				_activeProgramIdentity,
				entry.Identity,
				StringComparison.Ordinal);
		if (!string.Equals(_activeProgramIdentity, entry.Identity, StringComparison.Ordinal))
		{
			_activeProgramIdentity = entry.Identity;
			_pipelineDirty = true;
			_descriptorDirty = true;
			_vertexInputStateDirty = true;
		}

		_generatedProgram = entry.Data;
		_program = entry.Program;
		_program.Generate();
		bool linked = _program.Link(MeshRenderer?.GenerateAsync ?? false);
		if (linked)
			ObserveActiveProgramLinkGeneration(_program, replacingSameInterface);
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

	private GeneratedProgramState CaptureGeneratedProgramState(XRMaterial material)
	{
		XRMesh? mesh = Mesh;
		bool hasSkinning = mesh?.HasSkinning == true;
		bool hasBlendshapes = mesh?.BlendshapeCount > 0;
		bool isVulkan = RuntimeEngine.Rendering.State.IsVulkan;
		bool useComputeSkinning =
			hasSkinning &&
			RuntimeEngine.Rendering.Settings.AllowSkinning &&
			RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader &&
			!isVulkan;
		bool useComputeBlendshapes =
			hasBlendshapes &&
			RuntimeEngine.Rendering.Settings.AllowBlendshapes &&
			!isVulkan &&
			(RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || useComputeSkinning);

		return new GeneratedProgramState(
			material,
			material.ShaderStateRevision,
			ComputeMaterialShaderStateSignature(material),
			material.ActiveUberVariant.VariantHash,
			material.ActiveUberVariant.IsEmpty,
			Data.VertexShaderSource,
			material.Name,
			mesh?.Name,
			MeshRenderer.Name,
			Data.VersionKindLabel,
			Data.ProgramPriority,
			RuntimeEngine.Rendering.Settings.ShaderConfigVersion,
			hasSkinning,
			useComputeSkinning,
			hasBlendshapes,
			useComputeBlendshapes,
			RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass && !isVulkan,
			MeshRenderer.MeshDeformEnabled,
			material.DirectionalCascadeShadowMaterialKind,
			material.PointShadowMaterialKind,
			RuntimeEngine.Rendering.State.RenderingPipelineState?.UseDepthNormalMaterialVariants ?? false,
			RuntimeEngine.Rendering.EffectiveClipDepthRange,
			RuntimeEngine.Rendering.Settings.ClipSpaceYDirection);
	}

	private static ulong ComputeMaterialShaderStateSignature(XRMaterial material)
	{
		const ulong offset = 1469598103934665603UL;
		const ulong prime = 1099511628211UL;
		ulong hash = offset;
		hash = (hash ^ unchecked((ulong)material.Shaders.Count)) * prime;
		for (int i = 0; i < material.Shaders.Count; i++)
		{
			XRShader? shader = material.Shaders[i];
			if (shader is null)
			{
				hash = (hash ^ ulong.MaxValue) * prime;
				continue;
			}

			hash = (hash ^ unchecked((uint)RuntimeHelpers.GetHashCode(shader))) * prime;
			hash = (hash ^ unchecked((ulong)shader.SourceRevision)) * prime;
			hash = (hash ^ unchecked((uint)shader.Type)) * prime;
			hash = (hash ^ shader.GeneratedUberVariantHash) * prime;
			hash = (hash ^ ReferenceIdentity(shader.Source)) * prime;
			hash = (hash ^ ReferenceIdentity(shader.Source?.Text)) * prime;
			hash = (hash ^ ReferenceIdentity(shader.Source?.FilePath)) * prime;
			hash = (hash ^ ReferenceIdentity(shader.FilePath)) * prime;
		}

		return hash;
	}

	private static ulong ReferenceIdentity(object? value)
		=> value is null ? 0UL : unchecked((uint)RuntimeHelpers.GetHashCode(value));

	private static List<XRShader> BuildCombinedShaderList(IReadOnlyList<XRShader> sourceShaders, XRShader vertexShader)
	{
		List<XRShader> shaders = new(sourceShaders.Count + 1);
		for (int i = 0; i < sourceShaders.Count; i++)
		{
			XRShader shader = sourceShaders[i];
			if (shader.Type != EShaderType.Vertex)
				shaders.Add(shader);
		}

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

	private static string BuildGeneratedProgramIdentity(
		GeneratedProgramState state,
		string generatedProgramAxes,
		string shaderStageList,
		string? generatedVertexIdentity)
		=> $"material={RuntimeHelpers.GetHashCode(state.Material):X8};shaderRevision={state.ShaderStateRevision};shaderSignature={state.ShaderSourceSignature:X16};uberVariant={state.MaterialVariantHash:X16};axes={generatedProgramAxes};stages={shaderStageList};generatedVertex={generatedVertexIdentity ?? string.Empty}";

	private static string BuildGeneratedProgramName(
		GeneratedProgramState state,
		string generatedProgramAxes,
		string shaderStageList)
		=> $"VkCombined:{SanitizeProgramName(state.MaterialName, "material")}:{SanitizeProgramName(state.MeshName, "mesh")}:{generatedProgramAxes}:{shaderStageList}";

	private static string BuildGeneratedProgramAxes(GeneratedProgramState state)
		=> $"shaderConfig={state.ShaderConfigVersion};skinning={state.HasSkinning};computeSkinning={state.UseComputeSkinning};blendshapes={state.HasBlendshapes};computeBlendshapes={state.UseComputeBlendshapes};precombineBlendshapes={state.UsePrecombinedBlendshapes};meshDeform={state.MeshDeformEnabled};directionalShadow={state.DirectionalShadowKind};pointShadow={state.PointShadowKind};depthNormal={state.UseDepthNormalVariants};clipDepth={state.ClipDepthRange};clipY={state.ClipYDirection}";

	private static string BuildShaderStageList(IReadOnlyList<XRShader> shaders)
	{
		if (shaders.Count == 0)
			return "no-shaders";

		var builder = new System.Text.StringBuilder(shaders.Count * 24);
		for (int i = 0; i < shaders.Count; i++)
		{
			if (i != 0)
				builder.Append(", ");

			XRShader shader = shaders[i];
			builder.Append(ResolveShaderTypeName(shader.Type))
				.Append(':')
				.Append(ResolveShaderLabel(shader));
		}

		return builder.ToString();
	}

	private static string ResolveShaderLabel(XRShader shader)
	{
		if (!string.IsNullOrWhiteSpace(shader.Source?.FilePath))
			return Path.GetFileName(shader.Source.FilePath!);
		if (!string.IsNullOrWhiteSpace(shader.FilePath))
			return Path.GetFileName(shader.FilePath!);
		if (!string.IsNullOrWhiteSpace(shader.Name))
			return shader.Name!;

		return ResolveShaderTypeName(shader.Type);
	}

	private static string ResolveShaderTypeName(EShaderType type)
		=> type switch
		{
			EShaderType.Fragment => nameof(EShaderType.Fragment),
			EShaderType.Vertex => nameof(EShaderType.Vertex),
			EShaderType.Geometry => nameof(EShaderType.Geometry),
			EShaderType.TessEvaluation => nameof(EShaderType.TessEvaluation),
			EShaderType.TessControl => nameof(EShaderType.TessControl),
			EShaderType.Compute => nameof(EShaderType.Compute),
			EShaderType.Task => nameof(EShaderType.Task),
			EShaderType.Mesh => nameof(EShaderType.Mesh),
			_ => "Unknown",
		};

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
		VulkanStableHash64 hash = new(schemaVersion: 2);
		hash.Add(passIndex);
		hash.Add(passMetadata?.Count ?? 0);
		if (passMetadata is IReadOnlyList<RenderPassMetadata> metadataList)
		{
			for (int metadataIndex = 0; metadataIndex < metadataList.Count; metadataIndex++)
				AddMetadata(ref hash, metadataList[metadataIndex]);
		}
		else if (passMetadata is not null)
		{
			foreach (RenderPassMetadata metadata in passMetadata)
				AddMetadata(ref hash, metadata);
		}

		hash.Add(RuntimeEngine.Rendering.State.RenderingPipelineState?.ShadowPass ?? false);
		hash.Add(RuntimeEngine.Rendering.State.RenderingPipelineState?.UseDepthNormalMaterialVariants ?? false);
		hash.Add(RuntimeEngine.Rendering.State.RenderingPipelineState?.DirectionalCascadeLayeredShadowPass ?? false);
		hash.Add(RuntimeEngine.Rendering.State.RenderingPipelineState?.PointLightLayeredShadowPass ?? false);
		return hash.Value;

		static void AddMetadata(ref VulkanStableHash64 hash, RenderPassMetadata metadata)
		{
			hash.Add(metadata.PassIndex);
			hash.Add(metadata.Name);
			hash.Add((int)metadata.Stage);
			hash.Add(metadata.DescriptorSchemas.Count);
			for (int schemaIndex = 0; schemaIndex < metadata.DescriptorSchemas.Count; schemaIndex++)
				hash.Add(metadata.DescriptorSchemas[schemaIndex]);
		}
	}

	private ulong ComputeFeatureProfileHash()
	{
		VulkanStableHash64 hash = new(schemaVersion: 2);
		hash.Add(_pipelineShaderConfigVersion);
		hash.Add(_pipelineUsesShaderClipDepthRemap);
		hash.Add(_pipelineUsesNativeDepthClipControl);
		hash.Add((int)RuntimeEngine.Rendering.EffectiveClipDepthRange);
		hash.Add((int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection);
		hash.Add(RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl);
		hash.Add(BackendContext.Supports(EVulkanDeviceCapability.IndexTypeUint8));
		return hash.Value;
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
	/// and draw state. Pipelines are cached by <see cref="VulkanGraphicsPipelineKey"/>. If no
	/// cached pipeline matches, a new one is created with the current shader
	/// program, vertex layout, and fixed-function state.
	/// </summary>
	private bool EnsurePipelineCore(
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
		bool allowPipelineCreation,
		bool foregroundRequired,
		out Pipeline pipeline,
		out bool retryable,
		out string failureReason)
	{
		pipeline = default;
		retryable = false;
		failureReason = string.Empty;

		RefreshClipDepthPipelinePolicy();

		if (draw.PreparedProgram is { } preparedProgram)
		{
			if (!ActivateCapturedProgram(material, preparedProgram, draw.PreparedProgramIdentity, draw.PreparedProgramLinkGeneration))
			{
				retryable = true;
				failureReason = "captured graphics program generation changed";
				return false;
			}
		}
		else if (!EnsureProgram(material))
		{
			retryable = true;
			failureReason = "graphics program is not ready";
			return false;
		}

		bool pipelineInvalidated = _pipelineDirty;
		uint colorAttachmentCount = useDynamicRendering
			? dynamicRenderingFormats.ColorAttachmentCount
			: ProgramCreationPort.GetRenderPassColorAttachmentCount(renderPass);
		PendingMeshDraw effectiveDraw = ResolveAttachmentCompatibleDrawState(
			draw,
			passIndex,
			passMetadata,
			depthStencilReadOnly,
			colorAttachmentCount);

		BuildVertexInputState();

		ulong programPipelineHash = _program!.ComputeGraphicsPipelineFingerprint();
		ulong vertexLayoutHash = ComputeVertexLayoutHash();
		ulong descriptorLayoutHash = _program.DescriptorSchemaFingerprint;
		ulong passMetadataHash = ComputePassMetadataHash(passMetadata, passIndex);
		ulong featureProfileHash = ComputeFeatureProfileHash();
		bool useNativeNegativeOneToOneDepth = RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl;

		VulkanGraphicsPipelineKey key = new(
			topology,
			useDynamicRendering,
			useDynamicRendering ? 0UL : renderPass.Handle,
			useDynamicRendering ? dynamicRenderingFormats : default,
			programPipelineHash,
			_program.LinkGeneration,
			vertexLayoutHash,
			descriptorLayoutHash,
			_program.PipelineLayout.Handle,
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

		if (BackendContext.Resources.PipelineManager.TryGetSharedGraphicsPipeline(key, out pipeline))
		{
			RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheLookup(cacheHit: true);
			_pipelines[key] = pipeline;
			_pipelineDirty = false;
			return true;
		}

		// The pre-recording warmup pass is the only path permitted to compile a
		// graphics pipeline. Recording may consume a cached handle, but a cache
		// miss must defer the frame so it cannot inject shader/pipeline work into
		// the primary command-buffer hot path.
		if (!allowPipelineCreation)
		{
			_pipelineDirty = true;
			retryable = true;
			failureReason = "graphics pipeline was not present in the prepared cache";
			return false;
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
				: ProgramCreationPort.GetRenderPassColorAttachmentFormat(renderPass, (uint)i);
			if (!ProgramCreationPort.SupportsColorAttachmentBlend(attachmentFormat))
				attachmentBlend.BlendEnable = Vk.False;

			blendAttachments[i] = attachmentBlend;
		}

		DynamicState[] dynamicStates =
		[
			DynamicState.Viewport,
			DynamicState.Scissor,
		];

		VkRenderProgram program = _program ?? throw new InvalidOperationException("Graphics program was not initialized.");
		VulkanGraphicsPipelineBuildRequest request;
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
		catch (VulkanPipelineCompilationDeferredException ex)
		{
			_pipelineDirty = true;
			retryable = true;
			failureReason = ex.Message;
			return false;
		}
		catch (InvalidOperationException ex)
		{
			ReportPipelineCreateFailure(program, material, pipelineName, passIndex, topology, ex);
			failureReason = ex.Message;
			return false;
		}

		if (BackendContext.Resources.PipelineManager.IsAsyncCompilationEnabled(
				BackendContext.IsLogicalDeviceReady,
				BackendContext.IsDeviceOperational,
				RuntimeEngine.Rendering.Settings.AsyncProgramCompilation))
		{
			if (BackendContext.Resources.PipelineManager.TryTakeCompletedGraphicsPipeline(request.CompileKey, request.DependencyGeneration, out VulkanGraphicsPipelineCompileResult asyncResult))
			{
				if (!asyncResult.Success || asyncResult.Pipeline.Handle == 0)
				{
				retryable = asyncResult.Retryable;
				failureReason = asyncResult.ErrorMessage ??
					"graphics pipeline compilation returned no native pipeline";
					if (asyncResult.Retryable)
					{
						_pipelineDirty = true;
						return false;
					}

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

				pipeline = BackendContext.Resources.PipelineManager.StoreOrRetireSharedGraphicsPipeline(key, asyncResult.Pipeline);
				_pipelines[key] = pipeline;
				_pipelineDirty = false;
				return true;
			}

			if (BackendContext.Resources.PipelineManager.IsGraphicsPipelineCompileInFlight(request.CompileKey))
			{
				string foregroundReason = "foreground pipeline completion failed";
				bool foregroundRetryable = true;
				if (foregroundRequired &&
				BackendContext.Resources.PipelineManager.TryCompleteGraphicsPipelineForForeground(
					request.CompileKey,
					request.DependencyGeneration,
					out VulkanGraphicsPipelineCompileResult foregroundResult,
					out foregroundReason,
					out foregroundRetryable))
				{
					pipeline = BackendContext.Resources.PipelineManager.StoreOrRetireSharedGraphicsPipeline(key, foregroundResult.Pipeline);
					_pipelines[key] = pipeline;
					_pipelineDirty = false;
					return true;
				}

				if (foregroundRequired)
				{
					retryable = foregroundRetryable;
					failureReason = foregroundReason;
					Debug.VulkanWarningEvery(
						$"Vulkan.Pipeline.ForegroundCompileFailed.{program.Data.Name ?? "UnknownProgram"}",
						TimeSpan.FromSeconds(2),
						"[Vulkan] Foreground pipeline admission failed for program='{0}' pipeline='{1}': {2}",
						program.Data.Name ?? "<unnamed program>", pipelineName, foregroundReason);
				}
				else
				{
					retryable = true;
					failureReason = "graphics pipeline compile is queued";
				}

				RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineTelemetry(
					EVulkanPipelineTelemetryEvent.DrawNotReady,
					backgroundCompile: true);
				_pipelineDirty = true;
				return false;
			}

			// A completion continuation publishes successful worker results directly
			// into the shared object cache. Recheck after observing no in-flight job
			// so a just-completed compile is not redundantly enqueued.
			if (BackendContext.Resources.PipelineManager.TryGetSharedGraphicsPipeline(key, out pipeline))
			{
				RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheLookup(cacheHit: true);
				_pipelines[key] = pipeline;
				_pipelineDirty = false;
				return true;
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

			if (!BackendContext.Resources.PipelineManager.TryEnqueueGraphicsPipelineCompile(
					request,
					BackendContext.IsDeviceOperational,
					RuntimeEngine.Rendering.Settings.AsyncProgramCompilation,
					foregroundRequired,
					out string rejectReason,
					out bool enqueueRetryable))
			{
				retryable = enqueueRetryable;
				failureReason = rejectReason;
				RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineTelemetry(
					EVulkanPipelineTelemetryEvent.DrawNotReady,
					backgroundCompile: true);
				Debug.VulkanEvery(
					$"Vulkan.Pipeline.AsyncEnqueueRejected.{program.Data?.Name ?? "UnknownProgram"}",
					TimeSpan.FromSeconds(2),
					"[Vulkan] Async graphics pipeline enqueue skipped for program='{0}' pipeline='{1}': {2}",
					program.Data?.Name ?? "<unnamed program>",
					pipelineName,
					rejectReason);
				_pipelineDirty = true;
				return false;
			}

			if (foregroundRequired)
			{
				if (BackendContext.Resources.PipelineManager.TryCompleteGraphicsPipelineForForeground(
						request.CompileKey,
						request.DependencyGeneration,
						out VulkanGraphicsPipelineCompileResult foregroundCompletion,
						out string foregroundReason,
						out bool foregroundRetryable))
				{
					pipeline = BackendContext.Resources.PipelineManager.StoreOrRetireSharedGraphicsPipeline(key, foregroundCompletion.Pipeline);
					_pipelines[key] = pipeline;
					_pipelineDirty = false;
					return true;
				}

				retryable = foregroundRetryable;
				failureReason = foregroundReason;
			}
			else
			{
				retryable = true;
				failureReason = "graphics pipeline compile was queued";
			}

			_pipelineDirty = true;
			RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineTelemetry(
				EVulkanPipelineTelemetryEvent.DrawNotReady,
				backgroundCompile: true);
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
			pipeline = BackendContext.Resources.PipelineManager.CreateGraphicsPipelineFromRequest(
				request,
				BackendContext.Resources.PipelineManager.ActivePipelineCache,
				backgroundCompile: false);
		}
		catch (VulkanPipelineCompilationDeferredException ex)
		{
			_pipelineDirty = true;
			pipeline = default;
			retryable = true;
			failureReason = ex.Message;
			return false;
		}
		catch (InvalidOperationException ex)
		{
			ReportPipelineCreateFailure(program, material, pipelineName, passIndex, topology, ex);
			pipeline = default;
			failureReason = ex.Message;
			return false;
		}

		pipeline = BackendContext.Resources.PipelineManager.StoreOrRetireSharedGraphicsPipeline(key, pipeline);
		_pipelines[key] = pipeline;
		_pipelineDirty = false;
		if (pipeline.Handle != 0)
			return true;

		failureReason = "graphics pipeline creation returned a null native handle";
		return false;
	}

	private bool EnsurePipeline(
		XRMaterial material, PrimitiveTopology topology, in PendingMeshDraw draw,
		RenderPass renderPass, bool useDynamicRendering,
		DynamicRenderingFormatSignature dynamicRenderingFormats, int passIndex,
		IReadOnlyCollection<RenderPassMetadata>? passMetadata, bool depthStencilReadOnly,
		string pipelineName, bool allowPipelineCreation, out Pipeline pipeline)
		=> EnsurePipelineCore(material, topology, draw, renderPass, useDynamicRendering,
			dynamicRenderingFormats, passIndex, passMetadata, depthStencilReadOnly,
			pipelineName, allowPipelineCreation, false, out pipeline, out _, out _);

	private bool RecordGraphicsPipelineCacheMiss(
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
		in VulkanGraphicsPipelineKey key,
		in PendingMeshDraw effectiveDraw)
	{
		bool knownAtStartup = BackendContext.Resources.PipelineManager.RecordGraphicsPipelineCacheMiss(
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
			descriptorLayoutHash,
			key.PassMetadataHash,
			key.FeatureProfileHash,
			ComputeStableFixedFunctionStateHash(key),
			effectiveDraw.RasterizationSamples,
			effectiveDraw.DepthTestEnabled,
			effectiveDraw.BlendEnabled,
			effectiveDraw.AlphaToCoverageEnabled,
			effectiveDraw.ColorWriteMask);

		uint keyHash = unchecked((uint)key.GetHashCode());
		Debug.VulkanEvery(
			$"Vulkan.Pipeline.CacheMiss.Program.{programPipelineHash:X16}",
			TimeSpan.FromSeconds(5),
			"[Vulkan] Representative pipeline cache miss: key=0x{0:X8} program='{1}' dynRendering={2} renderPass=0x{3:X} colorCount={4} programHash=0x{5:X16} vertexLayout=0x{6:X16} descriptorLayout=0x{7:X16} depthTest={8} depthWrite={9} depthCompare={10} blend={11} atc={12} cull={13}",
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
		return knownAtStartup;
	}

	private static ulong ComputeStableFixedFunctionStateHash(in VulkanGraphicsPipelineKey key)
	{
		VulkanStableHash64 hash = new(schemaVersion: 2);

		Add((ulong)key.RasterizationSamples);
		Add(key.DepthTestEnabled ? 1UL : 0UL);
		Add(key.DepthWriteEnabled ? 1UL : 0UL);
		Add((ulong)key.DepthCompareOp);
		Add(key.StencilTestEnabled ? 1UL : 0UL);
		AddStencil(key.FrontStencilState);
		AddStencil(key.BackStencilState);
		Add(key.StencilWriteMask);
		Add((ulong)key.CullMode);
		Add((ulong)key.FrontFace);
		Add(key.BlendEnabled ? 1UL : 0UL);
		Add(key.AlphaToCoverageEnabled ? 1UL : 0UL);
		Add((ulong)key.ColorBlendOp);
		Add((ulong)key.AlphaBlendOp);
		Add((ulong)key.SrcColorBlendFactor);
		Add((ulong)key.DstColorBlendFactor);
		Add((ulong)key.SrcAlphaBlendFactor);
		Add((ulong)key.DstAlphaBlendFactor);
		Add((ulong)key.ColorWriteMask);
		Add(key.ViewportScissorCount);
		Add(key.NativeNegativeOneToOneDepth ? 1UL : 0UL);
		return hash.Value;

		void Add(ulong value)
			=> hash.Add(value);

		void AddStencil(StencilOpState state)
		{
			Add((ulong)state.FailOp);
			Add((ulong)state.PassOp);
			Add((ulong)state.DepthFailOp);
			Add((ulong)state.CompareOp);
			Add(state.CompareMask);
			Add(state.WriteMask);
			Add(state.Reference);
		}
	}

	private bool ShouldUseGraphicsPipelineLibraries()
		=> RuntimeEngine.Rendering.Settings.AllowShaderPipelines &&
		   Data.AllowShaderPipelines &&
			   BackendContext.Supports(EVulkanDeviceCapability.GraphicsPipelineLibrary);

	private VulkanGraphicsPipelineBuildRequest CreateGraphicsPipelineBuildRequest(
		VkRenderProgram program,
		VulkanGraphicsPipelineKey key,
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
		DynamicRenderingFormatSignature dynamicRenderingFormats,
		in VulkanVisibilityVertexInputSnapshot visibilityVertexInput = default,
		bool isMeshShaderPipeline = false)
	{
		using VulkanPipelineCompilationDependencyLease dependencyLease =
			BackendContext.Resources.PipelineManager.AcquireCompilationDependencyLease();
		long dependencyGeneration = dependencyLease.Generation;
				if (!program.IsLinked ||
					program.PipelineLayout.Handle == 0 ||
					program.ComputeGraphicsPipelineFingerprint() != key.ProgramPipelineHash ||
					program.DescriptorSchemaFingerprint != key.DescriptorLayoutHash)
				{
					throw new VulkanPipelineCompilationDeferredException(
						"The Vulkan program interface changed while the graphics pipeline request was being prepared.");
				}

				PipelineShaderStageCreateInfo[] graphicsStages = VulkanGraphicsPipelineFactory.GetGraphicsPipelineLibraryStages(
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

				for (int stageIndex = 0; stageIndex < graphicsStages.Length; stageIndex++)
				{
					PipelineShaderStageCreateInfo stage = graphicsStages[stageIndex];
					if (stage.Module.Handle == 0 || stage.PName is null)
					{
						throw new VulkanPipelineCompilationDeferredException(
							"The Vulkan program contains a shader stage that is being regenerated.");
					}
				}

				PipelineShaderStageCreateInfo[] preRasterStages = VulkanGraphicsPipelineFactory.GetGraphicsPipelineLibraryStages(
					program,
					EProgramStageMask.VertexShaderBit |
					EProgramStageMask.TessControlShaderBit |
					EProgramStageMask.TessEvaluationShaderBit |
					EProgramStageMask.GeometryShaderBit |
					EProgramStageMask.TaskShaderBit |
					EProgramStageMask.MeshShaderBit,
					colorAttachmentCount);

				PipelineShaderStageCreateInfo[] fragmentStages = VulkanGraphicsPipelineFactory.GetGraphicsPipelineLibraryStages(
					program,
					EProgramStageMask.FragmentShaderBit,
					colorAttachmentCount);

		ReadOnlySpan<VertexInputBindingDescription> vertexBindings = visibilityVertexInput.IsValid
			? visibilityVertexInput.Bindings
			: _vertexBindings;
		ReadOnlySpan<VertexInputAttributeDescription> vertexAttributes = visibilityVertexInput.IsValid
			? visibilityVertexInput.Attributes
			: _vertexAttributes;
		return new VulkanGraphicsPipelineBuildRequest(
					PipelineCompileOwnerId,
					program,
					ProgramCreationPort,
					!isMeshShaderPipeline && ShouldUseGraphicsPipelineLibraries(),
					dependencyGeneration,
					key,
					pipelineName,
					colorAttachmentCount,
					program.PipelineLayout,
					vertexBindings.ToArray(),
					vertexAttributes.ToArray(),
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
					fragmentStages,
					isMeshShaderPipeline);
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

		if (passMetadata is IReadOnlyList<RenderPassMetadata> indexedPasses)
		{
			for (int index = 0; index < indexedPasses.Count; index++)
			{
				RenderPassMetadata pass = indexedPasses[index];
				if (pass.PassIndex == passIndex)
					return PassHasReadOnlyDepthStencilUsage(pass);
			}

			return false;
		}

		foreach (RenderPassMetadata pass in passMetadata)
			if (pass.PassIndex == passIndex)
				return PassHasReadOnlyDepthStencilUsage(pass);

		return false;
	}

	private static bool PassHasReadOnlyDepthStencilUsage(RenderPassMetadata pass)
	{
		bool hasDepthStencilUsage = false;
		bool hasDepthStencilWriteUsage = false;
		for (int index = 0; index < pass.ResourceUsages.Count; index++)
		{
			RenderPassResourceUsage usage = pass.ResourceUsages[index];
			if (usage.ResourceType is not (ERenderPassResourceType.DepthAttachment or ERenderPassResourceType.StencilAttachment))
				continue;

			hasDepthStencilUsage = true;
			if (usage.Access is ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite)
				hasDepthStencilWriteUsage = true;
		}

		return hasDepthStencilUsage && !hasDepthStencilWriteUsage;
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
		_programStateCache.Clear();
		_observedProgramLinkGenerations.Clear();
		_program = null;
		_generatedProgram = null;
		_activeProgramIdentity = null;
		_activeProgramLinkGeneration = 0UL;
	}

	#endregion // Pipeline Management
}
