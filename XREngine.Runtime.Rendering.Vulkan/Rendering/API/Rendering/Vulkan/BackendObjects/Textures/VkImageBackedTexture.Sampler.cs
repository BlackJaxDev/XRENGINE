using Silk.NET.Vulkan;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal abstract partial class VkImageBackedTexture<TTexture> : VkTexture<TTexture>, IVkFrameBufferAttachmentSource where TTexture : XRTexture
    {
        #region Sampler Management

        /// <summary>Destroys the sampler and resets the handle.</summary>
        private void DestroySampler()
        {
            if (_sampler.Handle != 0)
            {
                Renderer.RetireSampler(_sampler);
                _sampler = default;
            }
        }

        /// <summary>
        /// Resolves the Vulkan image format from engine texture data.
        /// For resizable textures with mip data, the first mip's <c>InternalFormat</c>
        /// is treated as authoritative; otherwise <c>SizedInternalFormat</c> is used.
        /// </summary>
        private Format ReadFormatFromData()
        {
            ESizedInternalFormat sizedFormat = ReadSizedFormatFromData();

            if (IsResizableTexture(Data)
                && TryReadFirstMipmapInternalFormat(Data, out EPixelInternalFormat mipInternalFormat))
            {
                Format mipDerivedFormat = VkFormatConversions.FromPixelInternalFormat(mipInternalFormat);
                if (mipDerivedFormat != Format.Undefined)
                    return mipDerivedFormat;
            }

            return VkFormatConversions.FromSizedFormat(sizedFormat);
        }

        /// <summary>
        /// Reads <c>SizedInternalFormat</c> from the concrete engine texture.
        /// </summary>
        private ESizedInternalFormat ReadSizedFormatFromData()
            => Data switch
            {
                XRTexture1D t => t.SizedInternalFormat,
                XRTexture1DArray t => t.SizedInternalFormat,
                XRTexture2D t => t.SizedInternalFormat,
                XRTexture2DArray t => t.SizedInternalFormat,
                XRTexture3D t => t.SizedInternalFormat,
                XRTextureCube t => t.SizedInternalFormat,
                XRTextureCubeArray t => t.SizedInternalFormat,
                XRTextureRectangle t => t.SizedInternalFormat,
                _ => ESizedInternalFormat.Rgba8,
            };

        private SampleCountFlags ReadSampleCountFromData()
            => Data switch
            {
                XRTexture2D tex2D => ToSampleCountFlags(tex2D.MultiSampleCount),
                XRTexture2DArray texArray when texArray.MultiSample && texArray.Textures.Length > 0
                    => ToSampleCountFlags(Math.Max(2u, texArray.Textures[0].MultiSampleCount)),
                _ => SampleCountFlags.Count1Bit,
            };

        private static SampleCountFlags ToSampleCountFlags(uint samples)
            => samples switch
            {
                <= 1u => SampleCountFlags.Count1Bit,
                2u => SampleCountFlags.Count2Bit,
                3u or 4u => SampleCountFlags.Count4Bit,
                <= 8u => SampleCountFlags.Count8Bit,
                <= 16u => SampleCountFlags.Count16Bit,
                <= 32u => SampleCountFlags.Count32Bit,
                _ => SampleCountFlags.Count64Bit,
            };

        /// <summary>
        /// Determines whether the concrete texture should be treated as resizable.
        /// </summary>
        private static bool IsResizableTexture(XRTexture texture)
            => texture switch
            {
                XRTexture1D t => t.Resizable,
                XRTexture1DArray t => t.Resizable,
                XRTexture2D t => t.Resizable,
                XRTexture2DArray t => t.Resizable,
                XRTexture3D t => t.Resizable,
                XRTextureCube t => t.Resizable,
                XRTextureCubeArray t => t.Resizable,
                XRTextureRectangle t => t.Resizable,
                _ => texture.IsResizeable,
            };

        /// <summary>
        /// Attempts to read the first mipmap's <see cref="EPixelInternalFormat"/> from the concrete texture.
        /// </summary>
        private static bool TryReadFirstMipmapInternalFormat(XRTexture texture, out EPixelInternalFormat internalFormat)
        {
            switch (texture)
            {
                case XRTexture1D t when t.Mipmaps is { Length: > 0 }:
                    internalFormat = t.Mipmaps[0].InternalFormat;
                    return true;

                case XRTexture1DArray t
                    when t.Textures is { Length: > 0 }
                         && t.Textures[0].Mipmaps is { Length: > 0 }:
                    internalFormat = t.Textures[0].Mipmaps[0].InternalFormat;
                    return true;

                case XRTexture2D t when t.Mipmaps is { Length: > 0 }:
                    internalFormat = t.Mipmaps[0].InternalFormat;
                    return true;

                case XRTexture2DArray t when t.Mipmaps is { Length: > 0 }:
                    internalFormat = t.Mipmaps[0].InternalFormat;
                    return true;

                case XRTexture3D t when t.Mipmaps is { Length: > 0 }:
                    internalFormat = t.Mipmaps[0].InternalFormat;
                    return true;

                case XRTextureCube t
                    when t.Mipmaps is { Length: > 0 }
                         && t.Mipmaps[0].Sides is { Length: > 0 }:
                    internalFormat = t.Mipmaps[0].Sides[0].InternalFormat;
                    return true;

                case XRTextureCubeArray t
                    when t.Cubes is { Length: > 0 }
                         && t.Cubes[0].Mipmaps is { Length: > 0 }
                         && t.Cubes[0].Mipmaps[0].Sides is { Length: > 0 }:
                    internalFormat = t.Cubes[0].Mipmaps[0].Sides[0].InternalFormat;
                    return true;
            }

            internalFormat = default;
            return false;
        }

        /// <summary>
        /// Reads sampler-related properties (filter, wrap, LOD bias) from the engine-side
        /// <see cref="Data"/> texture using pattern matching, since the XRTexture hierarchy
        /// does not expose these through a common interface. Values are converted to Vulkan
        /// types via <see cref="SamplerConversions"/>.
        /// </summary>
        private (Filter minFilter, Filter magFilter, SamplerMipmapMode mipmapMode,
                 SamplerAddressMode uWrap, SamplerAddressMode vWrap, SamplerAddressMode wWrap,
                 float lodBias) ReadSamplerSettingsFromData()
        {
            // Defaults when the concrete type doesn't expose a particular property.
            ETexMinFilter engineMin = ETexMinFilter.Linear;
            ETexMagFilter engineMag = ETexMagFilter.Linear;
            ETexWrapMode  engineU   = ETexWrapMode.Repeat;
            ETexWrapMode  engineV   = ETexWrapMode.Repeat;
            ETexWrapMode  engineW   = ETexWrapMode.Repeat;
            float         lodBias   = 0f;

            switch (Data)
            {
                case XRTexture1D t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; lodBias = t.LodBias;
                    break;
                case XRTexture1DArray t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; lodBias = t.LodBias;
                    break;
                case XRTexture2D t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; engineV = t.VWrap; lodBias = t.LodBias;
                    break;
                case XRTexture2DArray t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; engineV = t.VWrap; lodBias = t.LodBias;
                    break;
                case XRTexture3D t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; engineV = t.VWrap; engineW = t.WWrap; lodBias = t.LodBias;
                    break;
                case XRTextureCube t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; engineV = t.VWrap; engineW = t.WWrap; lodBias = t.LodBias;
                    break;
                case XRTextureCubeArray t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; engineV = t.VWrap; engineW = t.WWrap; lodBias = t.LodBias;
                    break;
                case XRTextureRectangle t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; engineV = t.VWrap; lodBias = t.LodBias;
                    break;
            }

            var (minFilter, mipmapMode) = SamplerConversions.FromMinFilter(engineMin);
            Filter magFilter = SamplerConversions.FromMagFilter(engineMag);

            return (minFilter, magFilter, mipmapMode,
                    SamplerConversions.FromWrap(engineU),
                    SamplerConversions.FromWrap(engineV),
                    SamplerConversions.FromWrap(engineW),
                    lodBias);
        }

        /// <summary>
        /// Creates a Vulkan <see cref="Silk.NET.Vulkan.Sampler"/> by reading filter, wrap, and
        /// mipmap settings from the engine-side <see cref="Data"/> texture. Anisotropic filtering
        /// is enabled when the device supports it.
        /// </summary>
        private void CreateSamplerInternal()
        {
            DestroySampler();

            // Read sampler settings from the engine-side XRTexture data source.
            var (minFilter, magFilter, mipmapMode, uWrap, vWrap, wWrap, lodBias) = ReadSamplerSettingsFromData();
            var (minLod, maxLod) = ResolveSamplerLodRange();
            var (compareEnable, compareOp) = ReadCompareSettingsFromData();

            // Determine whether anisotropic filtering is available.
            var anisotropyEnable = Vk.False;
            float maxAnisotropy = 1f;
            if (Renderer.SamplerAnisotropyEnabled)
            {
                float requestedAnisotropy = Data is XRTexture2D texture2D ? texture2D.MaxAnisotropy : 1.0f;
                Api!.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties props);
                if (requestedAnisotropy > 1.0f && props.Limits.MaxSamplerAnisotropy > 1f)
                {
                    anisotropyEnable = Vk.True;
                    maxAnisotropy = MathF.Min(props.Limits.MaxSamplerAnisotropy, requestedAnisotropy);
                }
            }

            SamplerCreateInfo samplerInfo = new()
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = magFilter,
                MinFilter = minFilter,
                AddressModeU = uWrap,
                AddressModeV = vWrap,
                AddressModeW = wWrap,
                AnisotropyEnable = anisotropyEnable,
                MaxAnisotropy = maxAnisotropy,
                BorderColor = BorderColor.IntOpaqueBlack,
                UnnormalizedCoordinates = Vk.False,
                CompareEnable = compareEnable,
                CompareOp = compareOp,
                MipmapMode = mipmapMode,
                MipLodBias = lodBias,
                MinLod = minLod,
                MaxLod = maxLod,
            };

            if (Api!.CreateSampler(Device, ref samplerInfo, null, out _sampler) != Result.Success)
                throw new Exception("Failed to create sampler.");

            Renderer.RegisterLiveSampler(_sampler, in samplerInfo);
        }

        private (float minLod, float maxLod) ResolveSamplerLodRange()
        {
            float maxMip = Math.Max(0f, ResolvedMipLevels - 1u);
            float min = Math.Clamp(Math.Max(Data.MinLOD, Data.LargestMipmapLevel), 0f, maxMip);
            float max = Math.Clamp(Math.Min(Data.MaxLOD, Data.SmallestAllowedMipmapLevel), min, maxMip);
            return (min, max);
        }

        private (uint compareEnable, CompareOp compareOp) ReadCompareSettingsFromData()
        {
            bool enabled = false;
            ETextureCompareFunc func = ETextureCompareFunc.LessOrEqual;

            switch (Data)
            {
                case XRTexture2D texture2D:
                    enabled = texture2D.EnableComparison;
                    func = texture2D.CompareFunc;
                    break;
                case XRTexture2DArray texture2DArray:
                    enabled = texture2DArray.EnableComparison;
                    func = texture2DArray.CompareFunc;
                    break;
            }

            return (enabled ? Vk.True : Vk.False, enabled ? SamplerConversions.FromCompareOp(func) : CompareOp.Always);
        }

        #endregion
    }
}
