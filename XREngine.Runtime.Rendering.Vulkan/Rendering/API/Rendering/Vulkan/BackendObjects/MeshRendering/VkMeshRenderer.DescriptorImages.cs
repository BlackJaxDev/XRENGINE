// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.Descriptors.cs  – partial class: Descriptor Set Management
//
// Allocates and writes Vulkan descriptor sets for each swapchain frame.
// Resolves buffer, image, and texel-buffer descriptors from the buffer cache,
// material textures, and engine/auto uniform buffers.
// ──────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
	public partial class VkMeshRenderer
	{
		private bool TryResolveImage(
			DescriptorBindingInfo binding,
			XRMaterial material,
			DescriptorType descriptorType,
			out DescriptorImageInfo imageInfo,
			int arrayIndex = 0,
			ComputeDispatchSnapshot? snapshot = null)
		{
			imageInfo = default;
			bool bindless = VulkanBindlessMaterialDescriptors.IsBindlessTextureArrayBinding(binding);
			MaterialTextureBindingResolution textureBinding = snapshot is not null
				? MaterialTextureBindingResolver.Resolve(
					material,
					binding.Name,
					(int)binding.Binding,
					arrayIndex,
					bindless,
					snapshot,
					static (capturedSnapshot, samplerName) =>
						capturedSnapshot.TryGetSamplerTexture(samplerName, out XRTexture? namedTexture)
							? namedTexture
							: null)
				: MaterialTextureBindingResolver.Resolve(
					material,
					binding.Name,
					(int)binding.Binding,
					arrayIndex,
					bindless,
					_program,
					static (program, samplerName) =>
						program is not null && program.TryGetSamplerTexture(samplerName, out XRTexture? namedTexture)
							? namedTexture
							: null);
			XRTexture? texture = textureBinding.Texture;

			if (texture is null)
			{
				// Use a 1×1 magenta placeholder to satisfy the descriptor binding
				// instead of failing the entire descriptor set write.
				imageInfo = Renderer.GetPlaceholderImageInfo(descriptorType, binding.ExpectedImageViewType);
				if (imageInfo.ImageView.Handle != 0)
				{
					LogPostProcessDescriptor(binding, arrayIndex, null, imageInfo, "placeholder-missing-texture");
					LogDeferredLightingDescriptor(binding, arrayIndex, textureBinding, null, null, imageInfo, "placeholder-missing-texture");
					LogMaterialDescriptor(binding, material, arrayIndex, textureBinding, null, null, imageInfo, "placeholder-missing-texture");
					RecordDescriptorFallback(binding);
					return true;
				}

				WarnOnce($"No texture available for descriptor binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}).");
				RecordDescriptorFailure(binding, "missing texture and placeholder unavailable");
				return false;
			}

			bool allowSynchronousTextureUpload = Renderer.AllowSynchronousResourceUploads;
			bool suppressSynchronousTextureUploadForPressure =
				allowSynchronousTextureUpload &&
				Renderer.ShouldAvoidSynchronousImageAllocationForOpenXr(out _);
			if (suppressSynchronousTextureUploadForPressure)
				allowSynchronousTextureUpload = false;

			AbstractRenderAPIObject? apiTextureObject;
			if (allowSynchronousTextureUpload)
			{
				try
				{
					apiTextureObject = Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true);
				}
				catch (VulkanOutOfMemoryException ex)
				{
					if (TryUsePlaceholderDescriptor(binding, descriptorType, arrayIndex, material, textureBinding, texture, "placeholder-texture-allocation-failed", out imageInfo))
						return true;

					WarnOnce($"Texture for descriptor binding '{binding.Name}' could not allocate a Vulkan image: {ex.Message}");
					RecordDescriptorFailure(binding, "texture allocation failed");
					return false;
				}
			}
			else
			{
				Renderer.TryGetAPIRenderObject(texture, out apiTextureObject);
			}

			if (apiTextureObject is not IVkImageDescriptorSource source)
			{
				if (suppressSynchronousTextureUploadForPressure &&
					TryUsePlaceholderDescriptor(binding, descriptorType, arrayIndex, material, textureBinding, texture, "placeholder-texture-allocation-pressure", out imageInfo))
					return true;

				if (!allowSynchronousTextureUpload &&
					TryUsePlaceholderDescriptor(binding, descriptorType, arrayIndex, material, textureBinding, texture, "placeholder-texture-wrapper-not-ready", out imageInfo))
					return true;

				WarnOnce($"Texture for descriptor binding '{binding.Name}' is not a Vulkan texture.");
				RecordDescriptorFailure(binding, "texture has no Vulkan descriptor source");
				return false;
			}

			string descriptorReason = MeshMaterialDescriptorReasons.GetOrAdd(
				binding.Name,
				static bindingName => $"mesh material descriptor '{bindingName}'");
			bool snapshotReady = source.TryGetDescriptorSnapshot(
				binding.ExpectedImageViewType,
				requestedAspectMask: null,
				descriptorReason,
				allowSynchronousTextureUpload,
				out VkImageDescriptorSnapshot descriptorSnapshot);
			if (!snapshotReady)
			{
				if (TryUsePlaceholderDescriptor(binding, descriptorType, arrayIndex, material, textureBinding, texture, "placeholder-texture-not-ready", out imageInfo, source))
					return true;

				WarnOnce($"Texture for descriptor binding '{binding.Name}' is not ready for Vulkan descriptor use.");
				RecordDescriptorFailure(binding, "texture descriptor not ready");
				return false;
			}

			bool requiresSampledUsage = descriptorType is DescriptorType.CombinedImageSampler or DescriptorType.SampledImage or DescriptorType.Sampler or DescriptorType.InputAttachment;
			if (requiresSampledUsage && (descriptorSnapshot.Usage & ImageUsageFlags.SampledBit) == 0)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' is missing VK_IMAGE_USAGE_SAMPLED_BIT.");
				RecordDescriptorFailure(binding, "texture missing VK_IMAGE_USAGE_SAMPLED_BIT");
				return false;
			}

			if (descriptorType == DescriptorType.StorageImage && (descriptorSnapshot.Usage & ImageUsageFlags.StorageBit) == 0)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' is missing VK_IMAGE_USAGE_STORAGE_BIT.");
				RecordDescriptorFailure(binding, "texture missing VK_IMAGE_USAGE_STORAGE_BIT");
				return false;
			}

			if (IsCombinedDepthStencilFormat(descriptorSnapshot.Format) &&
				(descriptorSnapshot.Aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) == (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit))
			{
				bool stencilOnly = RequiresStencilOnlyDescriptor(binding);
				ImageAspectFlags aspectMask = stencilOnly ? ImageAspectFlags.StencilBit : ImageAspectFlags.DepthBit;
				string aspectLabel = stencilOnly ? "stencil-only" : "depth-only";
				if (source.TryGetDescriptorSnapshot(
						binding.ExpectedImageViewType,
						aspectMask,
						descriptorReason,
						allowSynchronousTextureUpload,
						out descriptorSnapshot) &&
					descriptorSnapshot.View.Handle != 0)
				{
					if (!Renderer.IsLiveImageViewBackedByLiveImage(descriptorSnapshot.View))
					{
						if (TryUsePlaceholderDescriptor(binding, descriptorType, arrayIndex, material, textureBinding, texture, "placeholder-retired-image-view", out imageInfo, source))
							return true;

						WarnOnce($"Texture for descriptor binding '{binding.Name}' references a retired Vulkan image view.");
						RecordDescriptorFailure(binding, "texture image view retired");
						return false;
					}

					if (!TryResolveDescriptorSampler(binding, descriptorType, in descriptorSnapshot, out Sampler sampler))
						return false;

					imageInfo = new DescriptorImageInfo
					{
						ImageLayout = Renderer.ResolveDescriptorImageLayout(source, in descriptorSnapshot, descriptorType),
						ImageView = descriptorSnapshot.View,
						Sampler = sampler,
					};
					string detail = ShouldBuildDescriptorDiagnosticDetail(binding)
						? $"{descriptorSnapshot.Format}/{descriptorSnapshot.Aspect}/{aspectLabel}"
						: string.Empty;
					LogPostProcessDescriptor(binding, arrayIndex, texture, imageInfo, detail);
					LogDeferredLightingDescriptor(binding, arrayIndex, textureBinding, texture, source, imageInfo, detail, descriptorSnapshot);
					LogMaterialDescriptor(binding, material, arrayIndex, textureBinding, texture, source, imageInfo, detail, descriptorSnapshot);
					return true;
				}

				WarnOnce($"Texture for descriptor binding '{binding.Name}' uses a combined depth-stencil format and no {aspectLabel} view is available.");
				RecordDescriptorFailure(binding, $"combined depth-stencil texture has no {aspectLabel} view");
				return false;
			}

			if (descriptorSnapshot.View.Handle == 0)
			{
				imageInfo = Renderer.GetPlaceholderImageInfo(descriptorType, binding.ExpectedImageViewType);
				if (imageInfo.ImageView.Handle != 0)
				{
					WarnOnce($"Texture for descriptor binding '{binding.Name}' cannot provide expected view type '{binding.ExpectedImageViewType}'. Using placeholder.");
					LogPostProcessDescriptor(binding, arrayIndex, texture, imageInfo, "placeholder-view-type");
					LogDeferredLightingDescriptor(binding, arrayIndex, textureBinding, texture, source, imageInfo, "placeholder-view-type");
					LogMaterialDescriptor(binding, material, arrayIndex, textureBinding, texture, source, imageInfo, "placeholder-view-type");
					RecordDescriptorFallback(binding);
					return true;
				}

				WarnOnce($"Texture for descriptor binding '{binding.Name}' cannot provide expected view type '{binding.ExpectedImageViewType}'.");
				RecordDescriptorFailure(binding, "texture view type mismatch");
				return false;
			}

			if (!Renderer.IsLiveImageViewBackedByLiveImage(descriptorSnapshot.View))
			{
				if (TryUsePlaceholderDescriptor(binding, descriptorType, arrayIndex, material, textureBinding, texture, "placeholder-retired-image-view", out imageInfo, source))
					return true;

				WarnOnce($"Texture for descriptor binding '{binding.Name}' references a retired Vulkan image view.");
				RecordDescriptorFailure(binding, "texture image view retired");
				return false;
			}

			if (!TryResolveDescriptorSampler(binding, descriptorType, in descriptorSnapshot, out Sampler descriptorSampler))
				return false;

			imageInfo = new DescriptorImageInfo
			{
				ImageLayout = Renderer.ResolveDescriptorImageLayout(source, in descriptorSnapshot, descriptorType),
				ImageView = descriptorSnapshot.View,
				Sampler = descriptorSampler,
			};
			string descriptorDetail = ShouldBuildDescriptorDiagnosticDetail(binding)
				? $"{descriptorSnapshot.Format}/{descriptorSnapshot.Aspect}"
				: string.Empty;
			LogPostProcessDescriptor(binding, arrayIndex, texture, imageInfo, descriptorDetail);
			LogDeferredLightingDescriptor(binding, arrayIndex, textureBinding, texture, source, imageInfo, descriptorDetail, descriptorSnapshot);
			LogMaterialDescriptor(binding, material, arrayIndex, textureBinding, texture, source, imageInfo, descriptorDetail, descriptorSnapshot);
			return imageInfo.ImageView.Handle != 0;
		}

		private bool TryUsePlaceholderDescriptor(
			DescriptorBindingInfo binding,
			DescriptorType descriptorType,
			int arrayIndex,
			XRMaterial material,
			MaterialTextureBindingResolution textureBinding,
			XRTexture? texture,
			string reason,
			out DescriptorImageInfo imageInfo,
			IVkImageDescriptorSource? source = null)
		{
			imageInfo = Renderer.GetPlaceholderImageInfo(descriptorType, binding.ExpectedImageViewType);
			if (imageInfo.ImageView.Handle == 0)
				return false;

			WarnOnce($"Texture for descriptor binding '{binding.Name}' is not ready for Vulkan descriptor use ({reason}). Using placeholder.");
			if (DescriptorTraceEnabled)
			{
				Debug.VulkanWarningEvery(
					$"Vulkan.Descriptor.Placeholder.{GetHashCode()}.{binding.Name}.{binding.Binding}.{arrayIndex}.{reason}",
					TimeSpan.FromSeconds(2),
					"[VulkanDescriptor] fallback={0} program='{1}' mesh='{2}' material='{3}' binding='{4}' set={5} bindingIndex={6} arrayIndex={7} texture='{8}' sourceImage=0x{9:X} sourceView=0x{10:X} imageInfoView=0x{11:X}",
					reason,
					_program?.Data?.Name ?? "<null>",
					Mesh?.Name ?? "<null>",
					material.Name ?? "<unnamed>",
					binding.Name ?? "<null>",
					binding.Set,
					binding.Binding,
					arrayIndex,
					texture?.Name ?? texture?.GetDescribingName() ?? "<null>",
					source?.DescriptorImage.Handle ?? 0,
					source?.DescriptorView.Handle ?? 0,
					imageInfo.ImageView.Handle);
			}

			LogPostProcessDescriptor(binding, arrayIndex, texture, imageInfo, reason);
			LogDeferredLightingDescriptor(binding, arrayIndex, textureBinding, texture, source, imageInfo, reason);
			LogMaterialDescriptor(binding, material, arrayIndex, textureBinding, texture, source, imageInfo, reason);
			RecordDescriptorFallback(binding);
			return true;
		}

		private bool TryResolveDescriptorSampler(
			DescriptorBindingInfo binding,
			DescriptorType descriptorType,
			in VkImageDescriptorSnapshot snapshot,
			out Sampler sampler)
		{
			sampler = default;
			if (descriptorType is not (DescriptorType.CombinedImageSampler or DescriptorType.Sampler))
				return true;

			sampler = snapshot.Sampler;
			if (sampler.Handle != 0 && Renderer.IsLiveSampler(sampler))
				return true;

			if (sampler.Handle != 0)
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' references a retired Vulkan sampler. Using placeholder sampler.");
				RecordDescriptorFallback(binding);
			}

			sampler = Renderer.GetPlaceholderSampler();
			if (sampler.Handle != 0 && Renderer.IsLiveSampler(sampler))
			{
				WarnOnce($"Texture for descriptor binding '{binding.Name}' has no Vulkan sampler. Using placeholder sampler.");
				RecordDescriptorFallback(binding);
				return true;
			}

			WarnOnce($"Texture for descriptor binding '{binding.Name}' has no Vulkan sampler and placeholder sampler is unavailable.");
			RecordDescriptorFailure(binding, "texture sampler unavailable");
			return false;
		}

		private void LogPostProcessDescriptor(
			DescriptorBindingInfo binding,
			int arrayIndex,
			XRTexture? texture,
			DescriptorImageInfo imageInfo,
			string detail)
		{
			if (!RenderDiagnosticsFlags.DiagPostProcess || !IsPostProcessSampler(binding.Name))
				return;

			string textureLabel = texture is null
				? "<null>"
				: $"{(string.IsNullOrWhiteSpace(texture.Name) ? texture.GetType().Name : texture.Name)}#{texture.GetHashCode():X8}";

			Debug.VulkanEvery(
				$"PostProcess.Descriptor.{GetHashCode()}.{binding.Name}.{binding.Binding}.{arrayIndex}",
				TimeSpan.FromSeconds(1),
				"[PostProcessDiag] Descriptor name={0} set={1} binding={2} index={3} type={4} texture={5} layout={6} view=0x{7:X} sampler=0x{8:X} detail={9}",
				binding.Name,
				binding.Set,
				binding.Binding,
				arrayIndex,
				binding.DescriptorType,
				textureLabel,
				imageInfo.ImageLayout,
				imageInfo.ImageView.Handle,
				imageInfo.Sampler.Handle,
				detail);
		}

		private bool ShouldBuildDescriptorDiagnosticDetail(DescriptorBindingInfo binding)
			=> (RenderDiagnosticsFlags.DiagPostProcess && IsPostProcessSampler(binding.Name)) ||
				(DeferredLightingDiagnostics.Enabled && DeferredLightingDiagnostics.IsDeferredLightCombineSampler(binding.Name)) ||
				(MaterialBindingDiagnosticsEnabled && IsMaterialSampler(binding.Name));

		private void LogDeferredLightingDescriptor(
			DescriptorBindingInfo binding,
			int arrayIndex,
			MaterialTextureBindingResolution resolution,
			XRTexture? texture,
			IVkImageDescriptorSource? source,
			DescriptorImageInfo imageInfo,
			string detail,
			VkImageDescriptorSnapshot? snapshot = null)
		{
			if (!DeferredLightingDiagnostics.Enabled || !DeferredLightingDiagnostics.IsDeferredLightCombineSampler(binding.Name))
				return;

			string textureLabel = texture is null
				? "<null>"
				: $"{(string.IsNullOrWhiteSpace(texture.Name) ? texture.GetType().Name : texture.Name)}#{texture.GetHashCode():X8}";
			string programName = _program?.Data?.Name ?? "<null>";
			string meshName = Mesh?.Name ?? "<null>";
			string sourceImage = snapshot.HasValue ? $"0x{snapshot.Value.Image.Handle:X}" : source is null ? "<null>" : $"0x{source.DescriptorImage.Handle:X}";
			string sourceView = snapshot.HasValue ? $"0x{snapshot.Value.View.Handle:X}" : source is null ? "<null>" : $"0x{source.DescriptorView.Handle:X}";
			string sourceSampler = snapshot.HasValue ? $"0x{snapshot.Value.Sampler.Handle:X}" : source is null ? "<null>" : $"0x{source.DescriptorSampler.Handle:X}";
			string sourceLayout = snapshot.HasValue ? snapshot.Value.TrackedLayout.ToString() : source is null ? "<null>" : source.TrackedImageLayout.ToString();
			string sourceUsage = snapshot.HasValue ? snapshot.Value.Usage.ToString() : source is null ? "<null>" : source.DescriptorUsage.ToString();
			string sourceAllocator = snapshot.HasValue ? snapshot.Value.UsesAllocatorImage.ToString() : source is null ? "<null>" : source.UsesAllocatorImage.ToString();

			DeferredLightingDiagnostics.Write(
				"[VkMeshRenderer.Descriptor] " +
				$"program='{programName}' mesh='{meshName}' " +
				$"name='{binding.Name ?? "<null>"}' set={binding.Set} binding={binding.Binding} arrayIndex={arrayIndex} type={binding.DescriptorType} " +
				$"rung={resolution.Rung} resolvedIndex={resolution.TextureIndex} resolvedSampler='{resolution.SamplerName ?? "<null>"}' reason='{resolution.Reason}' " +
				$"texture={textureLabel} imageInfoLayout={imageInfo.ImageLayout} imageInfoView=0x{imageInfo.ImageView.Handle:X} imageInfoSampler=0x{imageInfo.Sampler.Handle:X} " +
				$"sourceImage={sourceImage} sourceView={sourceView} sourceSampler={sourceSampler} sourceLayout={sourceLayout} sourceUsage={sourceUsage} allocatorImage={sourceAllocator} " +
				$"detail={detail}");
		}

		private void LogMaterialDescriptor(
			DescriptorBindingInfo binding,
			XRMaterial material,
			int arrayIndex,
			MaterialTextureBindingResolution resolution,
			XRTexture? texture,
			IVkImageDescriptorSource? source,
			DescriptorImageInfo imageInfo,
			string detail,
			VkImageDescriptorSnapshot? snapshot = null)
		{
			if (!MaterialBindingDiagnosticsEnabled || !IsMaterialSampler(binding.Name))
				return;

			string textureLabel = texture is null
				? "<null>"
				: $"{(string.IsNullOrWhiteSpace(texture.Name) ? texture.GetType().Name : texture.Name)}#{texture.GetHashCode():X8}";
			string sourceLayout = snapshot.HasValue ? snapshot.Value.TrackedLayout.ToString() : source is null ? "<null>" : source.TrackedImageLayout.ToString();
			string sourceUsage = snapshot.HasValue ? snapshot.Value.Usage.ToString() : source is null ? "<null>" : source.DescriptorUsage.ToString();
			string programName = _program?.Data?.Name ?? "<null>";
			string meshName = Mesh?.Name ?? "<null>";
			string materialName = material.Name ?? "<null>";

			Debug.VulkanEvery(
				$"Vulkan.MaterialDescriptor.{GetHashCode()}.{programName}.{materialName}.{binding.Name}.{arrayIndex}",
				TimeSpan.FromSeconds(1),
				"[VkMaterialDescriptor] program='{0}' mesh='{1}' material='{2}' name='{3}' set={4} binding={5} arrayIndex={6} type={7} " +
				"rung={8} resolvedIndex={9} resolvedSampler='{10}' reason='{11}' texture={12} imageLayout={13} view=0x{14:X} sampler=0x{15:X} sourceLayout={16} sourceUsage={17} detail={18}",
				programName,
				meshName,
				materialName,
				binding.Name ?? "<null>",
				binding.Set,
				binding.Binding,
				arrayIndex,
				binding.DescriptorType,
				resolution.Rung,
				resolution.TextureIndex,
				resolution.SamplerName ?? "<null>",
				resolution.Reason,
				textureLabel,
				imageInfo.ImageLayout,
				imageInfo.ImageView.Handle,
				imageInfo.Sampler.Handle,
				sourceLayout,
				sourceUsage,
				detail);
		}

		private static bool IsPostProcessSampler(string? name)
			=> string.Equals(name, "HDRSceneTex", StringComparison.Ordinal)
			|| string.Equals(name, "BloomBlurTexture", StringComparison.Ordinal)
			|| string.Equals(name, "DepthView", StringComparison.Ordinal)
			|| string.Equals(name, "StencilView", StringComparison.Ordinal)
			|| string.Equals(name, "AutoExposureTex", StringComparison.Ordinal)
			|| string.Equals(name, "AtmosphereColor", StringComparison.Ordinal)
			|| string.Equals(name, "VolumetricFogColor", StringComparison.Ordinal);

		private static bool IsMaterialSampler(string? name)
			=> name is not null &&
			   name.StartsWith("Texture", StringComparison.Ordinal);

		private static bool RequiresStencilOnlyDescriptor(DescriptorBindingInfo binding)
			=> binding.Name?.Contains("Stencil", StringComparison.OrdinalIgnoreCase) == true;

		private ImageView ResolveDescriptorView(DescriptorBindingInfo binding, IVkImageDescriptorSource source)
		{
			if (binding.ExpectedImageViewType is not { } expectedViewType)
				return source.DescriptorView;

			return source.GetDescriptorView(expectedViewType);
		}

		/// <summary>Returns true if the Vulkan format is a combined depth+stencil format.</summary>
		private static bool IsCombinedDepthStencilFormat(Format format)
			=> format is Format.D24UnormS8Uint
				or Format.D32SfloatS8Uint
				or Format.D16UnormS8Uint;

		/// <summary>
		/// Resolves a texel buffer view descriptor from the material's textures.
		/// The texture must implement <see cref="IVkTexelBufferDescriptorSource"/>.
		/// </summary>
		private bool TryResolveTexelBuffer(DescriptorBindingInfo binding, XRMaterial material, out BufferView texelView, int arrayIndex = 0)
		{
			texelView = default;
			MaterialTextureBindingResolution textureBinding = MaterialTextureBindingResolver.Resolve(
				material,
				binding.Name,
				(int)binding.Binding,
				arrayIndex,
				false,
				_program,
				static (program, samplerName) =>
					program is not null && program.TryGetSamplerTexture(samplerName, out XRTexture? namedTexture)
						? namedTexture
						: null);
			XRTexture? texture = textureBinding.Texture;

			if (texture is null)
			{
				WarnOnce($"No texture available for texel descriptor binding '{binding.Name}' (set {binding.Set}, binding {binding.Binding}).");
				RecordDescriptorFailure(binding, "missing texel buffer texture");
				return false;
			}

			if (Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true) is not IVkTexelBufferDescriptorSource source)
			{
				WarnOnce($"Texture for texel descriptor binding '{binding.Name}' is not a Vulkan texel-buffer texture.");
				RecordDescriptorFailure(binding, "texture has no Vulkan texel-buffer source");
				return false;
			}

			texelView = source.DescriptorBufferView;
			return texelView.Handle != 0;
		}

		/// <summary>
		/// Resolves a descriptor buffer binding for a built-in engine uniform
		/// (e.g. ModelMatrix, ViewMatrix). Creates the per-frame UBO on demand.
		/// </summary>
				}
}
