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
        #region Event Handlers

        protected override void DataPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
            base.DataPropertyChanging(sender, e);

            if (e.PropertyName is nameof(XRTexture1DArray.Textures)
                or nameof(XRTexture2DArray.Textures)
                or nameof(XRTextureCubeArray.Cubes))
            {
                UnsubscribeChildTextureEvents(e.CurrentValue);
            }
        }

        protected override void DataPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.DataPropertyChanged(sender, e);

            if (IsSamplerDataProperty(e.PropertyName))
                RecreateSamplerForPropertyChange();

            if (IsStorageDataProperty(e.PropertyName))
                RecreateImageForPropertyChange();

            if (e.PropertyName is nameof(XRTexture1DArray.Textures)
                or nameof(XRTexture2DArray.Textures)
                or nameof(XRTextureCubeArray.Cubes))
            {
                SubscribeChildTextureEvents(e.NewValue);
            }
        }

        /// <summary>
        /// Uploads invalidated texture data on the render thread.
        /// </summary>
        public override void PushData()
        {
            if (RuntimeEngine.InvokeOnMainThread(PushData, "VkTexture.PushData"))
                return;

            if (Renderer.IsDeviceLost)
                return;

            if (Data is XRTexture2D { RuntimeManagedProgressiveUploadActive: true })
                return;

            if (!TryBeginPushData(out bool allowPostPushCallback))
                return;

            PushTextureData();
            if (IsGenerated)
                MarkUploaded();

            CompletePushData(allowPostPushCallback);
        }

        /// <summary>
        /// Generates mipmaps on the render thread.
        /// </summary>
        public override void GenerateMipmaps()
        {
            if (RuntimeEngine.InvokeOnMainThread(GenerateMipmaps, "VkTexture.GenerateMipmaps"))
                return;

            if (Renderer.IsDeviceLost)
                return;

            GenerateMipmapsGPU();
            if (IsGenerated)
                MarkUploaded();
        }

        public override void Bind()
        {
            EnsureDescriptorReadyForVulkanUse("BindRequested");
            if (IsGenerated && _view.Handle != 0 && (!CreateSampler || _sampler.Handle != 0))
                MarkDescriptorClean();
        }

        public override void Clear(ColorF4 color, int level = 0)
        {
            if (RuntimeEngine.InvokeOnMainThread(() => Clear(color, level), "VkTexture.Clear"))
                return;

            Generate();
            if (!IsGenerated || _image.Handle == 0)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.ClearNotGenerated.{Data.GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] ClearRequested could not generate image-backed texture '{0}' level={1}.",
                    Data.Name ?? Data.GetDescribingName(),
                    level);
                return;
            }

            uint baseMip = (uint)Math.Clamp(level, 0, Math.Max((int)ResolvedMipLevels - 1, 0));
            ImageLayout previousLayout = CurrentImageLayout;
            if (previousLayout != ImageLayout.TransferDstOptimal)
                TransitionImageLayout(previousLayout, ImageLayout.TransferDstOptimal);

            ImageSubresourceRange range = new()
            {
                AspectMask = AspectFlags,
                BaseMipLevel = baseMip,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = ResolvedArrayLayers,
            };

            using var scope = Renderer.NewCommandScope();
            if ((AspectFlags & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0)
            {
                ClearDepthStencilValue clearDepthStencil = new()
                {
                    Depth = color.R,
                    Stencil = (uint)Math.Clamp((int)color.G, 0, 255),
                };
                Renderer.CmdClearDepthStencilImageTracked(scope.CommandBuffer, _image, ImageLayout.TransferDstOptimal, ref clearDepthStencil, 1, ref range);
            }
            else
            {
                ClearColorValue clearColor = new()
                {
                    Float32_0 = color.R,
                    Float32_1 = color.G,
                    Float32_2 = color.B,
                    Float32_3 = color.A,
                };
                Renderer.CmdClearColorImageTracked(scope.CommandBuffer, _image, ImageLayout.TransferDstOptimal, ref clearColor, 1, ref range);
            }

            ImageLayout targetLayout = previousLayout == ImageLayout.Undefined
                ? ImageLayout.ShaderReadOnlyOptimal
                : previousLayout;
            if (targetLayout != ImageLayout.TransferDstOptimal)
                TransitionImageLayout(ImageLayout.TransferDstOptimal, targetLayout);

            MarkUploaded();
        }

        private void RecreateSamplerForPropertyChange()
        {
            MarkDescriptorDirty();
            if (!IsActive || !CreateSampler)
                return;

            DestroySampler();
            if (_image.Handle != 0)
                CreateSamplerInternal();
        }

        private void RecreateImageForPropertyChange()
        {
            InvalidateTextureData();
            _layoutInitialized = false;
            if (!IsActive)
                return;

            // DeleteObjectInternal publishes every owned image/view/sampler handle to the
            // renderer's frame-slot/timeline retirement queues.  Recreating a dedicated
            // imported texture therefore does not require a device-wide drain: the new
            // generation can be published immediately while the old generation remains
            // alive until its exact last-use ticket completes.
            Destroy();
            Generate();
        }

        private static bool IsSamplerDataProperty(string? propertyName)
            => propertyName is null
                or ""
                or nameof(XRTexture.MinLOD)
                or nameof(XRTexture.MaxLOD)
                or nameof(XRTexture.LargestMipmapLevel)
                or nameof(XRTexture.SmallestAllowedMipmapLevel)
                or nameof(XRTexture1D.MinFilter)
                or nameof(XRTexture1D.MagFilter)
                or nameof(XRTexture1D.UWrap)
                or nameof(XRTexture1D.LodBias)
                or nameof(XRTexture2D.VWrap)
                or nameof(XRTexture3D.WWrap)
                or nameof(XRTexture2D.EnableComparison)
                or nameof(XRTexture2D.CompareFunc);

        private static bool IsStorageDataProperty(string? propertyName)
            => propertyName is null
                or ""
                or nameof(XRTexture.RequiresStorageUsage)
                or nameof(XRTexture.FrameBufferAttachment)
                or nameof(XRTexture1D.Mipmaps)
                or nameof(XRTexture1D.SizedInternalFormat)
                or nameof(XRTexture1DArray.Textures)
                or nameof(XRTexture2DArray.Textures)
                or nameof(XRTextureCubeArray.Cubes)
                or nameof(XRTexture2D.MultiSampleCount)
                or nameof(XRTexture2D.FixedSampleLocations)
                or nameof(XRTextureRectangle.Width)
                or nameof(XRTextureRectangle.Height)
                or nameof(XRTextureRectangle.Data)
                or nameof(XRTextureRectangle.PixelFormat)
                or nameof(XRTextureRectangle.PixelType);

        private void OnChildTextureResized()
            => OnTextureResized();

        private void OnChildTexturePropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (IsSamplerDataProperty(e.PropertyName))
                RecreateSamplerForPropertyChange();

            if (IsStorageDataProperty(e.PropertyName))
                RecreateImageForPropertyChange();
        }

        /// <summary>
        /// Subscribes to the <c>Resized</c> event on the specific engine-texture subtype so
        /// that Vulkan resources are recreated when the texture dimensions change.
        /// </summary>
        private void SubscribeResizeEvents()
        {
            switch (Data)
            {
                case XRTexture1D tex1D:
                    tex1D.Resized += OnTextureResized;
                    break;
                case XRTexture1DArray tex1DArray:
                    tex1DArray.Resized += OnTextureResized;
                    break;
                case XRTexture2D tex2D:
                    tex2D.Resized += OnTextureResized;
                    break;
                case XRTexture2DArray texArray:
                    texArray.Resized += OnTextureResized;
                    break;
                case XRTextureCube texCube:
                    texCube.Resized += OnTextureResized;
                    break;
                case XRTextureCubeArray texCubeArray:
                    texCubeArray.Resized += OnTextureResized;
                    break;
                case XRTexture3D tex3D:
                    tex3D.Resized += OnTextureResized;
                    break;
                case XRTextureRectangle rectangle:
                    rectangle.Resized += OnTextureResized;
                    break;
            }
        }

        /// <summary>
        /// Unsubscribes from the <c>Resized</c> event on the specific engine-texture subtype.
        /// </summary>
        private void UnsubscribeResizeEvents()
        {
            switch (Data)
            {
                case XRTexture1D tex1D:
                    tex1D.Resized -= OnTextureResized;
                    break;
                case XRTexture1DArray tex1DArray:
                    tex1DArray.Resized -= OnTextureResized;
                    break;
                case XRTexture2D tex2D:
                    tex2D.Resized -= OnTextureResized;
                    break;
                case XRTexture2DArray texArray:
                    texArray.Resized -= OnTextureResized;
                    break;
                case XRTextureCube texCube:
                    texCube.Resized -= OnTextureResized;
                    break;
                case XRTextureCubeArray texCubeArray:
                    texCubeArray.Resized -= OnTextureResized;
                    break;
                case XRTexture3D tex3D:
                    tex3D.Resized -= OnTextureResized;
                    break;
                case XRTextureRectangle rectangle:
                    rectangle.Resized -= OnTextureResized;
                    break;
            }
        }

        private void SubscribeChildTextureEvents()
            => SubscribeChildTextureEvents(Data);

        private void UnsubscribeChildTextureEvents()
            => UnsubscribeChildTextureEvents(Data);

        private void SubscribeChildTextureEvents(object? value)
        {
            foreach (XRTexture texture in EnumerateChildTextures(value))
            {
                SubscribeChildResize(texture);
                texture.PropertyChanged += OnChildTexturePropertyChanged;
            }
        }

        private void UnsubscribeChildTextureEvents(object? value)
        {
            foreach (XRTexture texture in EnumerateChildTextures(value))
            {
                UnsubscribeChildResize(texture);
                texture.PropertyChanged -= OnChildTexturePropertyChanged;
            }
        }

        private static IEnumerable<XRTexture> EnumerateChildTextures(object? value)
        {
            switch (value)
            {
                case XRTexture1DArray tex1DArray:
                    return tex1DArray.Textures;
                case XRTexture2DArray tex2DArray:
                    return tex2DArray.Textures;
                case XRTextureCubeArray texCubeArray:
                    return texCubeArray.Cubes;
                case XRTexture1D[] tex1D:
                    return tex1D;
                case XRTexture2D[] tex2D:
                    return tex2D;
                case XRTextureCube[] texCube:
                    return texCube;
                default:
                    return [];
            }
        }

        private void SubscribeChildResize(XRTexture texture)
        {
            switch (texture)
            {
                case XRTexture1D tex1D:
                    tex1D.Resized += OnChildTextureResized;
                    break;
                case XRTexture2D tex2D:
                    tex2D.Resized += OnChildTextureResized;
                    break;
                case XRTextureCube texCube:
                    texCube.Resized += OnChildTextureResized;
                    break;
            }
        }

        private void UnsubscribeChildResize(XRTexture texture)
        {
            switch (texture)
            {
                case XRTexture1D tex1D:
                    tex1D.Resized -= OnChildTextureResized;
                    break;
                case XRTexture2D tex2D:
                    tex2D.Resized -= OnChildTextureResized;
                    break;
                case XRTextureCube texCube:
                    texCube.Resized -= OnChildTextureResized;
                    break;
            }
        }

        /// <summary>
        /// Called when the engine texture is resized. Destroys all Vulkan resources so they
        /// will be recreated with the new dimensions on the next <see cref="Generate"/> call.
        /// </summary>
        private void OnTextureResized()
        {
            Destroy();
            _layoutInitialized = false;
            _currentImageLayout = ImageLayout.Undefined;
            InvalidateTextureData();
        }

        #endregion
    }
}
