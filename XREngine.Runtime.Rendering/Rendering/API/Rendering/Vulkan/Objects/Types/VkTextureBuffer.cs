using Silk.NET.Vulkan;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Vulkan wrapper for a texel-buffer texture (<see cref="XRTextureBuffer"/>).
    /// Unlike image-backed textures, this creates a <see cref="BufferView"/> over an
    /// <see cref="XRDataBuffer"/> so it can be sampled as a uniform texel buffer in shaders.
    /// Implements <see cref="IVkTexelBufferDescriptorSource"/> for descriptor-set binding.
    /// </summary>
    internal sealed class VkTextureBuffer(VulkanRenderer api, XRTextureBuffer data) : VkTexture<XRTextureBuffer>(api, data), IVkTexelBufferDescriptorSource
    {
        private BufferView _view;
        private Format _format = Format.R8G8B8A8Unorm;

        /// <summary>The Vulkan buffer view handle.</summary>
        internal BufferView View => _view;
        /// <summary>The <see cref="Format"/> used when creating the buffer view.</summary>
        internal Format BufferFormat => _format;
        BufferView IVkTexelBufferDescriptorSource.DescriptorBufferView => _view;
        Format IVkTexelBufferDescriptorSource.DescriptorBufferFormat => _format;

        public override VkObjectType Type => VkObjectType.BufferView;
        public override bool IsGenerated => _view.Handle != 0;

        /// <summary>
        /// Creates the buffer view via <see cref="CreateBufferView"/> and caches it in the
        /// renderer's object store.
        /// </summary>
        protected override uint CreateObjectInternal()
        {
            CreateBufferView();
            return CacheObject(this);
        }

        /// <summary>
        /// Destroys the buffer view if it has been created.
        /// </summary>
        protected override void DeleteObjectInternal()
        {
            if (_view.Handle != 0)
            {
                Api!.DestroyBufferView(Device, _view, null);
                _view = default;
            }
        }

        /// <summary>
        /// Subscribes to <see cref="XRTextureBuffer.PropertyChanged"/> so the buffer view
        /// is recreated when the underlying data buffer, format, or texel count changes.
        /// </summary>
        protected override void LinkData()
            => Data.PropertyChanged += OnTextureBufferPropertyChanged;

        /// <summary>
        /// Unsubscribes from property-change notifications.
        /// </summary>
        protected override void UnlinkData()
            => Data.PropertyChanged -= OnTextureBufferPropertyChanged;

        /// <summary>
        /// Handles property changes on the engine texture buffer. When the data buffer,
        /// sized internal format, or texel count changes, the Vulkan buffer view is
        /// destroyed and regenerated.
        /// </summary>
        private void OnTextureBufferPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(XRTextureBuffer.DataBuffer):
                case nameof(XRTextureBuffer.SizedInternalFormat):
                case nameof(XRTextureBuffer.TexelCount):
                    if (IsActive)
                    {
                        Destroy();
                        Generate();
                    }
                    break;
            }
        }

        /// <summary>
        /// Allocates a <see cref="BufferView"/> over the Vulkan buffer backing
        /// <c>Data.DataBuffer</c>. Resolves the format from the engine's
        /// <see cref="ESizedInternalFormat"/> and computes the view range in bytes.
        /// </summary>
        private void CreateBufferView()
        {
            XRDataBuffer? sourceBuffer = Data.DataBuffer;
            if (sourceBuffer is null)
            {
                Debug.VulkanWarning($"Texture buffer '{Data.Name ?? "<unnamed>"}' has no source data buffer.");
                return;
            }

            if (Renderer.GetOrCreateAPIRenderObject(sourceBuffer, generateNow: true) is not VkDataBuffer vkDataBuffer)
                throw new InvalidOperationException("Texture buffer source is not backed by a Vulkan data buffer.");

            vkDataBuffer.PushData();
            Buffer? handle = vkDataBuffer.BufferHandle;
            if (handle is null || handle.Value.Handle == 0)
            {
                Debug.VulkanWarning($"Texture buffer '{Data.Name ?? "<unnamed>"}' could not resolve a Vulkan buffer handle.");
                return;
            }

            _format = VkFormatConversions.FromSizedFormat(Data.SizedInternalFormat);
            ulong bytesPerTexel = ResolveTexelSize(Data.SizedInternalFormat);
            ulong requestedRange = Data.TexelCount > 0
                ? bytesPerTexel * Data.TexelCount
                : Math.Max(1u, sourceBuffer.Length);
            ulong range = Math.Max(1ul, Math.Min(requestedRange, Math.Max(1u, sourceBuffer.Length)));

            BufferViewCreateInfo createInfo = new()
            {
                SType = StructureType.BufferViewCreateInfo,
                Buffer = handle.Value,
                Format = _format,
                Offset = 0,
                Range = range
            };

            if (Api!.CreateBufferView(Device, ref createInfo, null, out _view) != Result.Success)
                throw new Exception($"Failed to create Vulkan buffer view for texture buffer '{Data.Name ?? "<unnamed>"}'.");
        }

        /// <summary>
        /// Returns the byte size of a single texel for the given <see cref="ESizedInternalFormat"/>.
        /// Used to compute the buffer-view range from a texel count.
        /// </summary>
        private static ulong ResolveTexelSize(ESizedInternalFormat sizedFormat)
            => sizedFormat switch
            {
                ESizedInternalFormat.R8 or
                ESizedInternalFormat.R8Snorm or
                ESizedInternalFormat.R8i or
                ESizedInternalFormat.R8ui => 1,

                ESizedInternalFormat.R16 or
                ESizedInternalFormat.R16Snorm or
                ESizedInternalFormat.R16f or
                ESizedInternalFormat.R16i or
                ESizedInternalFormat.R16ui or
                ESizedInternalFormat.Rg8 or
                ESizedInternalFormat.Rg8Snorm or
                ESizedInternalFormat.Rg8i or
                ESizedInternalFormat.Rg8ui => 2,

                ESizedInternalFormat.R32f or
                ESizedInternalFormat.R32i or
                ESizedInternalFormat.R32ui or
                ESizedInternalFormat.Rg16 or
                ESizedInternalFormat.Rg16Snorm or
                ESizedInternalFormat.Rg16f or
                ESizedInternalFormat.Rg16i or
                ESizedInternalFormat.Rg16ui or
                ESizedInternalFormat.Rgba8 or
                ESizedInternalFormat.Rgba8Snorm or
                ESizedInternalFormat.Rgba8i or
                ESizedInternalFormat.Rgba8ui => 4,

                ESizedInternalFormat.Rgb32f or
                ESizedInternalFormat.Rgb32i or
                ESizedInternalFormat.Rgb32ui => 12,

                ESizedInternalFormat.Rgba32f or
                ESizedInternalFormat.Rgba32i or
                ESizedInternalFormat.Rgba32ui => 16,

                _ => 4
            };
    }
}
