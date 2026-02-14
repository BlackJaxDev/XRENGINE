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

            Image IVkImageDescriptorSource.DescriptorImage => _image;
            ImageView IVkImageDescriptorSource.DescriptorView => _view;
            Sampler IVkImageDescriptorSource.DescriptorSampler => _sampler;
            Format IVkImageDescriptorSource.DescriptorFormat => _format;
            ImageAspectFlags IVkImageDescriptorSource.DescriptorAspect => _aspect;
            ImageUsageFlags IVkImageDescriptorSource.DescriptorUsage => _usage;
            SampleCountFlags IVkImageDescriptorSource.DescriptorSamples => _samples;
            BufferView IVkTexelBufferDescriptorSource.DescriptorBufferView => _texelBufferView;
            Format IVkTexelBufferDescriptorSource.DescriptorBufferFormat => _texelBufferFormat;

            protected override uint CreateObjectInternal()
            {
                CreateView();
                return CacheObject(this);
            }

            protected override void DeleteObjectInternal()
            {
                if (_view.Handle != 0)
                {
                    Api!.DestroyImageView(Device, _view, null);
                    _view = default;
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
                => _view;

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
                }
            }

            private void CreateView()
            {
                XRTexture viewedTexture = Data.GetViewedTexture();
                if (viewedTexture is null)
                    throw new InvalidOperationException("Texture view requires a valid viewed texture.");

                AbstractRenderAPIObject apiObject = Renderer.GetOrCreateAPIRenderObject(viewedTexture, generateNow: true);
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

                    if (_texelBufferView.Handle == 0)
                        throw new InvalidOperationException("Failed to resolve Vulkan texel buffer view handle.");
                    return;
                }

                if (apiObject is not IVkImageDescriptorSource source)
                    throw new InvalidOperationException($"Viewed texture '{viewedTexture.GetType().Name}' is not backed by a Vulkan image.");

                _image = source.DescriptorImage;
                _sampler = source.DescriptorSampler;
                _format = source.DescriptorFormat;
                _usage = source.DescriptorUsage;
                _aspect = NormalizeAspectMaskForFormat(_format, source.DescriptorAspect);
                _samples = source.DescriptorSamples;
                _texelBufferView = default;
                _texelBufferFormat = Format.Undefined;

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
            }

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
