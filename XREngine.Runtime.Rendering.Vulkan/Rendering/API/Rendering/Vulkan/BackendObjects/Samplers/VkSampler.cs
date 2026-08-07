using System;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering.Vulkan;

internal unsafe class VkSampler(VulkanRenderer api, XRSampler data) : VkObject<XRSampler>(api, data)
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
            AnisotropyEnable = Vk.False,
            MaxAnisotropy = Math.Max(1f, Data.MaxAnisotropy),
            CompareEnable = Data.EnableComparison ? Vk.True : Vk.False,
            CompareOp = SamplerConversions.FromCompareOp(Data.CompareFunc),
            UnnormalizedCoordinates = Vk.False,
        };

        if (Data.EnableAnisotropy && BackendContext.Supports(EVulkanDeviceCapability.Anisotropy))
        {
            Api!.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties props);
            if (props.Limits.MaxSamplerAnisotropy > 1f)
            {
                samplerInfo.AnisotropyEnable = Vk.True;
                samplerInfo.MaxAnisotropy = MathF.Min(samplerInfo.MaxAnisotropy, props.Limits.MaxSamplerAnisotropy);
            }
        }

        if (Api!.CreateSampler(Device, ref samplerInfo, null, out _sampler) != Result.Success)
            throw new Exception("Failed to create Vulkan sampler.");

        BackendContext.RegisterSampler(_sampler, in samplerInfo, nameof(VkSampler));
    }

    private void DestroySampler()
    {
        if (_sampler.Handle == 0)
            return;

        Renderer.RetireSampler(_sampler);
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
