using Extensions;
using Silk.NET.OpenGL;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        /// <summary>
        /// OpenGL state wrapper for generic data objects.
        /// </summary>
        public abstract class GLObjectBase : AbstractRenderObject<OpenGLRenderer>, IGLObject
        {
            public const uint InvalidBindingId = 0;
            public abstract GLObjectType Type { get; }

            public override nint GetHandle() => (nint)BindingId;

            /// <summary>
            /// True if the object has been generated.
            /// Check this before using the BindingId property, as it will generate the object if it has not been generated yet.
            /// </summary>
            public override bool IsGenerated => _bindingId.HasValue && _bindingId != InvalidBindingId;

            internal uint? _bindingId;

            public virtual bool TryGetBindingId(out uint bindingId)
            {
                if (_bindingId.HasValue)
                {
                    bindingId = _bindingId.Value;
                    return bindingId != InvalidBindingId;
                }
                else
                {
                    bindingId = InvalidBindingId;
                    return false;
                }
            }

            public GLObjectBase(OpenGLRenderer renderer) : base(renderer) { }
            public GLObjectBase(OpenGLRenderer renderer, uint id) : base(renderer) => _bindingId = id;

            public override void Destroy()
            {
                //TODO: make an internal overrideable version and a public callable version of this method so we can force it to run on the main thread.
                if (Engine.IsRenderThread)
                    DeleteObject();
                else
                {
                    Engine.EnqueueMainThreadTask(Destroy);
                    return;
                }

                _invalidated = true;
                _hasSentInvalidationWarning = false;
            }

            protected internal virtual void PreGenerated()
            {
                if (IsGenerated)
                    Destroy();
            }

            protected internal virtual void PostGenerated()
            {

            }

            private bool _invalidated = true;
            private bool _hasSentInvalidationWarning = false;
            protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
            {
                base.OnPropertyChanged(propName, prev, field);
                _invalidated = true;
                _hasSentInvalidationWarning = false;
            }

            /// <summary>
            /// Generates the object on the GPU and assigns it a unique binding id.
            /// </summary>
            public override void Generate()
            {
                if (!_invalidated)
                {
                    if (!_hasSentInvalidationWarning)
                    {
                        Debug.Out($"Attempted to generate an OpenGL object with no changes since last generation attempt. Canceling to avoid infinite recursion on generation fail.");
                        _hasSentInvalidationWarning = true;
                    }
                    return;
                }

                if (!Engine.IsRenderThread)
                {
                    Debug.LogWarning("Attempted to generate OpenGL object from non-render thread. Enqueuing task to main thread, but this may break subsequent generation-dependent calls on the current thread.");
                    Engine.EnqueueMainThreadTask(Generate);
                    return;
                }

                //Debug.Out($"Generating OpenGL object {Type}");
                PreGenerated();
                _bindingId = CreateObject();
                if (_bindingId is not null && _bindingId != InvalidBindingId)
                {
                    PostGenerated();
                    _invalidated = false;
                    _hasSentInvalidationWarning = false;
                }
                else
                    Debug.Out("Failed to generate OpenGL object.");
            }

            protected internal virtual void PreDeleted() { }
            protected internal virtual void PostDeleted() { }

            /// <summary>
            /// The unique id of this object when generated.
            /// If not generated yet, the object will be generated on first access.
            /// Generation is deferred until necessary to allow for proper initialization of the object.
            /// </summary>
            public uint BindingId
            {
                get
                {
                    //try
                    //{
                    if (_bindingId is null || _bindingId.Value == InvalidBindingId)
                        Generate();

                    if (TryGetBindingId(out uint bindingId))
                        return bindingId;
                    else
                    {
                        Debug.LogWarning($"Failed to generate object of type {Type}.");
                        return InvalidBindingId;
                    }
                    //}
                    //catch (Exception ex)
                    //{
                    //    Debug.LogException(ex, $"Failed to generate object of type {Type}.");
                    //    return InvalidBindingId;
                    //}
                }
            }

            GenericRenderObject IRenderAPIObject.Data => Data_Internal;
            protected abstract GenericRenderObject Data_Internal { get; }

            public static GLEnum ToGLEnum(ETexWrapMode wrap)
                => wrap switch
                {
                    ETexWrapMode.ClampToEdge => GLEnum.ClampToEdge,
                    ETexWrapMode.ClampToBorder => GLEnum.ClampToBorder,
                    ETexWrapMode.MirroredRepeat => GLEnum.MirroredRepeat,
                    ETexWrapMode.Repeat => GLEnum.Repeat,
                    _ => throw new ArgumentOutOfRangeException(nameof(wrap), wrap, null),
                };

            public static GLEnum ToGLEnum(ETexMinFilter minFilter)
                => minFilter switch
                {
                    ETexMinFilter.Nearest => GLEnum.Nearest,
                    ETexMinFilter.Linear => GLEnum.Linear,
                    ETexMinFilter.NearestMipmapNearest => GLEnum.NearestMipmapNearest,
                    ETexMinFilter.LinearMipmapNearest => GLEnum.LinearMipmapNearest,
                    ETexMinFilter.NearestMipmapLinear => GLEnum.NearestMipmapLinear,
                    ETexMinFilter.LinearMipmapLinear => GLEnum.LinearMipmapLinear,
                    _ => throw new ArgumentOutOfRangeException(nameof(minFilter), minFilter, null),
                };

            public static GLEnum ToGLEnum(ETexMagFilter magFilter)
                => magFilter switch
                {
                    ETexMagFilter.Nearest => GLEnum.Nearest,
                    ETexMagFilter.Linear => GLEnum.Linear,
                    _ => throw new ArgumentOutOfRangeException(nameof(magFilter), magFilter, null),
                };



            public static GLEnum ToGLEnum(EPixelType pixelType)
                => pixelType switch
                {
                    EPixelType.UnsignedByte => GLEnum.UnsignedByte,
                    EPixelType.Byte => GLEnum.Byte,
                    EPixelType.UnsignedShort => GLEnum.UnsignedShort,
                    EPixelType.Short => GLEnum.Short,
                    EPixelType.UnsignedInt => GLEnum.UnsignedInt,
                    EPixelType.Int => GLEnum.Int,
                    EPixelType.HalfFloat => GLEnum.HalfFloat,
                    EPixelType.Float => GLEnum.Float,
                    EPixelType.UnsignedByte332 => GLEnum.UnsignedByte332,
                    EPixelType.UnsignedShort565 => GLEnum.UnsignedShort565,
                    EPixelType.UnsignedShort4444 => GLEnum.UnsignedShort4444,
                    EPixelType.UnsignedShort5551 => GLEnum.UnsignedShort5551,
                    EPixelType.UnsignedInt8888 => GLEnum.UnsignedInt8888,
                    EPixelType.UnsignedInt1010102 => GLEnum.UnsignedInt1010102,
                    EPixelType.UnsignedInt248 => GLEnum.UnsignedInt248,
                    EPixelType.UnsignedInt5999Rev => GLEnum.UnsignedInt5999Rev,
                    EPixelType.Float32UnsignedInt248Rev => GLEnum.Float32UnsignedInt248Rev,
                    _ => throw new ArgumentOutOfRangeException(nameof(pixelType), pixelType, null),
                };

            public static GLEnum ToGLEnum(EPixelFormat pixelFormat)
                => pixelFormat switch
                {
                    EPixelFormat.Red => GLEnum.Red,
                    EPixelFormat.Rg => GLEnum.RG,
                    EPixelFormat.Rgb => GLEnum.Rgb,
                    EPixelFormat.Bgr => GLEnum.Bgr,
                    EPixelFormat.Rgba => GLEnum.Rgba,
                    EPixelFormat.Bgra => GLEnum.Bgra,
                    EPixelFormat.RedInteger => GLEnum.RedInteger,
                    EPixelFormat.RgbInteger => GLEnum.RgbInteger,
                    EPixelFormat.BgrInteger => GLEnum.BgrInteger,
                    EPixelFormat.RgbaInteger => GLEnum.RgbaInteger,
                    EPixelFormat.BgraInteger => GLEnum.BgraInteger,
                    EPixelFormat.StencilIndex => GLEnum.StencilIndex,
                    EPixelFormat.DepthComponent => GLEnum.DepthComponent,
                    EPixelFormat.DepthStencil => GLEnum.DepthStencil,
                    EPixelFormat.Green => GLEnum.Green,
                    EPixelFormat.Blue => GLEnum.Blue,
                    EPixelFormat.Alpha => GLEnum.Alpha,
                    EPixelFormat.UnsignedShort => GLEnum.UnsignedShort,
                    EPixelFormat.UnsignedInt => GLEnum.UnsignedInt,
                    EPixelFormat.RgInteger => GLEnum.RGInteger,
                    EPixelFormat.GreenInteger => GLEnum.GreenInteger,
                    EPixelFormat.BlueInteger => GLEnum.BlueInteger,
                    _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat), pixelFormat, null),
                };

            public static GLEnum ToGLEnum(EPixelInternalFormat internalFormat)
                => internalFormat switch
                {
                    EPixelInternalFormat.R8 => GLEnum.R8,
                    EPixelInternalFormat.R8SNorm => GLEnum.R8SNorm,
                    EPixelInternalFormat.R16 => GLEnum.R16,
                    EPixelInternalFormat.R16SNorm => GLEnum.R16SNorm,
                    EPixelInternalFormat.RG8 => GLEnum.RG8,
                    EPixelInternalFormat.RG8SNorm => GLEnum.RG8SNorm,
                    EPixelInternalFormat.RG16 => GLEnum.RG16,
                    EPixelInternalFormat.RG16SNorm => GLEnum.RG16SNorm,
                    EPixelInternalFormat.R3G3B2 => GLEnum.R3G3B2,
                    EPixelInternalFormat.Rgb4 => GLEnum.Rgb4,
                    EPixelInternalFormat.Rgb5 => GLEnum.Rgb5,
                    EPixelInternalFormat.Rgb8 => GLEnum.Rgb8,
                    EPixelInternalFormat.Rgb8SNorm => GLEnum.Rgb8SNorm,
                    EPixelInternalFormat.Rgb10 => GLEnum.Rgb10,
                    EPixelInternalFormat.Rgb12 => GLEnum.Rgb12,
                    EPixelInternalFormat.Rgb16 => GLEnum.Rgb16,
                    EPixelInternalFormat.Rgb16SNorm => GLEnum.Rgb16SNorm,
                    EPixelInternalFormat.Rgba2 => GLEnum.Rgba2,
                    EPixelInternalFormat.Rgba4 => GLEnum.Rgba4,
                    EPixelInternalFormat.Rgb5A1 => GLEnum.Rgb5A1,
                    EPixelInternalFormat.Rgba8 => GLEnum.Rgba8,
                    EPixelInternalFormat.Rgba8SNorm => GLEnum.Rgba8SNorm,
                    EPixelInternalFormat.Rgb10A2 => GLEnum.Rgb10A2,
                    EPixelInternalFormat.Rgba12 => GLEnum.Rgba12,
                    EPixelInternalFormat.Rgba16 => GLEnum.Rgba16,
                    EPixelInternalFormat.Rgba16SNorm => GLEnum.Rgba16SNorm,
                    EPixelInternalFormat.Srgb8 => GLEnum.Srgb8,
                    EPixelInternalFormat.Srgb8Alpha8 => GLEnum.Srgb8Alpha8,
                    EPixelInternalFormat.R16f => GLEnum.R16f,
                    EPixelInternalFormat.RG16f => GLEnum.RG16f,
                    EPixelInternalFormat.Rgb16f => GLEnum.Rgb16f,
                    EPixelInternalFormat.Rgba16f => GLEnum.Rgba16f,
                    EPixelInternalFormat.R32f => GLEnum.R32f,
                    EPixelInternalFormat.RG32f => GLEnum.RG32f,
                    EPixelInternalFormat.Rgb32f => GLEnum.Rgb32f,
                    EPixelInternalFormat.Rgba32f => GLEnum.Rgba32f,
                    EPixelInternalFormat.R11fG11fB10f => GLEnum.R11fG11fB10f,
                    EPixelInternalFormat.Rgb9E5 => GLEnum.Rgb9E5,
                    EPixelInternalFormat.RG32i => GLEnum.RG32i,
                    EPixelInternalFormat.RG32ui => GLEnum.RG32ui,
                    EPixelInternalFormat.Rgba32i => GLEnum.Rgba32i,
                    EPixelInternalFormat.Rgba32ui => GLEnum.Rgba32ui,
                    //EPixelInternalFormat.One => GLEnum.One,
                    //EPixelInternalFormat.Alpha => GLEnum.Alpha,
                    EPixelInternalFormat.Rgb => GLEnum.Rgb,
                    EPixelInternalFormat.Rgba => GLEnum.Rgba,
                    EPixelInternalFormat.DepthComponent => GLEnum.DepthComponent,
                    EPixelInternalFormat.Depth24Stencil8 => GLEnum.Depth24Stencil8,
                    EPixelInternalFormat.Depth32fStencil8 => GLEnum.Depth32fStencil8,
                    EPixelInternalFormat.DepthComponent16 => GLEnum.DepthComponent16,
                    EPixelInternalFormat.DepthComponent24 => GLEnum.DepthComponent24,
                    EPixelInternalFormat.DepthComponent32 => GLEnum.DepthComponent32,
                    EPixelInternalFormat.DepthStencil => GLEnum.DepthStencil,
                    _ => throw new ArgumentOutOfRangeException(nameof(internalFormat), internalFormat, null),
                };

            public static GLEnum ToGLEnum(ESizedInternalFormat sizedInternalFormat)
                => sizedInternalFormat switch
                {
                    //Red
                    ESizedInternalFormat.R8 => GLEnum.R8,
                    ESizedInternalFormat.R8Snorm => GLEnum.R8SNorm,
                    ESizedInternalFormat.R16 => GLEnum.R16,
                    ESizedInternalFormat.R16Snorm => GLEnum.R16SNorm,
                    ESizedInternalFormat.R16f => GLEnum.R16f,
                    ESizedInternalFormat.R32f => GLEnum.R32f,
                    ESizedInternalFormat.R8i => GLEnum.R8i,
                    ESizedInternalFormat.R8ui => GLEnum.R8ui,
                    ESizedInternalFormat.R16i => GLEnum.R16i,
                    ESizedInternalFormat.R16ui => GLEnum.R16ui,
                    ESizedInternalFormat.R32i => GLEnum.R32i,
                    ESizedInternalFormat.R32ui => GLEnum.R32ui,

                    //Red Green
                    ESizedInternalFormat.Rg8 => GLEnum.RG8,
                    ESizedInternalFormat.Rg8Snorm => GLEnum.RG8SNorm,
                    ESizedInternalFormat.Rg16 => GLEnum.RG16,
                    ESizedInternalFormat.Rg16Snorm => GLEnum.RG16SNorm,
                    ESizedInternalFormat.Rg16f => GLEnum.RG16f,
                    ESizedInternalFormat.Rg32f => GLEnum.RG32f,
                    ESizedInternalFormat.Rg8i => GLEnum.RG8i,
                    ESizedInternalFormat.Rg8ui => GLEnum.RG8ui,
                    ESizedInternalFormat.Rg16i => GLEnum.RG16i,
                    ESizedInternalFormat.Rg16ui => GLEnum.RG16ui,
                    ESizedInternalFormat.Rg32i => GLEnum.RG32i,
                    ESizedInternalFormat.Rg32ui => GLEnum.RG32ui,

                    //Red Green Blue
                    ESizedInternalFormat.R3G3B2 => GLEnum.R3G3B2,
                    ESizedInternalFormat.Rgb4 => GLEnum.Rgb4,
                    ESizedInternalFormat.Rgb5 => GLEnum.Rgb5,
                    ESizedInternalFormat.Rgb8 => GLEnum.Rgb8,
                    ESizedInternalFormat.Rgb8Snorm => GLEnum.Rgb8SNorm,
                    ESizedInternalFormat.Rgb10 => GLEnum.Rgb10,
                    ESizedInternalFormat.Rgb12 => GLEnum.Rgb12,
                    ESizedInternalFormat.Rgb16Snorm => GLEnum.Rgb16SNorm,
                    //ESizedInternalFormat.Rgb16 => GLEnum.Rgb16,
                    ESizedInternalFormat.Rgba2 => GLEnum.Rgba2,
                    ESizedInternalFormat.Rgba4 => GLEnum.Rgba4,
                    ESizedInternalFormat.Srgb8 => GLEnum.Srgb8,
                    ESizedInternalFormat.Rgb16f => GLEnum.Rgb16f,
                    ESizedInternalFormat.Rgb32f => GLEnum.Rgb32f,
                    ESizedInternalFormat.R11fG11fB10f => GLEnum.R11fG11fB10f,
                    ESizedInternalFormat.Rgb9E5 => GLEnum.Rgb9E5,
                    ESizedInternalFormat.Rgb8i => GLEnum.Rgb8i,
                    ESizedInternalFormat.Rgb8ui => GLEnum.Rgb8ui,
                    ESizedInternalFormat.Rgb16i => GLEnum.Rgb16i,
                    ESizedInternalFormat.Rgb16ui => GLEnum.Rgb16ui,
                    ESizedInternalFormat.Rgb32i => GLEnum.Rgb32i,
                    ESizedInternalFormat.Rgb32ui => GLEnum.Rgb32ui,

                    //Red Green Blue Alpha
                    ESizedInternalFormat.Rgb5A1 => GLEnum.Rgb5A1,
                    ESizedInternalFormat.Rgba8 => GLEnum.Rgba8,
                    ESizedInternalFormat.Rgba8Snorm => GLEnum.Rgba8SNorm,
                    ESizedInternalFormat.Rgb10A2 => GLEnum.Rgb10A2,
                    ESizedInternalFormat.Rgba12 => GLEnum.Rgba12,
                    ESizedInternalFormat.Rgba16 => GLEnum.Rgba16,
                    ESizedInternalFormat.Srgb8Alpha8 => GLEnum.Srgb8Alpha8,
                    ESizedInternalFormat.Rgba16f => GLEnum.Rgba16f,
                    ESizedInternalFormat.Rgba32f => GLEnum.Rgba32f,
                    ESizedInternalFormat.Rgba8i => GLEnum.Rgba8i,
                    ESizedInternalFormat.Rgba8ui => GLEnum.Rgba8ui,
                    ESizedInternalFormat.Rgba16i => GLEnum.Rgba16i,
                    ESizedInternalFormat.Rgba16ui => GLEnum.Rgba16ui,
                    ESizedInternalFormat.Rgba32i => GLEnum.Rgba32i,
                    ESizedInternalFormat.Rgba32ui => GLEnum.Rgba32ui,

                    //Depth
                    ESizedInternalFormat.DepthComponent16 => GLEnum.DepthComponent16,
                    ESizedInternalFormat.DepthComponent24 => GLEnum.DepthComponent24,
                    ESizedInternalFormat.DepthComponent32f => GLEnum.DepthComponent32f,

                    //Depth Stencil
                    ESizedInternalFormat.Depth24Stencil8 => GLEnum.Depth24Stencil8,
                    ESizedInternalFormat.Depth32fStencil8 => GLEnum.Depth32fStencil8,

                    //Stencil
                    ESizedInternalFormat.StencilIndex8 => GLEnum.StencilIndex8,

                    _ => throw new ArgumentOutOfRangeException(nameof(sizedInternalFormat), sizedInternalFormat, null),
                };

            public static GLEnum ToGLEnum(EBufferTarget target)
                => target switch
                {
                    EBufferTarget.ArrayBuffer => GLEnum.ArrayBuffer,
                    EBufferTarget.ElementArrayBuffer => GLEnum.ElementArrayBuffer,
                    EBufferTarget.CopyReadBuffer => GLEnum.CopyReadBuffer,
                    EBufferTarget.CopyWriteBuffer => GLEnum.CopyWriteBuffer,
                    EBufferTarget.PixelPackBuffer => GLEnum.PixelPackBuffer,
                    EBufferTarget.PixelUnpackBuffer => GLEnum.PixelUnpackBuffer,
                    EBufferTarget.TransformFeedbackBuffer => GLEnum.TransformFeedbackBuffer,
                    EBufferTarget.UniformBuffer => GLEnum.UniformBuffer,
                    EBufferTarget.TextureBuffer => GLEnum.TextureBuffer,
                    EBufferTarget.ParameterBuffer => GLEnum.ParameterBuffer,
                    EBufferTarget.ShaderStorageBuffer => GLEnum.ShaderStorageBuffer,
                    EBufferTarget.DispatchIndirectBuffer => GLEnum.DispatchIndirectBuffer,
                    EBufferTarget.QueryBuffer => GLEnum.QueryBuffer,
                    EBufferTarget.AtomicCounterBuffer => GLEnum.AtomicCounterBuffer,
                    EBufferTarget.DrawIndirectBuffer => GLEnum.DrawIndirectBuffer,
                    _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
                };

            public static GLEnum ToGLEnum(ETextureTarget textureTarget)
                => textureTarget switch
                {
                    ETextureTarget.Texture1D => GLEnum.Texture1D,
                    ETextureTarget.Texture1DArray => GLEnum.Texture1DArray,
                    ETextureTarget.ProxyTexture1D => GLEnum.ProxyTexture1D,
                    ETextureTarget.ProxyTexture1DArray => GLEnum.ProxyTexture1DArray,

                    ETextureTarget.Texture2D => GLEnum.Texture2D,
                    ETextureTarget.Texture2DArray => GLEnum.Texture2DArray,
                    ETextureTarget.Texture2DMultisample => GLEnum.Texture2DMultisample,
                    ETextureTarget.Texture2DMultisampleArray => GLEnum.Texture2DMultisampleArray,
                    ETextureTarget.ProxyTexture2D => GLEnum.ProxyTexture2D,
                    ETextureTarget.ProxyTexture2DArray => GLEnum.ProxyTexture2DArray,
                    ETextureTarget.ProxyTexture2DMultisample => GLEnum.ProxyTexture2DMultisample,
                    ETextureTarget.ProxyTexture2DMultisampleArray => GLEnum.ProxyTexture2DMultisampleArray,

                    ETextureTarget.Texture3D => GLEnum.Texture3D,
                    ETextureTarget.ProxyTexture3D => GLEnum.ProxyTexture3D,

                    ETextureTarget.TextureCubeMap => GLEnum.TextureCubeMap,
                    ETextureTarget.ProxyTextureCubeMap => GLEnum.ProxyTextureCubeMap,
                    ETextureTarget.ProxyTextureCubeMapArray => GLEnum.ProxyTextureCubeMapArray,

                    ETextureTarget.TextureRectangle => GLEnum.TextureRectangle,
                    ETextureTarget.ProxyTextureRectangle => GLEnum.ProxyTextureRectangle,

                    ETextureTarget.TextureBuffer => GLEnum.TextureBuffer,

                    ETextureTarget.TextureBindingCubeMap => GLEnum.TextureBindingCubeMap,
                    ETextureTarget.TextureCubeMapArray => GLEnum.TextureBindingCubeMapArray,
                    ETextureTarget.TextureCubeMapPositiveX => GLEnum.TextureCubeMapPositiveX,
                    ETextureTarget.TextureCubeMapNegativeX => GLEnum.TextureCubeMapNegativeX,
                    ETextureTarget.TextureCubeMapPositiveY => GLEnum.TextureCubeMapPositiveY,
                    ETextureTarget.TextureCubeMapNegativeY => GLEnum.TextureCubeMapNegativeY,
                    ETextureTarget.TextureCubeMapPositiveZ => GLEnum.TextureCubeMapPositiveZ,
                    ETextureTarget.TextureCubeMapNegativeZ => GLEnum.TextureCubeMapNegativeZ,

                    _ => GLEnum.Texture2D
                };

            public static FramebufferAttachment ToFrameBufferAttachement(EFrameBufferAttachment attachment)
                => attachment switch
                {
                    EFrameBufferAttachment.ColorAttachment0 => FramebufferAttachment.ColorAttachment0,
                    EFrameBufferAttachment.ColorAttachment1 => FramebufferAttachment.ColorAttachment1,
                    EFrameBufferAttachment.ColorAttachment2 => FramebufferAttachment.ColorAttachment2,
                    EFrameBufferAttachment.ColorAttachment3 => FramebufferAttachment.ColorAttachment3,
                    EFrameBufferAttachment.ColorAttachment4 => FramebufferAttachment.ColorAttachment4,
                    EFrameBufferAttachment.ColorAttachment5 => FramebufferAttachment.ColorAttachment5,
                    EFrameBufferAttachment.ColorAttachment6 => FramebufferAttachment.ColorAttachment6,
                    EFrameBufferAttachment.ColorAttachment7 => FramebufferAttachment.ColorAttachment7,
                    EFrameBufferAttachment.ColorAttachment8 => FramebufferAttachment.ColorAttachment8,
                    EFrameBufferAttachment.ColorAttachment9 => FramebufferAttachment.ColorAttachment9,
                    EFrameBufferAttachment.ColorAttachment10 => FramebufferAttachment.ColorAttachment10,
                    EFrameBufferAttachment.ColorAttachment11 => FramebufferAttachment.ColorAttachment11,
                    EFrameBufferAttachment.ColorAttachment12 => FramebufferAttachment.ColorAttachment12,
                    EFrameBufferAttachment.ColorAttachment13 => FramebufferAttachment.ColorAttachment13,
                    EFrameBufferAttachment.ColorAttachment14 => FramebufferAttachment.ColorAttachment14,
                    EFrameBufferAttachment.ColorAttachment15 => FramebufferAttachment.ColorAttachment15,
                    EFrameBufferAttachment.ColorAttachment16 => FramebufferAttachment.ColorAttachment16,
                    EFrameBufferAttachment.ColorAttachment17 => FramebufferAttachment.ColorAttachment17,
                    EFrameBufferAttachment.ColorAttachment18 => FramebufferAttachment.ColorAttachment18,
                    EFrameBufferAttachment.ColorAttachment19 => FramebufferAttachment.ColorAttachment19,
                    EFrameBufferAttachment.ColorAttachment20 => FramebufferAttachment.ColorAttachment20,
                    EFrameBufferAttachment.ColorAttachment21 => FramebufferAttachment.ColorAttachment21,
                    EFrameBufferAttachment.ColorAttachment22 => FramebufferAttachment.ColorAttachment22,
                    EFrameBufferAttachment.ColorAttachment23 => FramebufferAttachment.ColorAttachment23,
                    EFrameBufferAttachment.ColorAttachment24 => FramebufferAttachment.ColorAttachment24,
                    EFrameBufferAttachment.ColorAttachment25 => FramebufferAttachment.ColorAttachment25,
                    EFrameBufferAttachment.ColorAttachment26 => FramebufferAttachment.ColorAttachment26,
                    EFrameBufferAttachment.ColorAttachment27 => FramebufferAttachment.ColorAttachment27,
                    EFrameBufferAttachment.ColorAttachment28 => FramebufferAttachment.ColorAttachment28,
                    EFrameBufferAttachment.ColorAttachment29 => FramebufferAttachment.ColorAttachment29,
                    EFrameBufferAttachment.ColorAttachment30 => FramebufferAttachment.ColorAttachment30,
                    EFrameBufferAttachment.ColorAttachment31 => FramebufferAttachment.ColorAttachment31,
                    EFrameBufferAttachment.DepthAttachment => FramebufferAttachment.DepthAttachment,
                    EFrameBufferAttachment.StencilAttachment => FramebufferAttachment.StencilAttachment,
                    EFrameBufferAttachment.DepthStencilAttachment => FramebufferAttachment.DepthStencilAttachment,
                    _ => FramebufferAttachment.ColorAttachment0
                };

            public static GLEnum ToGLEnum(EFrameBufferAttachment attachment)
                => attachment switch
                {
                    EFrameBufferAttachment.None => GLEnum.None,
                    EFrameBufferAttachment.Back => GLEnum.Back,
                    EFrameBufferAttachment.Front => GLEnum.Front,
                    EFrameBufferAttachment.Left => GLEnum.Left,
                    EFrameBufferAttachment.Right => GLEnum.Right,
                    EFrameBufferAttachment.BackLeft => GLEnum.BackLeft,
                    EFrameBufferAttachment.BackRight => GLEnum.BackRight,
                    EFrameBufferAttachment.FrontLeft => GLEnum.FrontLeft,
                    EFrameBufferAttachment.FrontRight => GLEnum.FrontRight,
                    //EFrameBufferAttachment.Color => GLEnum.Color,
                    //EFrameBufferAttachment.Depth => GLEnum.Depth,
                    //EFrameBufferAttachment.Stencil => GLEnum.Stencil,
                    EFrameBufferAttachment.ColorAttachment0 => GLEnum.ColorAttachment0,
                    EFrameBufferAttachment.ColorAttachment1 => GLEnum.ColorAttachment1,
                    EFrameBufferAttachment.ColorAttachment2 => GLEnum.ColorAttachment2,
                    EFrameBufferAttachment.ColorAttachment3 => GLEnum.ColorAttachment3,
                    EFrameBufferAttachment.ColorAttachment4 => GLEnum.ColorAttachment4,
                    EFrameBufferAttachment.ColorAttachment5 => GLEnum.ColorAttachment5,
                    EFrameBufferAttachment.ColorAttachment6 => GLEnum.ColorAttachment6,
                    EFrameBufferAttachment.ColorAttachment7 => GLEnum.ColorAttachment7,
                    EFrameBufferAttachment.ColorAttachment8 => GLEnum.ColorAttachment8,
                    EFrameBufferAttachment.ColorAttachment9 => GLEnum.ColorAttachment9,
                    EFrameBufferAttachment.ColorAttachment10 => GLEnum.ColorAttachment10,
                    EFrameBufferAttachment.ColorAttachment11 => GLEnum.ColorAttachment11,
                    EFrameBufferAttachment.ColorAttachment12 => GLEnum.ColorAttachment12,
                    EFrameBufferAttachment.ColorAttachment13 => GLEnum.ColorAttachment13,
                    EFrameBufferAttachment.ColorAttachment14 => GLEnum.ColorAttachment14,
                    EFrameBufferAttachment.ColorAttachment15 => GLEnum.ColorAttachment15,
                    EFrameBufferAttachment.ColorAttachment16 => GLEnum.ColorAttachment16,
                    EFrameBufferAttachment.ColorAttachment17 => GLEnum.ColorAttachment17,
                    EFrameBufferAttachment.ColorAttachment18 => GLEnum.ColorAttachment18,
                    EFrameBufferAttachment.ColorAttachment19 => GLEnum.ColorAttachment19,
                    EFrameBufferAttachment.ColorAttachment20 => GLEnum.ColorAttachment20,
                    EFrameBufferAttachment.ColorAttachment21 => GLEnum.ColorAttachment21,
                    EFrameBufferAttachment.ColorAttachment22 => GLEnum.ColorAttachment22,
                    EFrameBufferAttachment.ColorAttachment23 => GLEnum.ColorAttachment23,
                    EFrameBufferAttachment.ColorAttachment24 => GLEnum.ColorAttachment24,
                    EFrameBufferAttachment.ColorAttachment25 => GLEnum.ColorAttachment25,
                    EFrameBufferAttachment.ColorAttachment26 => GLEnum.ColorAttachment26,
                    EFrameBufferAttachment.ColorAttachment27 => GLEnum.ColorAttachment27,
                    EFrameBufferAttachment.ColorAttachment28 => GLEnum.ColorAttachment28,
                    EFrameBufferAttachment.ColorAttachment29 => GLEnum.ColorAttachment29,
                    EFrameBufferAttachment.ColorAttachment30 => GLEnum.ColorAttachment30,
                    EFrameBufferAttachment.ColorAttachment31 => GLEnum.ColorAttachment31,
                    EFrameBufferAttachment.DepthAttachment => GLEnum.DepthAttachment,
                    EFrameBufferAttachment.StencilAttachment => GLEnum.StencilAttachment,
                    EFrameBufferAttachment.DepthStencilAttachment => GLEnum.DepthStencilAttachment,
                    _ => throw new NotImplementedException()
                };

            public static GLEnum ToGLEnum(EFramebufferTarget target)
                => target switch
                {
                    EFramebufferTarget.Framebuffer => GLEnum.Framebuffer,
                    EFramebufferTarget.ReadFramebuffer => GLEnum.ReadFramebuffer,
                    EFramebufferTarget.DrawFramebuffer => GLEnum.DrawFramebuffer,
                    _ => throw new NotImplementedException()
                };

            public static InternalFormat ToInternalFormat(EPixelInternalFormat internalFormat)
                => (InternalFormat)internalFormat.ConvertByName(typeof(InternalFormat));

            public static EPixelInternalFormat ToBaseInternalFormat(ESizedInternalFormat sizedInternalFormat)
                => sizedInternalFormat switch
                {
                    ESizedInternalFormat.R8 or
                    ESizedInternalFormat.R8Snorm or
                    ESizedInternalFormat.R16 or
                    ESizedInternalFormat.R16Snorm or
                    ESizedInternalFormat.R16f or
                    ESizedInternalFormat.R32f or
                    ESizedInternalFormat.R8i or
                    ESizedInternalFormat.R8ui or
                    ESizedInternalFormat.R16i or
                    ESizedInternalFormat.R16ui or
                    ESizedInternalFormat.R32i or
                    ESizedInternalFormat.R32ui => EPixelInternalFormat.Red,

                    ESizedInternalFormat.Rg8 or
                    ESizedInternalFormat.Rg8Snorm or
                    ESizedInternalFormat.Rg16 or
                    ESizedInternalFormat.Rg16Snorm or
                    ESizedInternalFormat.Rg16f or
                    ESizedInternalFormat.Rg32f or
                    ESizedInternalFormat.Rg8i or
                    ESizedInternalFormat.Rg8ui or
                    ESizedInternalFormat.Rg16i or
                    ESizedInternalFormat.Rg16ui or
                    ESizedInternalFormat.Rg32i or
                    ESizedInternalFormat.Rg32ui => EPixelInternalFormat.RG,

                    ESizedInternalFormat.R3G3B2 or
                    ESizedInternalFormat.Rgb4 or
                    ESizedInternalFormat.Rgb5 or
                    ESizedInternalFormat.Rgb8 or
                    ESizedInternalFormat.Rgb8Snorm or
                    ESizedInternalFormat.Rgb10 or
                    ESizedInternalFormat.Rgb12 or
                    ESizedInternalFormat.Rgb16Snorm or
                    ESizedInternalFormat.Rgba2 or
                    ESizedInternalFormat.Rgba4 or
                    ESizedInternalFormat.Srgb8 or
                    ESizedInternalFormat.Rgb16f or
                    ESizedInternalFormat.Rgb32f or
                    ESizedInternalFormat.R11fG11fB10f or
                    ESizedInternalFormat.Rgb9E5 or
                    ESizedInternalFormat.Rgb8i or
                    ESizedInternalFormat.Rgb8ui or
                    ESizedInternalFormat.Rgb16i or
                    ESizedInternalFormat.Rgb16ui or
                    ESizedInternalFormat.Rgb32i or
                    ESizedInternalFormat.Rgb32ui => EPixelInternalFormat.Rgb,

                    ESizedInternalFormat.Rgb5A1 or
                    ESizedInternalFormat.Rgba8 or
                    ESizedInternalFormat.Rgba8Snorm or
                    ESizedInternalFormat.Rgb10A2 or
                    ESizedInternalFormat.Rgba12 or
                    ESizedInternalFormat.Rgba16 or
                    ESizedInternalFormat.Srgb8Alpha8 or
                    ESizedInternalFormat.Rgba16f or
                    ESizedInternalFormat.Rgba32f or
                    ESizedInternalFormat.Rgba8i or
                    ESizedInternalFormat.Rgba8ui or
                    ESizedInternalFormat.Rgba16i or
                    ESizedInternalFormat.Rgba16ui or
                    ESizedInternalFormat.Rgba32i or
                    ESizedInternalFormat.Rgba32ui => EPixelInternalFormat.Rgba,

                    ESizedInternalFormat.DepthComponent16 or
                    ESizedInternalFormat.DepthComponent24 or
                    ESizedInternalFormat.DepthComponent32f => EPixelInternalFormat.DepthComponent,

                    ESizedInternalFormat.Depth24Stencil8 or
                    ESizedInternalFormat.Depth32fStencil8 => EPixelInternalFormat.DepthStencil,

                    ESizedInternalFormat.StencilIndex8 => EPixelInternalFormat.StencilIndex,

                    _ => throw new ArgumentOutOfRangeException(nameof(sizedInternalFormat), sizedInternalFormat, null),
                };

            //public static ESizedInternalFormat ToSizedInternalFormat(EPixelInternalFormat internalFormat)
            //    => internalFormat switch
            //    {
            //        EPixelInternalFormat.Rgb8 => ESizedInternalFormat.Rgb8,
            //        EPixelInternalFormat.Rgba8 => ESizedInternalFormat.Rgba8,
            //        EPixelInternalFormat.Rgba16 => ESizedInternalFormat.Rgba16,
            //        EPixelInternalFormat.R8 => ESizedInternalFormat.R8,
            //        EPixelInternalFormat.R16 => ESizedInternalFormat.R16,
            //        EPixelInternalFormat.RG8 => ESizedInternalFormat.Rg8,
            //        EPixelInternalFormat.RG16 => ESizedInternalFormat.Rg16,
            //        EPixelInternalFormat.R16f => ESizedInternalFormat.R16f,
            //        EPixelInternalFormat.R32f => ESizedInternalFormat.R32f,
            //        EPixelInternalFormat.RG16f => ESizedInternalFormat.Rg16f,
            //        EPixelInternalFormat.RG32f => ESizedInternalFormat.Rg32f,
            //        EPixelInternalFormat.R8i => ESizedInternalFormat.R8i,
            //        EPixelInternalFormat.R8ui => ESizedInternalFormat.R8ui,
            //        EPixelInternalFormat.R16i => ESizedInternalFormat.R16i,
            //        EPixelInternalFormat.R16ui => ESizedInternalFormat.R16ui,
            //        EPixelInternalFormat.R32i => ESizedInternalFormat.R32i,
            //        EPixelInternalFormat.R32ui => ESizedInternalFormat.R32ui,
            //        EPixelInternalFormat.RG8i => ESizedInternalFormat.Rg8i,
            //        EPixelInternalFormat.RG8ui => ESizedInternalFormat.Rg8ui,
            //        EPixelInternalFormat.RG16i => ESizedInternalFormat.Rg16i,
            //        EPixelInternalFormat.RG16ui => ESizedInternalFormat.Rg16ui,
            //        EPixelInternalFormat.RG32i => ESizedInternalFormat.Rg32i,
            //        EPixelInternalFormat.RG32ui => ESizedInternalFormat.Rg32ui,
            //        EPixelInternalFormat.Rgb16f => ESizedInternalFormat.Rgb16f,
            //        EPixelInternalFormat.Rgb32f => ESizedInternalFormat.Rgb32f,
            //        EPixelInternalFormat.Rgba32f => ESizedInternalFormat.Rgba32f,
            //        EPixelInternalFormat.Rgba16f => ESizedInternalFormat.Rgba16f,
            //        EPixelInternalFormat.Rgba32ui => ESizedInternalFormat.Rgba32ui,
            //        EPixelInternalFormat.Rgba16ui => ESizedInternalFormat.Rgba16ui,
            //        EPixelInternalFormat.Rgba8ui => ESizedInternalFormat.Rgba8ui,
            //        EPixelInternalFormat.Rgba32i => ESizedInternalFormat.Rgba32i,
            //        EPixelInternalFormat.Rgba16i => ESizedInternalFormat.Rgba16i,
            //        EPixelInternalFormat.Rgba8i => ESizedInternalFormat.Rgba8i,
            //        _ => throw new ArgumentOutOfRangeException(nameof(internalFormat), internalFormat, null),
            //    };

            protected virtual uint CreateObject()
                => Renderer.CreateObjects(Type, 1)[0];
            protected virtual void DeleteObject()
            {
                if (!IsGenerated)
                    return;
                //Debug.Out($"Deleting OpenGL object {Type} {BindingId}");
                PreDeleted();
                uint id = _bindingId!.Value;
                switch (Type)
                {
                    case GLObjectType.Buffer:
                        Api.DeleteBuffer(id);
                        break;
                    case GLObjectType.Framebuffer:
                        Api.DeleteFramebuffer(id);
                        break;
                    case GLObjectType.Program:
                        Api.DeleteProgram(id);
                        break;
                    case GLObjectType.ProgramPipeline:
                        Api.DeleteProgramPipeline(id);
                        break;
                    case GLObjectType.Query:
                        Api.DeleteQuery(id);
                        break;
                    case GLObjectType.Renderbuffer:
                        Api.DeleteRenderbuffer(id);
                        break;
                    case GLObjectType.Sampler:
                        Api.DeleteSampler(id);
                        break;
                    case GLObjectType.Texture:
                        Api.DeleteTexture(id);
                        break;
                    case GLObjectType.TransformFeedback:
                        Api.DeleteTransformFeedback(id);
                        break;
                    case GLObjectType.VertexArray:
                        Api.DeleteVertexArray(id);
                        break;
                    case GLObjectType.Shader:
                        Api.DeleteShader(id);
                        break;
                }
                _bindingId = null;
                PostDeleted();
            }

            public static ReadBufferMode ToReadBufferMode(EFrameBufferAttachment attachment)
            {
                return attachment switch
                {
                    EFrameBufferAttachment.BackLeft => ReadBufferMode.BackLeft,
                    EFrameBufferAttachment.BackRight => ReadBufferMode.BackRight,
                    EFrameBufferAttachment.FrontLeft => ReadBufferMode.FrontLeft,
                    EFrameBufferAttachment.FrontRight => ReadBufferMode.FrontRight,
                    EFrameBufferAttachment.ColorAttachment0 => ReadBufferMode.ColorAttachment0,
                    EFrameBufferAttachment.ColorAttachment1 => ReadBufferMode.ColorAttachment1,
                    EFrameBufferAttachment.ColorAttachment2 => ReadBufferMode.ColorAttachment2,
                    EFrameBufferAttachment.ColorAttachment3 => ReadBufferMode.ColorAttachment3,
                    EFrameBufferAttachment.ColorAttachment4 => ReadBufferMode.ColorAttachment4,
                    EFrameBufferAttachment.ColorAttachment5 => ReadBufferMode.ColorAttachment5,
                    EFrameBufferAttachment.ColorAttachment6 => ReadBufferMode.ColorAttachment6,
                    EFrameBufferAttachment.ColorAttachment7 => ReadBufferMode.ColorAttachment7,
                    EFrameBufferAttachment.ColorAttachment8 => ReadBufferMode.ColorAttachment8,
                    EFrameBufferAttachment.ColorAttachment9 => ReadBufferMode.ColorAttachment9,
                    EFrameBufferAttachment.ColorAttachment10 => ReadBufferMode.ColorAttachment10,
                    EFrameBufferAttachment.ColorAttachment11 => ReadBufferMode.ColorAttachment11,
                    EFrameBufferAttachment.ColorAttachment12 => ReadBufferMode.ColorAttachment12,
                    EFrameBufferAttachment.ColorAttachment13 => ReadBufferMode.ColorAttachment13,
                    EFrameBufferAttachment.ColorAttachment14 => ReadBufferMode.ColorAttachment14,
                    EFrameBufferAttachment.ColorAttachment15 => ReadBufferMode.ColorAttachment15,
                    _ => throw new ArgumentOutOfRangeException(nameof(attachment), attachment, null),
                };
            }
        }
    }
}