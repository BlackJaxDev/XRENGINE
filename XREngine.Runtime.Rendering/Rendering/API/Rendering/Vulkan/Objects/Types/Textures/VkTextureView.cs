using System;
using Silk.NET.Vulkan;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        public class VkTextureView(VulkanRenderer api, XRTextureViewBase data) : VkTexture<XRTextureViewBase>(api, data), IVkFrameBufferAttachmentSource, IVkTexelBufferDescriptorSource
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
            private uint _baseMipLevel;
            private uint _mipLevels = 1u;
            private uint _baseArrayLayer;
            private uint _arrayLayers = 1u;
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
            DeviceMemory IVkImageDescriptorSource.DescriptorMemory
            {
                get
                {
                    RefreshFromViewedTextureIfStale();

                    XRTexture viewedTexture = Data.GetViewedTexture();
                    if (viewedTexture is null)
                        return default;

                    return Renderer.GetOrCreateAPIRenderObject(viewedTexture, generateNow: true) is IVkImageDescriptorSource source
                        ? source.DescriptorMemory
                        : default;
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
            uint IVkImageDescriptorSource.DescriptorMipLevels
            {
                get
                {
                    RefreshFromViewedTextureIfStale();
                    return _mipLevels;
                }
            }
            uint IVkImageDescriptorSource.DescriptorArrayLayers
            {
                get
                {
                    RefreshFromViewedTextureIfStale();
                    return _arrayLayers;
                }
            }
            ImageLayout IVkImageDescriptorSource.TrackedImageLayout
            {
                get
                {
                    RefreshFromViewedTextureIfStale();

                    if (TryGetViewedAttachmentSource(out IVkFrameBufferAttachmentSource? attachmentSource))
                    {
                        ImageLayout? common = null;
                        uint mipCount = Math.Max(_mipLevels, 1u);
                        uint layerCount = Math.Max(_arrayLayers, 1u);
                        for (uint mip = 0; mip < mipCount; mip++)
                        {
                            int sourceMip = checked((int)(_baseMipLevel + mip));
                            for (uint layer = 0; layer < layerCount; layer++)
                            {
                                ImageLayout layout = attachmentSource.GetAttachmentTrackedLayout(
                                    sourceMip,
                                    checked((int)(_baseArrayLayer + layer)));
                                if (layout == ImageLayout.Undefined)
                                    return ImageLayout.Undefined;

                                if (common.HasValue && common.Value != layout)
                                    return ImageLayout.Undefined;

                                common = layout;
                            }
                        }

                        return common ?? ImageLayout.Undefined;
                    }

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

                ImageSubresourceRange subresourceRange = CurrentViewSubresourceRange(aspect);

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
                RetireOwnedViewsAndSampler();

                _image = default;
                _sampler = default;
                _format = Format.R8G8B8A8Unorm;
                _aspect = ImageAspectFlags.ColorBit;
                _usage = ImageUsageFlags.SampledBit;
                _samples = SampleCountFlags.Count1Bit;
                _baseMipLevel = 0u;
                _mipLevels = 1u;
                _baseArrayLayer = 0u;
                _arrayLayers = 1u;
                _texelBufferView = default;
                _texelBufferFormat = Format.Undefined;

                InvalidateTextureData();
            }

            protected override void LinkTextureData()
            {
                Data.ViewedTextureChanged += OnViewedTextureChanged;
                Data.PropertyChanged += OnTextureViewPropertyChanged;
                SubscribeViewedTextureEvents(Data.GetViewedTexture());
            }

            protected override void UnlinkTextureData()
            {
                UnsubscribeViewedTextureEvents(Data.GetViewedTexture());
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
                if (!TryGetViewedAttachmentSource(out IVkFrameBufferAttachmentSource? source))
                    return;

                uint mipCount = Math.Max(_mipLevels, 1u);
                uint layerCount = Math.Max(_arrayLayers, 1u);
                for (uint mip = 0; mip < mipCount; mip++)
                {
                    int sourceMip = checked((int)(_baseMipLevel + mip));
                    for (uint layer = 0; layer < layerCount; layer++)
                    {
                        source.UpdateAttachmentTrackedLayout(
                            layout,
                            sourceMip,
                            checked((int)(_baseArrayLayer + layer)));
                    }
                }
            }

            ImageLayout IVkFrameBufferAttachmentSource.GetAttachmentTrackedLayout(int mipLevel, int layerIndex)
            {
                RefreshFromViewedTextureIfStale();

                if (!TryGetViewedAttachmentSource(out IVkFrameBufferAttachmentSource? source))
                    return ImageLayout.Undefined;

                int sourceMip = checked((int)(_baseMipLevel + (uint)Math.Max(mipLevel, 0)));
                if (layerIndex >= 0)
                    return source.GetAttachmentTrackedLayout(sourceMip, checked((int)(_baseArrayLayer + (uint)layerIndex)));

                ImageLayout? common = null;
                uint layers = Math.Max(_arrayLayers, 1u);
                for (uint layer = 0; layer < layers; layer++)
                {
                    ImageLayout layout = source.GetAttachmentTrackedLayout(sourceMip, checked((int)(_baseArrayLayer + layer)));
                    if (layout == ImageLayout.Undefined)
                        return ImageLayout.Undefined;

                    if (common.HasValue && common.Value != layout)
                        return ImageLayout.Undefined;

                    common = layout;
                }

                return common ?? ImageLayout.Undefined;
            }

            void IVkFrameBufferAttachmentSource.UpdateAttachmentTrackedLayout(ImageLayout layout, int mipLevel, int layerIndex)
            {
                RefreshFromViewedTextureIfStale();

                if (!TryGetViewedAttachmentSource(out IVkFrameBufferAttachmentSource? source))
                    return;

                int sourceMip = checked((int)(_baseMipLevel + (uint)Math.Max(mipLevel, 0)));
                if (layerIndex >= 0)
                {
                    source.UpdateAttachmentTrackedLayout(layout, sourceMip, checked((int)(_baseArrayLayer + (uint)layerIndex)));
                    return;
                }

                uint layers = Math.Max(_arrayLayers, 1u);
                for (uint layer = 0; layer < layers; layer++)
                    source.UpdateAttachmentTrackedLayout(layout, sourceMip, checked((int)(_baseArrayLayer + layer)));
            }

            bool IVkImageDescriptorSource.TryTransitionDedicatedImageLayout(ImageLayout oldLayout, ImageLayout newLayout)
            {
                XRTexture viewedTexture = Data.GetViewedTexture();
                if (viewedTexture is null)
                    return false;

                return Renderer.GetOrCreateAPIRenderObject(viewedTexture, generateNow: true) is IVkImageDescriptorSource source &&
                    source.TryTransitionDedicatedImageLayout(oldLayout, newLayout);
            }

            private bool TryGetViewedAttachmentSource([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IVkFrameBufferAttachmentSource? source)
            {
                XRTexture viewedTexture = Data.GetViewedTexture();
                if (viewedTexture is not null &&
                    Renderer.GetOrCreateAPIRenderObject(viewedTexture, generateNow: true) is IVkFrameBufferAttachmentSource attachmentSource)
                {
                    source = attachmentSource;
                    return true;
                }

                source = null;
                return false;
            }

            private void OnViewedTextureChanged()
            {
                UnsubscribeViewedTextureEvents(_subscribedViewedTexture);
                SubscribeViewedTextureEvents(Data.GetViewedTexture());
                InvalidateTextureData();
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
                        InvalidateTextureData();
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
                    case nameof(XRTexture.MinLOD):
                    case nameof(XRTexture.MaxLOD):
                    case nameof(XRTexture.LargestMipmapLevel):
                    case nameof(XRTexture.SmallestAllowedMipmapLevel):
                        MarkDescriptorDirty();
                        if (IsActive && _texelBufferView.Handle == 0)
                            RecreateSampler();
                        break;
                }
            }

            public override void PushData()
            {
                XRTexture viewedTexture = Data.GetViewedTexture();
                viewedTexture?.PushData();
                Generate();
                if (IsGenerated)
                    MarkUploaded();
            }

            public override void Bind()
            {
                PushData();
                if (!IsGenerated)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.TextureView.BindNotReady.{Data.GetHashCode()}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Texture view BindRequested could not create a descriptor view for '{0}'.",
                        Data.Name ?? Data.GetDescribingName());
                    return;
                }

                MarkDescriptorClean();
            }

            private XRTexture? _subscribedViewedTexture;

            private void SubscribeViewedTextureEvents(XRTexture? texture)
            {
                if (texture is null || ReferenceEquals(texture, _subscribedViewedTexture))
                    return;

                _subscribedViewedTexture = texture;
                SubscribeViewedTextureResize(texture);
                texture.PropertyChanged += OnViewedTexturePropertyChanged;
            }

            private void UnsubscribeViewedTextureEvents(XRTexture? texture)
            {
                texture ??= _subscribedViewedTexture;
                if (texture is null)
                    return;

                UnsubscribeViewedTextureResize(texture);
                texture.PropertyChanged -= OnViewedTexturePropertyChanged;
                if (ReferenceEquals(texture, _subscribedViewedTexture))
                    _subscribedViewedTexture = null;
            }

            private void SubscribeViewedTextureResize(XRTexture texture)
            {
                switch (texture)
                {
                    case XRTexture1D tex1D:
                        tex1D.Resized += OnViewedTextureResized;
                        break;
                    case XRTexture1DArray tex1DArray:
                        tex1DArray.Resized += OnViewedTextureResized;
                        break;
                    case XRTexture2D tex2D:
                        tex2D.Resized += OnViewedTextureResized;
                        break;
                    case XRTexture2DArray tex2DArray:
                        tex2DArray.Resized += OnViewedTextureResized;
                        break;
                    case XRTexture3D tex3D:
                        tex3D.Resized += OnViewedTextureResized;
                        break;
                    case XRTextureCube texCube:
                        texCube.Resized += OnViewedTextureResized;
                        break;
                    case XRTextureCubeArray texCubeArray:
                        texCubeArray.Resized += OnViewedTextureResized;
                        break;
                    case XRTextureRectangle rectangle:
                        rectangle.Resized += OnViewedTextureResized;
                        break;
                }
            }

            private void UnsubscribeViewedTextureResize(XRTexture texture)
            {
                switch (texture)
                {
                    case XRTexture1D tex1D:
                        tex1D.Resized -= OnViewedTextureResized;
                        break;
                    case XRTexture1DArray tex1DArray:
                        tex1DArray.Resized -= OnViewedTextureResized;
                        break;
                    case XRTexture2D tex2D:
                        tex2D.Resized -= OnViewedTextureResized;
                        break;
                    case XRTexture2DArray tex2DArray:
                        tex2DArray.Resized -= OnViewedTextureResized;
                        break;
                    case XRTexture3D tex3D:
                        tex3D.Resized -= OnViewedTextureResized;
                        break;
                    case XRTextureCube texCube:
                        texCube.Resized -= OnViewedTextureResized;
                        break;
                    case XRTextureCubeArray texCubeArray:
                        texCubeArray.Resized -= OnViewedTextureResized;
                        break;
                    case XRTextureRectangle rectangle:
                        rectangle.Resized -= OnViewedTextureResized;
                        break;
                }
            }

            private void OnViewedTextureResized()
            {
                InvalidateTextureData();
                if (IsActive)
                {
                    Destroy();
                    Generate();
                }
            }

            private void OnViewedTexturePropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                if (IsViewedTextureStorageProperty(e.PropertyName))
                {
                    InvalidateTextureData();
                    if (IsActive)
                    {
                        Destroy();
                        Generate();
                    }
                }
            }

            private static bool IsViewedTextureStorageProperty(string? propertyName)
                => propertyName is nameof(XRTexture.MinLOD)
                    or nameof(XRTexture.MaxLOD)
                    or nameof(XRTexture.LargestMipmapLevel)
                    or nameof(XRTexture.SmallestAllowedMipmapLevel)
                    or nameof(XRTexture2D.SizedInternalFormat)
                    or nameof(XRTexture2D.Mipmaps)
                    or nameof(XRTexture2DArray.Textures)
                    or nameof(XRTextureCubeArray.Cubes);

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
                    _baseMipLevel = 0u;
                    _mipLevels = 1u;
                    _baseArrayLayer = 0u;
                    _arrayLayers = 1u;
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
                ImageSubresourceRange subresourceRange = ResolveViewSubresourceRange(source, NormalizeAspectMaskForFormat(_format, _aspect));

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

            internal void RefreshDescriptorFromViewedTextureIfStale()
                => RefreshFromViewedTextureIfStale();

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

                RetireOwnedViewsAndSampler();

                _image = liveImage;
                _format = source.DescriptorFormat;
                _usage = source.DescriptorUsage;
                _aspect = NormalizeAspectMaskForFormat(_format, source.DescriptorAspect);
                _samples = source.DescriptorSamples;

                ImageViewType viewType = ResolveViewType(Data.TextureTarget);
                ImageSubresourceRange subresourceRange = ResolveViewSubresourceRange(source, NormalizeAspectMaskForFormat(_format, _aspect));

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

                bool hasStencil = _format is Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint;
                if (_view.Handle != 0 && hasStencil && (_usage & ImageUsageFlags.SampledBit) != 0)
                {
                    ImageViewCreateInfo depthOnlyViewInfo = viewInfo with
                    {
                        SubresourceRange = viewInfo.SubresourceRange with
                        {
                            AspectMask = ImageAspectFlags.DepthBit,
                        },
                    };
                    if (Api!.CreateImageView(Device, ref depthOnlyViewInfo, null, out _depthOnlyView) != Result.Success)
                        _depthOnlyView = default;
                }

                if (_view.Handle != 0)
                    CreateSampler();

                MarkDescriptorDirty();
            }

            private ImageSubresourceRange ResolveViewSubresourceRange(IVkImageDescriptorSource source, ImageAspectFlags aspectMask)
            {
                uint backingMipLevels = Math.Max(source.DescriptorMipLevels, 1u);
                uint backingLayers = Math.Max(source.DescriptorArrayLayers, 1u);

                _baseMipLevel = Math.Min(Data.MinLevel, backingMipLevels - 1u);
                uint requestedLevels = Math.Max(Data.NumLevels, 1u);
                _mipLevels = Math.Min(requestedLevels, backingMipLevels - _baseMipLevel);

                _baseArrayLayer = Math.Min(Data.MinLayer, backingLayers - 1u);
                uint requestedLayers = Math.Max(Data.NumLayers, 1u);
                _arrayLayers = Math.Min(requestedLayers, backingLayers - _baseArrayLayer);

                if (_baseMipLevel != Data.MinLevel ||
                    _mipLevels != requestedLevels ||
                    _baseArrayLayer != Data.MinLayer ||
                    _arrayLayers != requestedLayers)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.TextureView.ClampedSubresource.{Data.GetHashCode()}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Texture view '{0}' subresource range clamped to backing image. requested mip={1}+{2} layer={3}+{4}; backing mips={5} layers={6}; using mip={7}+{8} layer={9}+{10}.",
                        Data.Name ?? Data.GetDescribingName(),
                        Data.MinLevel,
                        requestedLevels,
                        Data.MinLayer,
                        requestedLayers,
                        backingMipLevels,
                        backingLayers,
                        _baseMipLevel,
                        _mipLevels,
                        _baseArrayLayer,
                        _arrayLayers);
                }

                return CurrentViewSubresourceRange(aspectMask);
            }

            private ImageSubresourceRange CurrentViewSubresourceRange(ImageAspectFlags aspectMask)
                => new()
                {
                    AspectMask = aspectMask,
                    BaseMipLevel = _baseMipLevel,
                    LevelCount = Math.Max(_mipLevels, 1u),
                    BaseArrayLayer = _baseArrayLayer,
                    LayerCount = Math.Max(_arrayLayers, 1u),
                };

            private void RecreateSampler()
            {
                DestroySampler();
                if (_image.Handle != 0 && _texelBufferView.Handle == 0)
                    CreateSampler();
            }

            private void RetireOwnedViewsAndSampler()
            {
                if (_view.Handle == 0 &&
                    _depthOnlyView.Handle == 0 &&
                    _stencilOnlyView.Handle == 0 &&
                    _sampler.Handle == 0)
                {
                    return;
                }

                int attachmentCount = 0;
                if (_depthOnlyView.Handle != 0)
                    attachmentCount++;
                if (_stencilOnlyView.Handle != 0)
                    attachmentCount++;

                ImageView[] attachmentViews = attachmentCount == 0 ? [] : new ImageView[attachmentCount];
                int index = 0;
                if (_depthOnlyView.Handle != 0)
                    attachmentViews[index++] = _depthOnlyView;
                if (_stencilOnlyView.Handle != 0)
                    attachmentViews[index] = _stencilOnlyView;

                Renderer.RetireImageResources(new RetiredImageResources(
                    default,
                    default,
                    _view,
                    attachmentViews,
                    _sampler,
                    0));

                _view = default;
                _depthOnlyView = default;
                _stencilOnlyView = default;
                _sampler = default;
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

                Renderer.RegisterLiveSampler(_sampler);
            }

            private void DestroySampler()
            {
                if (_sampler.Handle == 0)
                    return;

                Renderer.RetireSampler(_sampler);
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
