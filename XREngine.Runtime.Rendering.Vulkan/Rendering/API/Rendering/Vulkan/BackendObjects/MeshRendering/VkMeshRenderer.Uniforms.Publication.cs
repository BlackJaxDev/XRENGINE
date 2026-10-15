using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	/// <summary>
	/// Builds a static byte template and typed operations for values that can
	/// change during a draw. Schema and identity checks reject stale layouts.
	/// A cached plan avoids repeated reflected member lookups.
	/// </summary>
	private bool TryGetAutoUniformMaterialWritePlan(
		AutoUniformBlockInfo block,
		uint bufferSize,
		XRMaterial material,
		ComputeDispatchSnapshot? bindingSnapshot,
		out AutoUniformMaterialWritePlan? plan,
		out EVulkanAutoUniformFallbackReason fallbackReason)
	{
		plan = null;
		if (_program is null)
		{
			fallbackReason = EVulkanAutoUniformFallbackReason.ProgramUnavailable;
			return false;
		}

		if (bufferSize == 0 || bufferSize > int.MaxValue)
		{
			fallbackReason = EVulkanAutoUniformFallbackReason.InvalidBufferSize;
			return false;
		}

		if (_program.BindingSchema is not { } programSchema ||
			!programSchema.TryGetAutoUniformBlock(
				block.InstanceName,
				out VulkanAutoUniformBindingSchema schema))
		{
			fallbackReason = EVulkanAutoUniformFallbackReason.BindingSchemaUnavailable;
			return false;
		}

		if (!schema.IsFastPathEligible)
		{
			fallbackReason = schema.FallbackKind;
			WarnAutoUniformFallback(
				block,
				fallbackReason,
				schema.FallbackReason);
			return false;
		}

		if (!ReferenceEquals(schema.Block, block) ||
			schema.Block.Size != bufferSize)
		{
			RecordAutoUniformSchemaMismatch(
				EVulkanAutoUniformSchemaMismatchSite.BlockIdentityOrSize,
				block,
				bufferSize,
				schema,
				entryName: null,
				block.Frequency);
			fallbackReason = EVulkanAutoUniformFallbackReason.BindingSchemaMismatch;
			return false;
		}

		fallbackReason = EVulkanAutoUniformFallbackReason.None;
		string blockName = block.InstanceName;
		ulong linkGeneration = _program.LinkGeneration;
		bool materialOwned =
			block.Frequency is EVulkanBindingFrequency.Unknown or
				EVulkanBindingFrequency.Material;
		bool cacheDependsOnMaterialOrRuntime =
			schema.HasMaterialOrRuntimeSources;
		ulong runtimeUniformNameSignature =
			cacheDependsOnMaterialOrRuntime
				? bindingSnapshot?.RuntimeUniformNameSignature ?? 0UL
				: 0UL;
		ulong runtimeUniformPublicationLayoutSignature =
			cacheDependsOnMaterialOrRuntime
				? bindingSnapshot
					?.RuntimeUniformPublicationLayoutSignature ?? 0UL
				: 0UL;
		ulong publicationLayoutSignature =
			schema.PublicationLayoutSignature;
		AutoUniformMaterialWritePlanCacheKey materialPlanKey = new(
			publicationLayoutSignature,
			material,
			runtimeUniformNameSignature,
			runtimeUniformPublicationLayoutSignature,
			bindingSnapshot?.MaterialUniformBindings);
		VkMaterial? materialPlanOwner = cacheDependsOnMaterialOrRuntime
			? WrapperLookup.GetOrCreate(
				material,
				generateNow: true) as VkMaterial
			: null;
		bool planCacheHit = materialPlanOwner is not null
			? materialPlanOwner.TryGetAutoUniformMaterialWritePlan(
				materialPlanKey,
				out AutoUniformMaterialWritePlan? cached)
			: _program.TryGetAutoUniformMaterialWritePlan(
				blockName,
				publicationLayoutSignature,
				material,
				runtimeUniformNameSignature,
				runtimeUniformPublicationLayoutSignature,
				cacheDependsOnMaterialOrRuntime,
				out cached);
		if (planCacheHit &&
			cached is not null &&
			ReferenceEquals(cached.MaterialPayload,
				bindingSnapshot?.MaterialUniformBindings) &&
			cached.PublicationLayoutSignature ==
				publicationLayoutSignature &&
			(!materialOwned ||
			 (cached.MaterialLayoutVersion ==
					material.BindingLayoutVersion &&
			  cached.MaterialValueVersion ==
					material.BindingValueVersion)) &&
			cached.RuntimeUniformNameSignature ==
				runtimeUniformNameSignature &&
			cached.RuntimeUniformPublicationLayoutSignature ==
				runtimeUniformPublicationLayoutSignature &&
			cached.StaticBytes.Length == (int)bufferSize)
		{
			RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformPlanLookup(hit: true);
			plan = cached;
			return true;
		}
		RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformPlanLookup(hit: false);
		if (XREnvironment.IsEnabled(
				XREngineEnvironmentVariables.VulkanFrameDataReuseDiag))
		{
			Debug.VulkanEvery(
				$"Vulkan.AutoUniformPlanCacheMiss.{material.ID}.{blockName}",
				TimeSpan.FromSeconds(1),
				"[Vulkan.AutoUniformPlanCacheMiss] material='{0}' id={1} " +
				"block='{2}' program={3}/{4} layout={5} value={6} " +
				"runtimeNames=0x{7:X16} runtimeLayout=0x{8:X16}.",
				material.Name ?? "<unnamed>",
				material.ID,
				blockName,
				_program.BindingId,
				linkGeneration,
				material.BindingLayoutVersion,
				material.BindingValueVersion,
				runtimeUniformNameSignature,
				runtimeUniformPublicationLayoutSignature);
		}

		byte[] staticBytes = new byte[(int)bufferSize];
		List<VulkanAutoUniformBindingOperation> dynamicOperations = [];
		VulkanAutoUniformBindingOperation[] operations = schema.Operations;
		for (int operationIndex = 0; operationIndex < operations.Length; operationIndex++)
		{
			VulkanAutoUniformBindingOperation operation = operations[operationIndex];
			bool hasRuntimeOverride =
				operation.SourceKind ==
					EVulkanAutoUniformSourceKind.MaterialOrRuntime &&
				bindingSnapshot?.HasRuntimeUniform(
					operation.Member.Name) == true;
			if (operation.SourceKind ==
					EVulkanAutoUniformSourceKind.MaterialOrRuntime &&
				!hasRuntimeOverride)
			{
				AutoUniformMember member = operation.Member;
				bool capturedEffect = IsPhysicsVertexEffectControl(member.Name) &&
					bindingSnapshot is not null;
				ShaderVar? materialParameter = capturedEffect
					? null : material.Parameter<ShaderVar>(member.Name);
				bool hasCapturedEffectValue = capturedEffect &&
					_program.TryGetUniformValue(bindingSnapshot, member.Name, out _);
				bool hasDeclaredDefault =
					VulkanAutoUniformBindingSchema.HasExplicitDefault(
						member);
				if (materialParameter is null && !hasCapturedEffectValue && !hasDeclaredDefault)
				{
					// Loose GLSL uniforms default to zero. Preserve that
					// contract after rewriting them into a UBO instead of
					// treating an omitted optional material parameter as a
					// per-draw runtime dependency.
					continue;
				}

				if (TryWriteStaticMaterialAutoUniform(
						staticBytes,
						member,
						material,
						bindingSnapshot))
				{
					continue;
				}

				fallbackReason =
					EVulkanAutoUniformFallbackReason
						.TypedMaterialOrRuntimeWriteFailed;
				return false;
			}

			if (hasRuntimeOverride)
			{
				EVulkanBindingFrequency runtimeFrequency =
					ResolveRuntimeOverrideFrequency(
						block.Frequency,
						bindingSnapshot!,
						operation.Member.Name);
				if (block.Frequency != EVulkanBindingFrequency.Unknown &&
					runtimeFrequency != block.Frequency)
				{
					RecordAutoUniformSchemaMismatch(
						EVulkanAutoUniformSchemaMismatchSite.Frequency,
						block,
						bufferSize,
						schema,
						operation.Member.Name,
						runtimeFrequency);
					fallbackReason =
						EVulkanAutoUniformFallbackReason.BindingSchemaMismatch;
					return false;
				}
				dynamicOperations.Add(operation with
				{
					Frequency = runtimeFrequency,
				});
			}
			else
			{
				dynamicOperations.Add(operation);
			}
		}

		plan = new AutoUniformMaterialWritePlan(
			schema,
			material.BindingLayoutVersion,
			material.BindingValueVersion,
			runtimeUniformNameSignature,
			runtimeUniformPublicationLayoutSignature,
			staticBytes,
			[.. dynamicOperations])
		{
			MaterialPayload = bindingSnapshot?.MaterialUniformBindings,
		};
		if (materialPlanOwner is not null)
		{
			materialPlanOwner.CacheAutoUniformMaterialWritePlan(
				materialPlanKey,
				plan);
		}
		else
		{
			_program.CacheAutoUniformMaterialWritePlan(
				blockName,
				publicationLayoutSignature,
				material,
				runtimeUniformNameSignature,
				runtimeUniformPublicationLayoutSignature,
				cacheDependsOnMaterialOrRuntime,
				plan);
		}
		return true;
	}

	internal static bool IsMaterialBindingSnapshotEligible(
		bool materialOwned,
		bool shadowPass,
		ComputeDispatchSnapshot? snapshot)
		=> !materialOwned ||
		   (!shadowPass &&
			(snapshot is null ||
			 snapshot.AllowsMaterialBindingFastPath));

	internal static EVulkanBindingFrequency ResolveRuntimeOverrideFrequency(
		EVulkanBindingFrequency blockFrequency,
		ComputeDispatchSnapshot snapshot,
		string uniformName)
	{
		if (snapshot.TryGetRuntimeUniformPublication(
				uniformName,
				out VulkanRuntimeUniformPublication publication))
		{
			return publication.Frequency;
		}

		return snapshot.IsMutableLegacyUniform(uniformName) &&
			   blockFrequency != EVulkanBindingFrequency.Unknown
			? blockFrequency
			: EVulkanBindingFrequency.RuntimeCallback;
	}

	private int ResolveFrequencyOwnedAutoUniformBufferIndex(
		AutoUniformBlockInfo block,
		int frameIndex,
		int drawUniformSlot,
		XRMaterial material,
		in PendingMeshDraw draw,
		int bufferCount,
		out ulong ownerIdentity)
	{
		ownerIdentity = 0;
		if (block.Frequency == EVulkanBindingFrequency.Unknown)
		{
			return ResolveUniformBufferIndex(
				frameIndex,
				drawUniformSlot,
				bufferCount);
		}

		VulkanAutoUniformOwnerSlotTable table =
			GetOrCreateAutoUniformOwnerSlotTable(block.InstanceName);
		ownerIdentity = ComputeAutoUniformOwnerIdentity(
			block.Frequency,
			material,
			draw,
			drawUniformSlot);
		int ownerSlot = table.ResolveAndPublish(
			frameIndex,
			drawUniformSlot,
			ownerIdentity);
		return ResolveUniformBufferIndex(
			frameIndex,
			ownerSlot,
			bufferCount);
	}

	private int ResolvePublishedAutoUniformBufferIndex(
		AutoUniformBlockInfo block,
		int frameIndex,
		int drawUniformSlot,
		int bufferCount)
	{
		if (block.Frequency == EVulkanBindingFrequency.Unknown ||
			!_autoUniformOwnerSlotTables.TryGetValue(
				block.InstanceName,
				out VulkanAutoUniformOwnerSlotTable? table) ||
			table.FrameCount != UniformBufferFrameCount ||
			table.DrawSlotCapacity != UniformBufferSlotCount)
		{
			return ResolveUniformBufferIndex(
				frameIndex,
				drawUniformSlot,
				bufferCount);
		}

		int ownerSlot = table.ResolvePublished(
			frameIndex,
			drawUniformSlot);
		return ResolveUniformBufferIndex(
			frameIndex,
			ownerSlot,
			bufferCount);
	}

	/// <summary>
	/// Keeps one stable draw slot for each owner in the current frame layout.
	/// </summary>
	private VulkanAutoUniformOwnerSlotTable GetOrCreateAutoUniformOwnerSlotTable(
		string blockName)
	{
		if (_autoUniformOwnerSlotTables.TryGetValue(
				blockName,
				out VulkanAutoUniformOwnerSlotTable? table) &&
			table.FrameCount == UniformBufferFrameCount &&
			table.DrawSlotCapacity == UniformBufferSlotCount)
		{
			return table;
		}

		table = new VulkanAutoUniformOwnerSlotTable(
			UniformBufferFrameCount,
			UniformBufferSlotCount);
		_autoUniformOwnerSlotTables[blockName] = table;
		return table;
	}

	private ulong ComputeAutoUniformOwnerIdentity(
		EVulkanBindingFrequency frequency,
		XRMaterial material,
		in PendingMeshDraw draw,
		int drawUniformSlot)
	{
		FrameOpSignatureHasher hash = new();
		hash.Add((byte)frequency);
		switch (frequency)
		{
			case EVulkanBindingFrequency.Frame:
				hash.Add(1u);
				break;
			case EVulkanBindingFrequency.View:
				hash.Add(
					draw.Camera is null
						? 0
						: RuntimeHelpers.GetHashCode(draw.Camera));
				hash.Add(
					draw.StereoRightEyeCamera is null
						? 0
						: RuntimeHelpers.GetHashCode(
							draw.StereoRightEyeCamera));
				hash.Add(draw.IsStereoPass);
				hash.Add(draw.UseUnjitteredProjection);
				break;
			case EVulkanBindingFrequency.Pass:
				hash.Add(draw.RenderAreaWidth);
				hash.Add(draw.RenderAreaHeight);
				if (draw.RenderAreaWidth <= 0 ||
					draw.RenderAreaHeight <= 0)
				{
					hash.Add(draw.Viewport.Width);
					hash.Add(MathF.Abs(draw.Viewport.Height));
				}
				AddShadowPassOwnerIdentity(ref hash, draw);
				break;
			case EVulkanBindingFrequency.Material:
				hash.Add(RuntimeHelpers.GetHashCode(material));
				hash.Add(
					draw.ProgramBindingSnapshot
						?.RuntimeUniformNameSignature ?? 0UL);
				hash.Add(
					draw.ProgramBindingSnapshot
						?.RuntimeUniformPublicationLayoutSignature ?? 0UL);
				break;
			case EVulkanBindingFrequency.Object:
				hash.Add(RuntimeHelpers.GetHashCode(MeshRenderer));
				hash.Add(drawUniformSlot);
				break;
			case EVulkanBindingFrequency.Instance:
				hash.Add(RuntimeHelpers.GetHashCode(MeshRenderer));
				hash.Add(drawUniformSlot);
				break;
			case EVulkanBindingFrequency.RuntimeCallback:
				// The manifest separates draw slots across view and pass families.
				// Snapshot values determine content generation, not persistent storage ownership.
				hash.Add(RuntimeHelpers.GetHashCode(this));
				hash.Add(RuntimeHelpers.GetHashCode(material));
				hash.Add(drawUniformSlot);
				break;
			default:
				return 0;
		}

		return hash.ToHash();
	}

	/// <summary>
	/// Adds only the stable identity of a shadow pass to its frequency-owned
	/// uniform reservation. Cascade and cubemap matrices are mutable pass
	/// content and already participate in <see cref="VulkanAutoUniformPublicationSnapshot.PassGeneration"/>.
	/// Including those values here moved the arena reservation whenever a shadow
	/// camera moved, while cached secondary command buffers retained the previous
	/// dynamic offset.
	/// </summary>
	private static void AddShadowPassOwnerIdentity(
		ref FrameOpSignatureHasher hash,
		in PendingMeshDraw draw)
	{
		LayeredShadowUniformState shadowState = draw.ShadowUniformState;
		hash.Add(shadowState.IsShadowPass);
		if (!shadowState.IsShadowPass)
			return;

		hash.Add(
			draw.Camera is null
				? 0
				: RuntimeHelpers.GetHashCode(draw.Camera));
		hash.Add(shadowState.DirectionalCascadeInstancedLayeredShadowPass);
		hash.Add(shadowState.DirectionalCascadeShadowLayerCount);
		hash.Add(shadowState.PointLightInstancedLayeredShadowPass);
		hash.Add(shadowState.PointLightShadowFaceCount);
		for (int index = 0;
			 index < shadowState.PointLightShadowFaceCount;
			 index++)
		{
			shadowState.TryGetPointLightShadowFaceIndex(
				index,
				out int faceIndex);
			hash.Add(faceIndex);
		}
	}

	internal bool TryGetReusableAutoUniformOwner(
		EVulkanBindingFrequency frequency,
		XRMaterial material,
		in PendingMeshDraw draw,
		int drawUniformSlot,
		out ulong ownerIdentity,
		out ulong publicationLayoutSignature,
		out ulong contentGeneration)
	{
		VkRenderProgram? publicationProgram =
			draw.PreparedProgram ?? _program;
		publicationLayoutSignature =
			publicationProgram?.BindingSchema
				?.GetFrequencyPublicationLayoutSignature(frequency) ?? 0UL;
		ownerIdentity = ComputeAutoUniformOwnerIdentity(
			frequency,
			material,
			draw,
			drawUniformSlot);
		if (ownerIdentity == 0 || publicationLayoutSignature == 0)
		{
			contentGeneration = 0;
			return false;
		}

		bool dependsOnMaterialOrRuntime =
			publicationProgram?.BindingSchema
				?.FrequencyDependsOnMaterialOrRuntime(frequency) == true;
		ulong materialGeneration =
			dependsOnMaterialOrRuntime
				? ComputeMaterialPublicationGeneration(
					material,
					material.BindingLayoutVersion,
					material.BindingValueVersion,
					draw.ProgramBindingSnapshot
						?.RuntimeUniformNameSignature ?? 0UL,
					draw.ProgramBindingSnapshot
						?.RuntimeUniformPublicationLayoutSignature ?? 0UL,
					draw.ProgramBindingSnapshot
						?.MutableLegacyUniformValueSignature ?? 0UL)
				: 0UL;
		contentGeneration =
			draw.AutoUniformPublication.GetGeneration(
				frequency,
				materialGeneration);
		if (dependsOnMaterialOrRuntime &&
			frequency != EVulkanBindingFrequency.Material)
		{
			FrameOpSignatureHasher dependencyHash = new();
			dependencyHash.Add(contentGeneration);
			dependencyHash.Add(materialGeneration);
			contentGeneration = dependencyHash.ToHash();
		}
		return true;
	}

	private ulong ComputeAutoUniformFrequencyGeneration(
		EVulkanBindingFrequency frequency,
		AutoUniformMaterialWritePlan plan,
		in PendingMeshDraw draw,
		int drawUniformSlot)
	{
		XRMaterial? material =
			draw.MaterialOverride ?? MeshRenderer.Material;
		ulong materialGeneration = 0;
		if (material is not null &&
			(frequency == EVulkanBindingFrequency.Material ||
			 plan.Schema.HasMaterialOrRuntimeSources))
		{
			materialGeneration = ComputeMaterialPublicationGeneration(
				material,
				plan.MaterialLayoutVersion,
				plan.MaterialValueVersion,
				plan.RuntimeUniformNameSignature,
				plan.RuntimeUniformPublicationLayoutSignature,
				draw.ProgramBindingSnapshot
					?.MutableLegacyUniformValueSignature ?? 0UL);
		}

		ulong contentGeneration =
			draw.AutoUniformPublication.GetGeneration(
			frequency,
			materialGeneration);
		if (plan.Schema.HasMaterialOrRuntimeSources &&
			frequency != EVulkanBindingFrequency.Material)
		{
			FrameOpSignatureHasher dependencyHash = new();
			dependencyHash.Add(contentGeneration);
			dependencyHash.Add(materialGeneration);
			contentGeneration = dependencyHash.ToHash();
		}
		if (material is null)
			return contentGeneration;

		ulong ownerIdentity = ComputeAutoUniformOwnerIdentity(
			frequency,
			material,
			draw,
			drawUniformSlot);
		if (ownerIdentity == 0)
			return contentGeneration;

		FrameOpSignatureHasher hash = new();
		hash.Add(ownerIdentity);
		hash.Add(contentGeneration);
		return hash.ToHash();
	}

	internal static ulong ComputeMaterialPublicationGeneration(
		XRMaterial material,
		ulong materialLayoutVersion,
		ulong materialValueVersion,
		ulong runtimeUniformNameSignature,
		ulong runtimeUniformPublicationLayoutSignature,
		ulong mutableLegacyUniformValueSignature)
	{
		FrameOpSignatureHasher materialHash = new();
		materialHash.Add(RuntimeHelpers.GetHashCode(material));
		materialHash.Add(materialLayoutVersion);
		materialHash.Add(materialValueVersion);
		materialHash.Add(runtimeUniformNameSignature);
		materialHash.Add(runtimeUniformPublicationLayoutSignature);
		materialHash.Add(mutableLegacyUniformValueSignature);
		return materialHash.ToHash();
	}

	private bool TryWriteStaticMaterialAutoUniform(
		Span<byte> data,
		AutoUniformMember member,
		XRMaterial material,
		ComputeDispatchSnapshot? snapshot)
	{
		if (snapshot is not null && IsPhysicsVertexEffectControl(member.Name))
		{
			if (_program is not null && _program.TryGetUniformValue(
				snapshot, member.Name, out ProgramUniformValue captured))
				return TryWriteProgramUniformValue(data, member, captured);
			return member.DefaultValue is { } declared &&
				TryWriteAutoUniformValue(data, member, declared.Value, declared.Type);
		}
		ShaderVar? parameter = material.Parameter<ShaderVar>(member.Name);
		if (parameter is not null)
		{
			return member.IsArray
				? TryWriteAutoUniformArray(data, member, parameter)
				: TryWriteMaterialUniformValue(data, member, parameter);
		}

		if (member.IsArray && member.DefaultArrayValues is { Count: > 0 })
			return TryWriteAutoUniformArrayDefaults(data, member);

		return member.DefaultValue is { } defaultValue &&
			TryWriteAutoUniformValue(data, member, defaultValue.Value, defaultValue.Type);
	}

	private static bool IsPhysicsVertexEffectControl(string name)
		=> name.StartsWith("_Vertex", StringComparison.Ordinal) ||
			name is "_OutlineWidth" or "_OutlineExpansionMode" or
				"_OutlineDropShadowOffset";

	private bool TryWriteAutoUniformOperation(
		Span<byte> data,
		in VulkanAutoUniformBindingOperation operation,
		XRMaterial material,
		in PendingMeshDraw draw,
		out EVulkanAutoUniformFallbackReason fallbackReason)
	{
		RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformTypedOperation();
		AutoUniformMember member = operation.Member;
		switch (operation.SourceKind)
		{
			case EVulkanAutoUniformSourceKind.Engine:
				if (!TryResolveEngineUniformValue(
						operation.EngineUniform,
						draw,
						out EngineUniformValue engineValue,
						out EShaderVarType engineType))
				{
					fallbackReason =
						EVulkanAutoUniformFallbackReason.TypedEngineSourceUnavailable;
					return false;
				}

				bool wroteEngineValue = TryWriteAutoUniformValue(
					data,
					member,
					in engineValue,
					engineType);
				fallbackReason = wroteEngineValue
					? EVulkanAutoUniformFallbackReason.None
					: EVulkanAutoUniformFallbackReason.TypedEngineWriteFailed;
				return wroteEngineValue;

			case EVulkanAutoUniformSourceKind.TemporalViewProjection:
				bool wroteTemporalValue = TryWriteTemporalViewProjectionUniform(
					data,
					member,
					operation.TemporalSource,
					draw);
				fallbackReason = wroteTemporalValue
					? EVulkanAutoUniformFallbackReason.None
					: EVulkanAutoUniformFallbackReason.TypedTemporalWriteFailed;
				return wroteTemporalValue;

			case EVulkanAutoUniformSourceKind.MeshState:
				if (!TryResolveMeshStateUniformValue(
						operation.SpecialSource,
						draw,
						out EngineUniformValue meshValue,
						out EShaderVarType meshType))
				{
					fallbackReason =
						EVulkanAutoUniformFallbackReason.TypedMeshStateSourceUnavailable;
					return false;
				}

				bool wroteMeshValue = TryWriteAutoUniformValue(
					data,
					member,
					in meshValue,
					meshType);
				fallbackReason = wroteMeshValue
					? EVulkanAutoUniformFallbackReason.None
					: EVulkanAutoUniformFallbackReason.TypedMeshStateWriteFailed;
				return wroteMeshValue;

			case EVulkanAutoUniformSourceKind.MaterialOrRuntime:
				bool wroteMaterialOrRuntimeValue = TryWriteMaterialOrRuntimeAutoUniform(
					data,
					member,
					material,
					draw.ProgramBindingSnapshot);
				fallbackReason = wroteMaterialOrRuntimeValue
					? EVulkanAutoUniformFallbackReason.None
					: EVulkanAutoUniformFallbackReason.TypedMaterialOrRuntimeWriteFailed;
				return wroteMaterialOrRuntimeValue;

			case EVulkanAutoUniformSourceKind.StructSnapshot:
				bool wroteStructSnapshot =
					TryWriteStructUniformValue(
						data,
						member,
						member.Name,
						member.Offset,
						draw.ProgramBindingSnapshot);
				fallbackReason = wroteStructSnapshot
					? EVulkanAutoUniformFallbackReason.None
					: EVulkanAutoUniformFallbackReason.StructSnapshotRequired;
				return wroteStructSnapshot;

			default:
				fallbackReason = operation.FallbackKind;
				return false;
		}
	}

	private static bool IncludesBindingFrequency(
		EVulkanBindingFrequencyMask mask,
		EVulkanBindingFrequency frequency)
	{
		if (mask == EVulkanBindingFrequencyMask.All)
			return true;
		if (frequency is <= EVulkanBindingFrequency.Unknown or
			>= EVulkanBindingFrequency.Count)
			return false;

		EVulkanBindingFrequencyMask frequencyBit =
			(EVulkanBindingFrequencyMask)(
				1 << ((int)frequency - 1));
		return (mask & frequencyBit) != 0;
	}

	private bool TryWriteMaterialOrRuntimeAutoUniform(
		Span<byte> data,
		AutoUniformMember member,
		XRMaterial material,
		ComputeDispatchSnapshot? snapshot)
	{
		if (_program is not null &&
			_program.TryGetUniformValue(snapshot, member.Name, out ProgramUniformValue programValue))
		{
			return member.IsArray
				? TryWriteProgramUniformArray(data, member, member.Name, snapshot)
				: TryWriteProgramUniformValue(data, member, programValue);
		}

		if (member.IsArray &&
			TryWriteIndexedProgramUniformArray(data, member, member.Name, snapshot))
		{
			return true;
		}
		if (snapshot is not null && IsPhysicsVertexEffectControl(member.Name))
			return member.DefaultValue is not { } declared ||
				TryWriteAutoUniformValue(data, member, declared.Value, declared.Type);

		ShaderVar? parameter = material.Parameter<ShaderVar>(member.Name);
		if (parameter is not null)
		{
			return member.IsArray
				? TryWriteAutoUniformArray(data, member, parameter)
				: TryWriteMaterialUniformValue(data, member, parameter);
		}

		if (member.IsArray && member.DefaultArrayValues is { Count: > 0 })
			return TryWriteAutoUniformArrayDefaults(data, member);

		return member.DefaultValue is { } defaultValue &&
			TryWriteAutoUniformValue(data, member, defaultValue.Value, defaultValue.Type);
	}
}
