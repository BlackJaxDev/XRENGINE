using System;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        public class VkSampler(VulkanRenderer api, XRSampler data) : VkObject<XRSampler>(api, data)
        {
            private Sampler _sampler;

            public Sampler Handle => _sampler;

            public override VkObjectType Type => VkObjectType.Sampler;
            public override bool IsGenerated => _sampler.Handle != 0;

            protected override uint CreateObjectInternal()
            {
                CreateSampler();
                return CacheObject(this);
            }

            protected override void DeleteObjectInternal()
                => DestroySampler();

            protected override void LinkData()
                => Data.PropertyChanged += OnSamplerPropertyChanged;

            protected override void UnlinkData()
                => Data.PropertyChanged -= OnSamplerPropertyChanged;

            private void OnSamplerPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                if (!IsGenerated)
                    return;

                switch (e.PropertyName)
                {
                    case nameof(XRSampler.MinFilter):
                    case nameof(XRSampler.MagFilter):
                    case nameof(XRSampler.UWrap):
                    case nameof(XRSampler.VWrap):
                    case nameof(XRSampler.WWrap):
                    case nameof(XRSampler.MinLod):
                    case nameof(XRSampler.MaxLod):
                    case nameof(XRSampler.LodBias):
                    case nameof(XRSampler.EnableAnisotropy):
                    case nameof(XRSampler.MaxAnisotropy):
                    case nameof(XRSampler.EnableComparison):
                    case nameof(XRSampler.CompareFunc):
                    case nameof(XRSampler.BorderColor):
                        RecreateSampler();
                        break;
                }
            }

            private void RecreateSampler()
            {
                DestroySampler();
                CreateSampler();
            }

            private void CreateSampler()
            {
                DestroySampler();

                (Filter minFilter, SamplerMipmapMode mipmapMode) = SamplerConversions.FromMinFilter(Data.MinFilter);
                Filter magFilter = SamplerConversions.FromMagFilter(Data.MagFilter);

                SamplerCreateInfo samplerInfo = new()
                {
                    SType = StructureType.SamplerCreateInfo,
                    MagFilter = magFilter,
                    MinFilter = minFilter,
                    MipmapMode = mipmapMode,
                    AddressModeU = SamplerConversions.FromWrap(Data.UWrap),
                    AddressModeV = SamplerConversions.FromWrap(Data.VWrap),
                    AddressModeW = SamplerConversions.FromWrap(Data.WWrap),
                    MipLodBias = Data.LodBias,
                    MinLod = Data.MinLod,
                    MaxLod = Math.Max(Data.MinLod, Data.MaxLod),
                    BorderColor = ConvertBorderColor(Data.BorderColor),
                    AnisotropyEnable = Data.EnableAnisotropy ? Vk.True : Vk.False,
                    MaxAnisotropy = Math.Max(1f, Data.MaxAnisotropy),
                    CompareEnable = Data.EnableComparison ? Vk.True : Vk.False,
                    CompareOp = SamplerConversions.FromCompareOp(Data.CompareFunc),
                    UnnormalizedCoordinates = Vk.False,
                };

                if (Data.EnableAnisotropy)
                {
                    Api!.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties props);
                    samplerInfo.MaxAnisotropy = MathF.Min(samplerInfo.MaxAnisotropy, props.Limits.MaxSamplerAnisotropy);
                }

                if (Api!.CreateSampler(Device, ref samplerInfo, null, out _sampler) != Result.Success)
                    throw new Exception("Failed to create Vulkan sampler.");
            }

            private void DestroySampler()
            {
                if (_sampler.Handle == 0)
                    return;
                Api!.DestroySampler(Device, _sampler, null);
                _sampler = default;
            }

            private static BorderColor ConvertBorderColor(ColorF4 color)
            {
                if (color.A <= 0.0001f)
                    return BorderColor.FloatTransparentBlack;

                bool isWhite = AlmostEqual(color.R, 1f) && AlmostEqual(color.G, 1f) && AlmostEqual(color.B, 1f);
                bool isBlack = AlmostEqual(color.R, 0f) && AlmostEqual(color.G, 0f) && AlmostEqual(color.B, 0f);

                if (isWhite)
                    return BorderColor.FloatOpaqueWhite;
                if (isBlack)
                    return BorderColor.FloatOpaqueBlack;

                return color.R > 0.5f || color.G > 0.5f || color.B > 0.5f
                    ? BorderColor.FloatOpaqueWhite
                    : BorderColor.FloatOpaqueBlack;
            }

            private static bool AlmostEqual(float a, float b)
                => Math.Abs(a - b) <= 0.0001f;
        }

        internal static class SamplerConversions
        {
            public static (Filter filter, SamplerMipmapMode mipmap) FromMinFilter(ETexMinFilter filter)
                => filter switch
                {
                    ETexMinFilter.Nearest => (Filter.Nearest, SamplerMipmapMode.Nearest),
                    ETexMinFilter.Linear => (Filter.Linear, SamplerMipmapMode.Nearest),
                    ETexMinFilter.NearestMipmapNearest => (Filter.Nearest, SamplerMipmapMode.Nearest),
                    ETexMinFilter.LinearMipmapNearest => (Filter.Linear, SamplerMipmapMode.Nearest),
                    ETexMinFilter.NearestMipmapLinear => (Filter.Nearest, SamplerMipmapMode.Linear),
                    ETexMinFilter.LinearMipmapLinear => (Filter.Linear, SamplerMipmapMode.Linear),
                    _ => (Filter.Linear, SamplerMipmapMode.Linear),
                };

            public static Filter FromMagFilter(ETexMagFilter filter)
                => filter switch
                {
                    ETexMagFilter.Nearest => Filter.Nearest,
                    ETexMagFilter.Linear => Filter.Linear,
                    _ => Filter.Linear,
                };

            public static SamplerAddressMode FromWrap(ETexWrapMode mode)
                => mode switch
                {
                    ETexWrapMode.Repeat => SamplerAddressMode.Repeat,
                    ETexWrapMode.MirroredRepeat => SamplerAddressMode.MirroredRepeat,
                    ETexWrapMode.ClampToEdge => SamplerAddressMode.ClampToEdge,
                    ETexWrapMode.ClampToBorder => SamplerAddressMode.ClampToBorder,
                    _ => SamplerAddressMode.Repeat,
                };

            public static CompareOp FromCompareOp(ETextureCompareFunc func)
                => func switch
                {
                    ETextureCompareFunc.Never => CompareOp.Never,
                    ETextureCompareFunc.Less => CompareOp.Less,
                    ETextureCompareFunc.Equal => CompareOp.Equal,
                    ETextureCompareFunc.LessOrEqual => CompareOp.LessOrEqual,
                    ETextureCompareFunc.Greater => CompareOp.Greater,
                    ETextureCompareFunc.NotEqual => CompareOp.NotEqual,
                    ETextureCompareFunc.GreaterOrEqual => CompareOp.GreaterOrEqual,
                    ETextureCompareFunc.Always => CompareOp.Always,
                    _ => CompareOp.Never,
                };
        }
    }
}
