using System;
using System.Numerics;
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
	/// Resolves and uploads a named engine uniform to a host-visible UBO.
	/// This is the legacy per-binding path; auto uniform blocks use the
	/// TryWriteAutoUniformBlock path instead.
	/// </summary>
	private bool TryWriteEngineUniform(string name, in PendingMeshDraw draw, EngineUniformBuffer buffer)
	{
		string normalized = NormalizeEngineUniformName(name);
		if (normalized.Equals(FallbackDescriptorUniformName, StringComparison.Ordinal))
			return ClearEngineUniformBuffer(buffer);

		XRCamera? camera = draw.Camera;
		bool stereoPass = draw.IsStereoPass;

		// Camera matrices/vectors come from the draw snapshot captured at enqueue time.
		// Reading live camera state here can be stale because the pipeline camera stack
		// has already been popped.
		Matrix4x4 viewMatrix = draw.ViewMatrix;
		Matrix4x4 inverseViewMatrix = draw.InverseViewMatrix;
		Matrix4x4 projMatrix = draw.ProjectionMatrix;
		Matrix4x4 inverseProjMatrix = draw.InverseProjectionMatrix;
		Matrix4x4 rightEyeViewMatrix = draw.RightEyeViewMatrix;
		Matrix4x4 rightEyeInverseProjMatrix = draw.RightEyeInverseProjectionMatrix;
		Matrix4x4 rightEyeProjMatrix = draw.RightEyeProjectionMatrix;

		switch (normalized)
		{
			case "u_SourceFlatDirection":
				return UploadUniform(buffer, draw.SourceFlatDirection);
			case nameof(EEngineUniform.UpdateDelta):
				return UploadUniform(buffer, CommandOperations.MaterialUniformUpdateDelta);
			case nameof(EEngineUniform.RenderTime):
				return UploadUniform(buffer, CommandOperations.MaterialUniformSeconds);
			case nameof(EEngineUniform.EngineTime):
				return UploadUniform(buffer, CommandOperations.MaterialUniformSeconds);
			case nameof(EEngineUniform.DeltaTime):
				return UploadUniform(buffer, CommandOperations.MaterialUniformRenderDelta);
			case TransformIdUniformName:
				return UploadUniform(buffer, draw.TransformId);
			case SkinPaletteBaseUniformName:
				return UploadUniform(buffer, MeshRenderer.ActiveSkinPaletteBase);
			case SkinPaletteCountUniformName:
				return UploadUniform(buffer, MeshRenderer.ActiveSkinPaletteCount);
			case SkinningInfluenceCapUniformName:
				return UploadUniform(buffer, MeshRenderer.ActiveSkinningInfluenceCap);
			case BlendshapeActiveCountUniformName:
				return UploadUniform(buffer, MeshRenderer.ActiveBlendshapeCount);
			case BlendshapeWeightThresholdUniformName:
				return UploadUniform(buffer, MeshRenderer.BlendshapeActiveWeightThreshold);
			case UsePrecombinedBlendshapeDeltasUniformName:
				return UploadUniform(
					buffer,
					RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass
					&& !RuntimeEngine.Rendering.State.IsVulkan
					&& MeshRenderer.HasValidPrecombinedBlendshapeDeltas
						? 1
						: 0);
			case nameof(EEngineUniform.ModelMatrix):
				return UploadUniform(buffer, draw.ModelMatrix);
			case nameof(EEngineUniform.PrevModelMatrix):
				return UploadUniform(buffer, draw.PreviousModelMatrix);
			case nameof(EEngineUniform.RootInvModelMatrix):
				Matrix4x4.Invert(draw.ModelMatrix, out Matrix4x4 inverseModel);
				return UploadUniform(buffer, inverseModel);
			case nameof(EEngineUniform.ViewMatrix):
			case nameof(EEngineUniform.LeftEyeViewMatrix):
				return UploadUniform(buffer, viewMatrix);
			case nameof(EEngineUniform.PrevViewMatrix):
			case nameof(EEngineUniform.PrevLeftEyeViewMatrix):
				return UploadUniform(buffer, draw.PreviousViewMatrix);
			case nameof(EEngineUniform.RightEyeViewMatrix):
				return UploadUniform(buffer, rightEyeViewMatrix);
			case nameof(EEngineUniform.PrevRightEyeViewMatrix):
				return UploadUniform(buffer, draw.PreviousRightEyeViewMatrix);
			case nameof(EEngineUniform.InverseViewMatrix):
				return UploadUniform(buffer, inverseViewMatrix);
			case nameof(EEngineUniform.InverseProjMatrix):
				return UploadUniform(buffer, inverseProjMatrix);
			case nameof(EEngineUniform.ViewProjectionMatrix):
				return UploadUniform(buffer, draw.ViewProjectionMatrix);
			case nameof(EEngineUniform.ProjMatrix):
				return UploadUniform(buffer, projMatrix);
			case nameof(EEngineUniform.PrevProjMatrix):
			case nameof(EEngineUniform.PrevLeftEyeProjMatrix):
				return UploadUniform(buffer, draw.PreviousProjectionMatrix);
			case nameof(EEngineUniform.PrevRightEyeProjMatrix):
				return UploadUniform(buffer, draw.PreviousRightEyeProjectionMatrix);
			case nameof(EEngineUniform.LeftEyeViewProjectionMatrix):
				return UploadUniform(buffer, draw.ViewProjectionMatrix);
			case nameof(EEngineUniform.LeftEyeInverseViewMatrix):
				return UploadUniform(buffer, inverseViewMatrix);
			case nameof(EEngineUniform.LeftEyeInverseProjMatrix):
				return UploadUniform(buffer, inverseProjMatrix);
			case nameof(EEngineUniform.RightEyeInverseViewMatrix):
				return UploadUniform(buffer, draw.RightEyeInverseViewMatrix);
			case nameof(EEngineUniform.RightEyeInverseProjMatrix):
				return UploadUniform(buffer, rightEyeInverseProjMatrix);
			case nameof(EEngineUniform.RightEyeViewProjectionMatrix):
				return UploadUniform(buffer, draw.RightEyeViewProjectionMatrix);
			case nameof(EEngineUniform.LeftEyeProjMatrix):
				return UploadUniform(buffer, projMatrix);
			case nameof(EEngineUniform.RightEyeProjMatrix):
				return UploadUniform(buffer, rightEyeProjMatrix);
			case nameof(EEngineUniform.CameraPosition):
				return UploadUniform(buffer, ToVector4(draw.CameraPosition));
			case nameof(EEngineUniform.CameraForward):
				return UploadUniform(buffer, ToVector4(draw.CameraForward));
			case nameof(EEngineUniform.CameraUp):
				return UploadUniform(buffer, ToVector4(draw.CameraUp));
			case nameof(EEngineUniform.CameraRight):
				return UploadUniform(buffer, ToVector4(draw.CameraRight));
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
			case nameof(EEngineUniform.ClipSpaceYDirection):
				return UploadUniform(buffer, (int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection);
			case nameof(EEngineUniform.ClipDepthRange):
				return UploadUniform(buffer, (int)RuntimeEngine.Rendering.EffectiveClipDepthRange);
			case nameof(EEngineUniform.FramebufferTextureYDirection):
				return UploadUniform(buffer, (int)RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan));
			case nameof(EEngineUniform.ScreenWidth):
			case nameof(EEngineUniform.ScreenHeight):
				// Prefer the enqueue-time render-area snapshot; the live RenderArea is empty
				// at deferred record time (see the matching note in TryResolveEngineUniformValue).
				float screenW = draw.RenderAreaWidth;
				float screenH = draw.RenderAreaHeight;
				if (screenW <= 0f || screenH <= 0f)
				{
					screenW = draw.Viewport.Width;
					screenH = MathF.Abs(draw.Viewport.Height);
				}
				if (screenW <= 0f || screenH <= 0f)
				{
					var area = RuntimeEngine.Rendering.State.RenderArea;
					screenW = area.Width;
					screenH = area.Height;
				}
				return UploadUniform(buffer, normalized.Equals(nameof(EEngineUniform.ScreenWidth), StringComparison.Ordinal) ? screenW : screenH);
			case nameof(EEngineUniform.ScreenOrigin):
				return UploadUniform(buffer, new Vector2(0f, 0f));
			case nameof(EEngineUniform.BillboardMode):
				return UploadUniform(buffer, (int)draw.BillboardMode);
			case nameof(EEngineUniform.VRMode):
				return UploadUniform(buffer, stereoPass ? 1 : 0);
			case nameof(EEngineUniform.UIXYWH):
				if (_program is not null &&
					_program.TryGetUniformValue(
						draw.ProgramBindingSnapshot,
						nameof(EEngineUniform.UIXYWH),
						out ProgramUniformValue uiBounds))
					return UploadProgramUniform(buffer, uiBounds);
				return UploadUniform(buffer, Vector4.Zero);
			case nameof(EEngineUniform.UIX):
			case nameof(EEngineUniform.UIY):
			case nameof(EEngineUniform.UIWidth):
			case nameof(EEngineUniform.UIHeight):
				if (_program is not null &&
					_program.TryGetUniformValue(
						draw.ProgramBindingSnapshot,
						normalized,
						out ProgramUniformValue uiScalar))
					return UploadProgramUniform(buffer, uiScalar);
				if (_program is not null &&
					_program.TryGetUniformValue(
						draw.ProgramBindingSnapshot,
						nameof(EEngineUniform.UIXYWH),
						out ProgramUniformValue packedBounds) &&
					packedBounds.TryGetVector4(out Vector4 b))
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

	/// <summary>Strips and caches the vertex-stage suffix ("_VTX") from engine uniform names.</summary>
	private static string NormalizeEngineUniformName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return string.Empty;

		return name.EndsWith(VertexUniformSuffix, StringComparison.Ordinal)
			? NormalizedEngineUniformNames.GetOrAdd(
				name,
				static uniformName => uniformName[..^VertexUniformSuffix.Length])
			: name;
	}

	/// <summary>
	/// Returns the byte size of a named engine uniform.
	/// Returns 0 for unrecognized names (e.g. user-defined uniforms).
	/// </summary>
	private static uint GetEngineUniformSize(string name)
	{
		string normalized = NormalizeEngineUniformName(name);
		return normalized switch
		{
			nameof(EEngineUniform.ModelMatrix) or nameof(EEngineUniform.PrevModelMatrix) or nameof(EEngineUniform.ViewMatrix) or nameof(EEngineUniform.LeftEyeViewMatrix) or nameof(EEngineUniform.RightEyeViewMatrix) or nameof(EEngineUniform.InverseViewMatrix) or nameof(EEngineUniform.InverseProjMatrix) or nameof(EEngineUniform.ProjMatrix) or nameof(EEngineUniform.ViewProjectionMatrix) or nameof(EEngineUniform.LeftEyeViewProjectionMatrix) or nameof(EEngineUniform.RightEyeViewProjectionMatrix) or nameof(EEngineUniform.LeftEyeInverseViewMatrix) or nameof(EEngineUniform.RightEyeInverseViewMatrix) or nameof(EEngineUniform.LeftEyeInverseProjMatrix) or nameof(EEngineUniform.RightEyeInverseProjMatrix) or nameof(EEngineUniform.LeftEyeProjMatrix) or nameof(EEngineUniform.RightEyeProjMatrix) or nameof(EEngineUniform.PrevViewMatrix) or nameof(EEngineUniform.PrevLeftEyeViewMatrix) or nameof(EEngineUniform.PrevRightEyeViewMatrix) or nameof(EEngineUniform.PrevProjMatrix) or nameof(EEngineUniform.PrevLeftEyeProjMatrix) or nameof(EEngineUniform.PrevRightEyeProjMatrix) or nameof(EEngineUniform.RootInvModelMatrix) => (uint)Unsafe.SizeOf<Matrix4x4>(),
			nameof(EEngineUniform.CameraPosition) or nameof(EEngineUniform.CameraForward) or nameof(EEngineUniform.CameraUp) or nameof(EEngineUniform.CameraRight) or "u_SourceFlatDirection" => 16u,
			nameof(EEngineUniform.CameraNearZ) or nameof(EEngineUniform.CameraFarZ) or nameof(EEngineUniform.CameraFovX) or nameof(EEngineUniform.CameraFovY) or nameof(EEngineUniform.CameraAspect) or nameof(EEngineUniform.ScreenWidth) or nameof(EEngineUniform.ScreenHeight) or nameof(EEngineUniform.UpdateDelta) or nameof(EEngineUniform.RenderTime) or nameof(EEngineUniform.EngineTime) or nameof(EEngineUniform.DeltaTime) or nameof(EEngineUniform.DepthMode) or nameof(EEngineUniform.ClipSpaceYDirection) or nameof(EEngineUniform.ClipDepthRange) or nameof(EEngineUniform.FramebufferTextureYDirection) or nameof(EEngineUniform.UIX) or nameof(EEngineUniform.UIY) or nameof(EEngineUniform.UIWidth) or nameof(EEngineUniform.UIHeight) or TransformIdUniformName or SkinPaletteBaseUniformName or SkinPaletteCountUniformName or SkinningInfluenceCapUniformName or BlendshapeActiveCountUniformName or BlendshapeWeightThresholdUniformName or UsePrecombinedBlendshapeDeltasUniformName => 4u,
			nameof(EEngineUniform.ScreenOrigin) => 8u,
			nameof(EEngineUniform.BillboardMode) or nameof(EEngineUniform.VRMode) => 4u,
			nameof(EEngineUniform.UIXYWH) => 16u,
			_ => 0u,
		};
	}

	/// <summary>Converts a Vector3 to Vector4 with W=0 for shader upload.</summary>
	private static Vector4 ToVector4(in Vector3 v) => new(v, 0f);

	/// <summary>Maps and uploads a single unmanaged value to a host-visible UBO.</summary>
	private bool UploadUniform<T>(EngineUniformBuffer buffer, in T value) where T : unmanaged
	{
		uint size = (uint)Unsafe.SizeOf<T>();
		uint copySize = Math.Min(buffer.Size, size);
		if (buffer.UsesMappedFrameArena)
		{
			if (BackendContext.Resources.MappedFrameArena is not { } arena ||
				!arena.TryBeginWrite(buffer.MappedSlice, out VulkanMappedFrameWriteScope write))
			{
				return false;
			}
			using (write)
			{
				T localValue = value;
				ReadOnlySpan<byte> source = new(Unsafe.AsPointer(ref localValue), checked((int)copySize));
				source.CopyTo(write.Bytes);
			}
			return true;
		}

		VulkanMappedMemorySlice mappedMemorySlice = buffer.MappedMemorySlice;
		if (!buffer.UsesMappedMemoryLease ||
			!BackendContext.Resources.Buffers.TryAcquireWrite(BackendContext, in mappedMemorySlice, out VulkanMappedMemoryWriteLease mappedWriteLease))
			return false;
		using (mappedWriteLease)
		{
			T local = value;
			ReadOnlySpan<byte> source = new(Unsafe.AsPointer(ref local), checked((int)copySize));
			source.CopyTo(mappedWriteLease.Bytes);
		}
		return true;
	}

	private bool ClearEngineUniformBuffer(EngineUniformBuffer buffer)
	{
		if (buffer.UsesMappedFrameArena)
		{
			if (BackendContext.Resources.MappedFrameArena is not { } arena ||
				!arena.TryBeginWrite(buffer.MappedSlice, out VulkanMappedFrameWriteScope write))
			{
				return false;
			}
			using (write)
				write.Bytes.Clear();
			return true;
		}

		VulkanMappedMemorySlice mappedMemorySlice = buffer.MappedMemorySlice;
		if (!buffer.UsesMappedMemoryLease ||
			!BackendContext.Resources.Buffers.TryAcquireWrite(BackendContext, in mappedMemorySlice, out VulkanMappedMemoryWriteLease mappedWriteLease))
			return false;
		using (mappedWriteLease)
			mappedWriteLease.Bytes.Clear();
		return true;
	}

	/// <summary>Uploads an inline program uniform value to a host-visible UBO without boxing.</summary>
	private bool UploadProgramUniform(EngineUniformBuffer buffer, ProgramUniformValue value)
	{
		if (!value.HasInlineValue)
			return value.ReferenceValue is { } reference &&
				UploadReferencedProgramUniform(buffer, reference, value.Type);

		return value.Type switch
		{
			EShaderVarType._float => UploadUniform(buffer, value.Float),
			EShaderVarType._int or EShaderVarType._bool => UploadUniform(buffer, value.Int),
			EShaderVarType._uint => UploadUniform(buffer, value.UInt),
			EShaderVarType._vec2 => UploadUniform(buffer, value.Vector2),
			EShaderVarType._vec3 => UploadUniform(buffer, new Vector4(value.Vector3, 0f)),
			EShaderVarType._vec4 => UploadUniform(buffer, value.Vector4),
			EShaderVarType._ivec2 => UploadUniform(buffer, new IVector2(value.IVector4.X, value.IVector4.Y)),
			EShaderVarType._ivec3 => UploadUniform(buffer, value.IVector4),
			EShaderVarType._ivec4 => UploadUniform(buffer, value.IVector4),
			EShaderVarType._uvec2 => UploadUniform(buffer, new UVector2(value.UVector4.X, value.UVector4.Y)),
			EShaderVarType._uvec3 => UploadUniform(buffer, value.UVector4),
			EShaderVarType._uvec4 => UploadUniform(buffer, value.UVector4),
			EShaderVarType._mat4 => UploadUniform(buffer, value.Matrix4x4),
			_ => false
		};
	}

	private bool UploadReferencedProgramUniform(
		EngineUniformBuffer buffer,
		object value,
		EShaderVarType type)
		=> type switch
		{
			EShaderVarType._float => UploadUniform(buffer, Convert.ToSingle(value)),
			EShaderVarType._int => UploadUniform(buffer, Convert.ToInt32(value)),
			EShaderVarType._uint => UploadUniform(buffer, Convert.ToUInt32(value)),
			EShaderVarType._bool => UploadUniform(buffer, Convert.ToBoolean(value) ? 1 : 0),
			EShaderVarType._vec2 when value is Vector2 v2 => UploadUniform(buffer, v2),
			EShaderVarType._vec3 when TryConvertVector3(value, out Vector3 v3) => UploadUniform(buffer, new Vector4(v3, 0f)),
			EShaderVarType._vec4 when TryConvertVector4(value, out Vector4 v4) => UploadUniform(buffer, v4),
			EShaderVarType._ivec2 when value is IVector2 iv2 => UploadUniform(buffer, iv2),
			EShaderVarType._ivec3 when value is IVector3 iv3 => UploadUniform(buffer, new IVector4(iv3.X, iv3.Y, iv3.Z, 0)),
			EShaderVarType._ivec4 when value is IVector4 iv4 => UploadUniform(buffer, iv4),
			EShaderVarType._uvec2 when value is UVector2 uv2 => UploadUniform(buffer, uv2),
			EShaderVarType._uvec3 when value is UVector3 uv3 => UploadUniform(buffer, new UVector4(uv3.X, uv3.Y, uv3.Z, 0)),
			EShaderVarType._uvec4 when value is UVector4 uv4 => UploadUniform(buffer, uv4),
			EShaderVarType._mat4 when value is Matrix4x4 mat => UploadUniform(buffer, mat),
			_ => false
		};
}
