using System;
using Silk.NET.Vulkan;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        public class VkTextureView(VulkanRenderer api, XRTextureViewBase data) : VkObject<XRTextureViewBase>(api, data), IVkFrameBufferAttachmentSource, IVkTexelBufferDescriptorSource
        {
            private Image _image;
            private ImageView _view;
            private ImageView _depthOnlyView;
            private ImageView _stencilOnlyView;
            private Sampler _sampler;
            private Format _format = Format.R8G8B8A8Unorm;
            private ImageAspectFlags _aspect = ImageAspectFlags.ColorBit;
            private ImageUsageFlags _usage = ImageUsageFlags.SampledBit;
            private SampleCountFlags _samples = SampleCountFlags.Count1Bit;
            private BufferView _texelBufferView;
            private Format _texelBufferFormat = Format.Undefined;

            public override VkObjectType Type => VkObjectType.Texture;
            public override bool IsGenerated => _view.Handle != 0 || _texelBufferView.Handle != 0;

            internal ImageView View => _view;
            internal Sampler Sampler => _sampler;
            internal Format ResolvedFormat => _format;
            internal ImageAspectFlags AspectFlags => _aspect;
            internal SampleCountFlags SampleCount => _samples;
            internal BufferView TexelBufferView => _texelBufferView;
            internal Format TexelBufferFormat => _texelBufferFormat;


            Image IVkImageDescriptorSource.DescriptorImage
            {
                get
                {
                    RefreshFromViewedTextureIfStale();
                    return _image;
                }
            }
            ImageView IVkImageDescriptorSource.DescriptorView
            {
                get
                {
                    RefreshFromViewedTextureIfStale();
                    return _view;
                }
            }
            ImageViewType IVkImageDescriptorSource.DescriptorViewType => ResolveViewType(Data.TextureTarget);
            Sampler IVkImageDescriptorSource.DescriptorSampler
            {
                get
                {
                    RefreshFromViewedTextureIfStale();
                    return _sampler;
                }
            }
            Format IVkImageDescriptorSource.DescriptorFormat
            {
                get
                {
                    RefreshFromViewedTextureIfStale();
                    return _format;
                }
            }
            ImageAspectFlags IVkImageDescriptorSource.DescriptorAspect
            {
                get
                {
                    RefreshFromViewedTextureIfStale();
                    return _aspect;
                }
            }
            ImageUsageFlags IVkImageDescriptorSource.DescriptorUsage
            {
                get
                {
                    RefreshFromViewedTextureIfStale();
                    return _usage;
                }
            }
            SampleCountFlags IVkImageDescriptorSource.DescriptorSamples
            {
                get
                {
                    RefreshFromViewedTextureIfStale();
                    return _samples;
                }
            }
            ImageLayout IVkImageDescriptorSource.TrackedImageLayout
            {
                get
                {
                    RefreshFromViewedTextureIfStale();

                    XRTexture viewedTexture = Data.GetViewedTexture();
                    if (viewedTexture is null)
                        return ImageLayout.Undefined;

                    return Renderer.GetOrCreateAPIRenderObject(viewedTexture, generateNow: true) is IVkImageDescriptorSource source
                        ? source.TrackedImageLayout
                        : ImageLayout.Undefined;
                }
            }
            bool IVkImageDescriptorSource.UsesAllocatorImage
            {
                get
                {
                    XRTexture viewedTexture = Data.GetViewedTexture();
                    if (viewedTexture is null)
                        return false;

                    return Renderer.GetOrCreateAPIRenderObject(viewedTexture, generateNow: true) is IVkImageDescriptorSource source
                        && source.UsesAllocatorImage;
                }
            }
            ImageView IVkImageDescriptorSource.GetDepthOnlyDescriptorView()
            {
                RefreshFromViewedTextureIfStale();

                return GetAspectOnlyDescriptorView(ImageAspectFlags.DepthBit, ref _depthOnlyView);
            }

            ImageView IVkImageDescriptorSource.GetStencilOnlyDescriptorView()
            {
                RefreshFromViewedTextureIfStale();

                return GetAspectOnlyDescriptorView(ImageAspectFlags.StencilBit, ref _stencilOnlyView);
            }

            private ImageView GetAspectOnlyDescriptorView(ImageAspectFlags aspect, ref ImageView cached)
            {
                if (!IsCombinedDepthStencilFormat(_format) ||
                    (_aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit))
                    return default;

                if (cached.Handle != 0)
                    return cached;

                if (_image.Handle == 0)
                    return default;

                ImageSubresourceRange subresourceRange = new()
                {
                    AspectMask = aspect,
                    BaseMipLevel = Data.MinLevel,
                    LevelCount = Math.Max(Data.NumLevels, 1u),
                    BaseArrayLayer = Data.MinLayer,
                    LayerCount = Math.Max(Data.NumLayers, 1u),
                };

                ImageViewCreateInfo depthViewInfo = new()
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = _image,
                    ViewType = ResolveViewType(Data.TextureTarget),
                    Format = _format,
                    SubresourceRange = subresourceRange,
                };

                if (Api!.CreateImageView(Device, ref depthViewInfo, null, out cached) != Result.Success)
                    return default;

                return cached;
            }
            BufferView IVkTexelBufferDescriptorSource.DescriptorBufferView => _texelBufferView;
            Format IVkTexelBufferDescriptorSource.DescriptorBufferFormat => _texelBufferFormat;

            protected override uint CreateObjectInternal()
            {
                CreateView();
                return CacheObject(this);
            }

            protected override void DeleteObjectInternal()
            {
                if (_depthOnlyView.Handle != 0)
                {
                    Api!.DestroyImageView(Device, _depthOnlyView, null);
                    _depthOnlyView = default;
                }

                DestroySampler();

                if (_view.Handle != 0)
                {
                    Api!.DestroyImageView(Device, _view, null);
                    _view = default;
                }

                if (_stencilOnlyView.Handle != 0)
                {
                    Api!.DestroyImageView(Device, _stencilOnlyView, null);
                    _stencilOnlyView = default;
                }

                _image = default;
                _sampler = default;
                _format = Format.R8G8B8A8Unorm;
                _aspect = ImageAspectFlags.ColorBit;
                _usage = ImageUsageFlags.SampledBit;
                _samples = SampleCountFlags.Count1Bit;
                _texelBufferView = default;
                _texelBufferFormat = Format.Undefined;
            }

            protected override void LinkData()
            {
                Data.ViewedTextureChanged += OnViewedTextureChanged;
                Data.PropertyChanged += OnTextureViewPropertyChanged;
            }

            protected override void UnlinkData()
            {
                Data.ViewedTextureChanged -= OnViewedTextureChanged;
                Data.PropertyChanged -= OnTextureViewPropertyChanged;
            }

            ImageView IVkFrameBufferAttachmentSource.GetAttachmentView(int mipLevel, int layerIndex)
            {
                RefreshFromViewedTextureIfStale();
                return _view;
            }

            void IVkFrameBufferAttachmentSource.EnsureAttachmentLayout(bool depthStencil)
            {
                XRTexture viewedTexture = Data.GetViewedTexture();
                if (viewedTexture is null)
                    return;

                if (Renderer.GetOrCreateAPIRenderObject(viewedTexture, generateNow: true) is IVkFrameBufferAttachmentSource source)
                    source.EnsureAttachmentLayout(depthStencil);
            }

            void IVkFrameBufferAttachmentSource.UpdateTrackedLayout(ImageLayout layout)
            {
                XRTexture viewedTexture = Data.GetViewedTexture();
                if (viewedTexture is null)
                    return;

                if (Renderer.GetOrCreateAPIRenderObject(viewedTexture, generateNow: true) is IVkFrameBufferAttachmentSource source)
                    source.UpdateTrackedLayout(layout);
            }

            bool IVkImageDescriptorSource.TryTransitionDedicatedImageLayout(ImageLayout oldLayout, ImageLayout newLayout)
            {
                XRTexture viewedTexture = Data.GetViewedTexture();
                if (viewedTexture is null)
                    return false;

                return Renderer.GetOrCreateAPIRenderObject(viewedTexture, generateNow: true) is IVkImageDescriptorSource source &&
                    source.TryTransitionDedicatedImageLayout(oldLayout, newLayout);
            }

            private void OnViewedTextureChanged()
            {
                if (IsActive)
                {
                    Destroy();
                    Generate();
                }
            }

            private void OnTextureViewPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                switch (e.PropertyName)
                {
                    case nameof(XRTextureViewBase.TextureTarget):
                    case nameof(XRTextureViewBase.MinLevel):
                    case nameof(XRTextureViewBase.NumLevels):
                    case nameof(XRTextureViewBase.MinLayer):
                    case nameof(XRTextureViewBase.NumLayers):
                    case nameof(XRTextureViewBase.InternalFormat):
                        if (IsActive)
                        {
                            Destroy();
                            Generate();
                        }
                        break;
                    case nameof(XRTextureViewBase.MinFilter):
                    case nameof(XRTextureViewBase.MagFilter):
                    case nameof(XRTextureViewBase.UWrap):
                    case nameof(XRTextureViewBase.VWrap):
                    case nameof(XRTextureViewBase.LodBias):
                        if (IsActive && _texelBufferView.Handle == 0)
                            RecreateSampler();
                        break;
                }
            }

            private void CreateView()
            {
                XRTexture? viewedTexture = Data.GetViewedTexture();
                if (viewedTexture is null)
                    throw new InvalidOperationException("Texture view requires a valid viewed texture.");

                AbstractRenderAPIObject? apiObject = Renderer.GetOrCreateAPIRenderObject(viewedTexture, generateNow: true);
                if (apiObject is IVkTexelBufferDescriptorSource texelSource)
                {
                    if (Data.TextureTarget != ETextureTarget.TextureBuffer)
                        throw new InvalidOperationException($"Texture view target '{Data.TextureTarget}' is incompatible with buffer texture '{viewedTexture.GetType().Name}'.");

                    _image = default;
                    _view = default;
                    _sampler = default;
                    _format = texelSource.DescriptorBufferFormat;
                    _aspect = ImageAspectFlags.None;
                    _usage = 0;
                    _samples = SampleCountFlags.Count1Bit;
                    _texelBufferView = texelSource.DescriptorBufferView;
                    _texelBufferFormat = texelSource.DescriptorBufferFormat;
                    _depthOnlyView = default;
                    _stencilOnlyView = default;

                    if (_texelBufferView.Handle == 0)
                        throw new InvalidOperationException("Failed to resolve Vulkan texel buffer view handle.");
                    return;
                }

                if (apiObject is not IVkImageDescriptorSource source)
                    throw new InvalidOperationException($"Viewed texture '{viewedTexture.GetType().Name}' is not backed by a Vulkan image.");

                _image = source.DescriptorImage;
                _format = source.DescriptorFormat;
                _usage = source.DescriptorUsage;
                _aspect = NormalizeAspectMaskForFormat(_format, source.DescriptorAspect);
                _samples = source.DescriptorSamples;
                _texelBufferView = default;
                _texelBufferFormat = Format.Undefined;
                _depthOnlyView = default;
                _stencilOnlyView = default;

                if (_image.Handle == 0)
                    throw new InvalidOperationException($"Viewed texture '{viewedTexture.GetDescribingName()}' has no Vulkan image handle.");

                ImageViewType viewType = ResolveViewType(Data.TextureTarget);
                ImageSubresourceRange subresourceRange = new()
                {
                    AspectMask = NormalizeAspectMaskForFormat(_format, _aspect),
                    BaseMipLevel = Data.MinLevel,
                    LevelCount = Math.Max(Data.NumLevels, 1u),
                    BaseArrayLayer = Data.MinLayer,
                    LayerCount = Math.Max(Data.NumLayers, 1u),
                };

                ImageViewCreateInfo viewInfo = new()
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = _image,
                    ViewType = viewType,
                    Format = _format,
                    SubresourceRange = subresourceRange,
                };

                if (Api!.CreateImageView(Device, ref viewInfo, null, out _view) != Result.Success)
                    throw new InvalidOperationException("Failed to create Vulkan texture view.");

                // For depth/stencil formats with both aspects, create a depth-only view for
                // sampled descriptors (Vulkan requires exactly one aspect in that case).
                bool hasStencil = _format is Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint;
                if (hasStencil && (_usage & ImageUsageFlags.SampledBit) != 0)
                {
                    ImageViewCreateInfo depthOnlyViewInfo = viewInfo with
                    {
                        SubresourceRange = viewInfo.SubresourceRange with
                        {
                            AspectMask = ImageAspectFlags.DepthBit,
                        },
                    };
                    if (Api!.CreateImageView(Device, ref depthOnlyViewInfo, null, out _depthOnlyView) != Result.Success)
                        throw new InvalidOperationException("Failed to create depth-only descriptor view for texture view.");
                }

                CreateSampler();
            }

            private void RefreshFromViewedTextureIfStale()
            {
                if (_texelBufferView.Handle != 0)
                    return;

                XRTexture? viewedTexture = Data.GetViewedTexture();
                if (viewedTexture is null)
                    return;

                AbstractRenderAPIObject? apiObject = Renderer.GetOrCreateAPIRenderObject(viewedTexture, generateNow: true);
                if (apiObject is not IVkImageDescriptorSource source)
                    return;

                Image liveImage = source.DescriptorImage;
                if (liveImage.Handle == 0)
                    return;

                if (liveImage.Handle == _image.Handle && _view.Handle != 0)
                {
                    if (_sampler.Handle == 0)
                        CreateSampler();
                    return;
                }

                if (_view.Handle != 0)
                {
                    Api!.DestroyImageView(Device, _view, null);
                    _view = default;
                }

                if (_depthOnlyView.Handle != 0)
                {
                    Api!.DestroyImageView(Device, _depthOnlyView, null);
                    _depthOnlyView = default;
                }

                if (_stencilOnlyView.Handle != 0)
                {
                    Api!.DestroyImageView(Device, _stencilOnlyView, null);
                    _stencilOnlyView = default;
                }

                DestroySampler();

                _image = liveImage;
                _format = source.DescriptorFormat;
                _usage = source.DescriptorUsage;
                _aspect = NormalizeAspectMaskForFormat(_format, source.DescriptorAspect);
                _samples = source.DescriptorSamples;

                ImageViewType viewType = ResolveViewType(Data.TextureTarget);
                ImageSubresourceRange subresourceRange = new()
                {
                    AspectMask = NormalizeAspectMaskForFormat(_format, _aspect),
                    BaseMipLevel = Data.MinLevel,
                    LevelCount = Math.Max(Data.NumLevels, 1u),
                    BaseArrayLayer = Data.MinLayer,
                    LayerCount = Math.Max(Data.NumLayers, 1u),
                };

                ImageViewCreateInfo viewInfo = new()
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = _image,
                    ViewType = viewType,
                    Format = _format,
                    SubresourceRange = subresourceRange,
                };

                if (Api!.CreateImageView(Device, ref viewInfo, null, out _view) != Result.Success)
                    _view = default;

                if (_view.Handle != 0)
                    CreateSampler();
            }

            private void RecreateSampler()
            {
                DestroySampler();
                if (_image.Handle != 0 && _texelBufferView.Handle == 0)
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
                    AddressModeW = SamplerConversions.FromWrap(Data.VWrap),
                    MipLodBias = Data.LodBias,
                    MinLod = 0f,
                    MaxLod = Math.Max(0f, Math.Max(Data.NumLevels, 1u) - 1u),
                    BorderColor = BorderColor.IntOpaqueBlack,
                    AnisotropyEnable = Vk.False,
                    MaxAnisotropy = 1f,
                    CompareEnable = Vk.False,
                    CompareOp = CompareOp.Always,
                    UnnormalizedCoordinates = Vk.False,
                };

                if (Renderer.SamplerAnisotropyEnabled && Data.NumLevels > 1)
                {
                    Api!.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties props);
                    if (props.Limits.MaxSamplerAnisotropy > 1f)
                    {
                        samplerInfo.AnisotropyEnable = Vk.True;
                        samplerInfo.MaxAnisotropy = MathF.Min(props.Limits.MaxSamplerAnisotropy, 16f);
                    }
                }

                if (Api!.CreateSampler(Device, ref samplerInfo, null, out _sampler) != Result.Success)
                    throw new Exception("Failed to create Vulkan texture-view sampler.");
            }

            private void DestroySampler()
            {
                if (_sampler.Handle == 0)
                    return;

                Api!.DestroySampler(Device, _sampler, null);
                _sampler = default;
            }

            private static bool IsCombinedDepthStencilFormat(Format format)
                => format is Format.D24UnormS8Uint
                    or Format.D32SfloatS8Uint
                    or Format.D16UnormS8Uint;

            private static ImageAspectFlags NormalizeAspectMaskForFormat(Format format, ImageAspectFlags requested)
            {
                bool isDepthStencil = format is Format.D16Unorm or Format.X8D24UnormPack32 or Format.D32Sfloat or Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint;
                if (!isDepthStencil)
                {
                    ImageAspectFlags colorMask = requested & ImageAspectFlags.ColorBit;
                    return colorMask != ImageAspectFlags.None ? colorMask : ImageAspectFlags.ColorBit;
                }

                bool hasStencil = format is Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint;
                ImageAspectFlags supported = hasStencil
                    ? (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)
                    : ImageAspectFlags.DepthBit;

                ImageAspectFlags normalized = requested & supported;
                if (normalized == ImageAspectFlags.None)
                    normalized = supported;

                if ((normalized & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) == ImageAspectFlags.None)
                    normalized = hasStencil ? ImageAspectFlags.DepthBit : supported;

                return normalized;
            }

            private static ImageViewType ResolveViewType(ETextureTarget target)
                => target switch
                {
                    ETextureTarget.Texture1D => ImageViewType.Type1D,
                    ETextureTarget.Texture1DArray => ImageViewType.Type1DArray,
                    ETextureTarget.Texture2D => ImageViewType.Type2D,
                    ETextureTarget.Texture2DArray => ImageViewType.Type2DArray,
                    ETextureTarget.Texture2DMultisample => ImageViewType.Type2D,
                    ETextureTarget.Texture2DMultisampleArray => ImageViewType.Type2DArray,
                    ETextureTarget.Texture3D => ImageViewType.Type3D,
                    ETextureTarget.TextureRectangle => ImageViewType.Type2D,
                    ETextureTarget.TextureCubeMap => ImageViewType.TypeCube,
                    ETextureTarget.TextureCubeMapArray => ImageViewType.TypeCubeArray,
                    _ => ImageViewType.Type2D,
                };
        }
    }
}
