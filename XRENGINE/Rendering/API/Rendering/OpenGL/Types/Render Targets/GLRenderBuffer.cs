using Silk.NET.OpenGL;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public class GLRenderBuffer(OpenGLRenderer renderer, XRRenderBuffer data) : GLObject<XRRenderBuffer>(renderer, data)
    {
        public override EGLObjectType Type => EGLObjectType.Renderbuffer;

        /// <summary>
        /// Tracks the currently allocated GPU memory size for this render buffer in bytes.
        /// </summary>
        private long _allocatedVRAMBytes = 0;

        protected override void LinkData()
        {
            Data.AllocateRequested += Allocate;
            Data.BindRequested += Bind;
            Data.UnbindRequested += Unbind;
            Data.AttachToFBORequested += AttachToFBO;
            Data.DetachFromFBORequested += DetachFromFBO;
        }

        protected override void UnlinkData()
        {
            Data.AllocateRequested -= Allocate;
            Data.BindRequested -= Bind;
            Data.UnbindRequested -= Unbind;
            Data.AttachToFBORequested -= AttachToFBO;
            Data.DetachFromFBORequested -= DetachFromFBO;
        }

        public bool Invalidated { get; private set; } = true;

        public void Bind()
        {
            Api.BindRenderbuffer(GLEnum.Renderbuffer, BindingId);
            if (Invalidated)
            {
                Invalidated = false;

                // Track VRAM deallocation of previous buffer if any
                if (_allocatedVRAMBytes > 0)
                {
                    Engine.Rendering.Stats.RemoveRenderBufferAllocation(_allocatedVRAMBytes);
                    _allocatedVRAMBytes = 0;
                }

                if (Data.IsMultisample)
                    Api.NamedRenderbufferStorageMultisample(BindingId, Data.MultisampleCount, ToGLEnum(Data.Type), Data.Width, Data.Height);
                else
                    Api.NamedRenderbufferStorage(BindingId, ToGLEnum(Data.Type), Data.Width, Data.Height);

                // Track VRAM allocation
                _allocatedVRAMBytes = CalculateRenderBufferVRAMSize(Data.Width, Data.Height, Data.Type, Data.IsMultisample ? Data.MultisampleCount : 1u);
                Engine.Rendering.Stats.AddRenderBufferAllocation(_allocatedVRAMBytes);
            }
        }
        public void Unbind()
            => Api.BindRenderbuffer(GLEnum.Renderbuffer, 0);

        private void Allocate()
            => Invalidated = true;

        protected internal override void PreDeleted()
        {
            // Track VRAM deallocation
            if (_allocatedVRAMBytes > 0)
            {
                Engine.Rendering.Stats.RemoveRenderBufferAllocation(_allocatedVRAMBytes);
                _allocatedVRAMBytes = 0;
            }
            base.PreDeleted();
        }

        /// <summary>
        /// Calculates the approximate VRAM size for a render buffer.
        /// </summary>
        private static long CalculateRenderBufferVRAMSize(uint width, uint height, ERenderBufferStorage type, uint sampleCount)
        {
            uint bpp = GetBytesPerPixel(type);
            return (long)width * height * bpp * sampleCount;
        }

        /// <summary>
        /// Returns the bytes per pixel for a given render buffer storage type.
        /// </summary>
        private static uint GetBytesPerPixel(ERenderBufferStorage type)
        {
            return type switch
            {
                ERenderBufferStorage.DepthComponent => 3,
                ERenderBufferStorage.R3G3B2 => 1,
                ERenderBufferStorage.Rgb4 => 2,
                ERenderBufferStorage.Rgb5 => 2,
                ERenderBufferStorage.Rgb8 => 3,
                ERenderBufferStorage.Rgb10 => 4,
                ERenderBufferStorage.Rgb12 => 5,
                ERenderBufferStorage.Rgb16 => 6,
                ERenderBufferStorage.Rgba2 => 1,
                ERenderBufferStorage.Rgba4 => 2,
                ERenderBufferStorage.Rgba8 => 4,
                ERenderBufferStorage.Rgb10A2 => 4,
                ERenderBufferStorage.Rgba12 => 6,
                ERenderBufferStorage.Rgba16 => 8,
                ERenderBufferStorage.DepthComponent16 => 2,
                ERenderBufferStorage.DepthComponent24 => 3,
                ERenderBufferStorage.DepthComponent32 => 4,
                ERenderBufferStorage.R8 => 1,
                ERenderBufferStorage.R16 => 2,
                ERenderBufferStorage.R16f => 2,
                ERenderBufferStorage.R32f => 4,
                ERenderBufferStorage.R8i => 1,
                ERenderBufferStorage.R8ui => 1,
                ERenderBufferStorage.R16i => 2,
                ERenderBufferStorage.R16ui => 2,
                ERenderBufferStorage.R32i => 4,
                ERenderBufferStorage.R32ui => 4,
                ERenderBufferStorage.DepthStencil => 4,
                ERenderBufferStorage.Rgba32f => 16,
                ERenderBufferStorage.Rgb32f => 12,
                ERenderBufferStorage.Rgba16f => 8,
                ERenderBufferStorage.Rgb16f => 6,
                ERenderBufferStorage.Depth24Stencil8 => 4,
                ERenderBufferStorage.R11fG11fB10f => 4,
                ERenderBufferStorage.Rgb9E5 => 4,
                ERenderBufferStorage.Srgb8 => 3,
                ERenderBufferStorage.Srgb8Alpha8 => 4,
                ERenderBufferStorage.DepthComponent32f => 4,
                ERenderBufferStorage.Depth32fStencil8 => 5,
                ERenderBufferStorage.StencilIndex1 => 1,
                ERenderBufferStorage.StencilIndex4 => 1,
                ERenderBufferStorage.StencilIndex8 => 1,
                ERenderBufferStorage.StencilIndex16 => 2,
                ERenderBufferStorage.Rgba32ui => 16,
                ERenderBufferStorage.Rgb32ui => 12,
                ERenderBufferStorage.Rgba16ui => 8,
                ERenderBufferStorage.Rgb16ui => 6,
                ERenderBufferStorage.Rgba8ui => 4,
                ERenderBufferStorage.Rgb8ui => 3,
                ERenderBufferStorage.Rgba32i => 16,
                ERenderBufferStorage.Rgb32i => 12,
                ERenderBufferStorage.Rgba16i => 8,
                ERenderBufferStorage.Rgb16i => 6,
                ERenderBufferStorage.Rgba8i => 4,
                ERenderBufferStorage.Rgb8i => 3,
                ERenderBufferStorage.Rgb10A2ui => 4,
                _ => 4, // Default assumption
            };
        }

        public void AttachToFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel)
            => Api.NamedFramebufferRenderbuffer(Renderer.GenericToAPI<GLFrameBuffer>(target)!.BindingId, ToGLEnum(attachment), GLEnum.Renderbuffer, BindingId);
        public void DetachFromFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel)
            => Api.NamedFramebufferRenderbuffer(Renderer.GenericToAPI<GLFrameBuffer>(target)!.BindingId, ToGLEnum(attachment), GLEnum.Renderbuffer, 0);

        private static GLEnum ToGLEnum(ERenderBufferStorage type) => type switch
        {
            ERenderBufferStorage.DepthComponent => GLEnum.DepthComponent,
            ERenderBufferStorage.R3G3B2 => GLEnum.R3G3B2,
            ERenderBufferStorage.Rgb4 => GLEnum.Rgb4,
            ERenderBufferStorage.Rgb5 => GLEnum.Rgb5,
            ERenderBufferStorage.Rgb8 => GLEnum.Rgb8,
            ERenderBufferStorage.Rgb10 => GLEnum.Rgb10,
            ERenderBufferStorage.Rgb12 => GLEnum.Rgb12,
            ERenderBufferStorage.Rgb16 => GLEnum.Rgb16,
            ERenderBufferStorage.Rgba2 => GLEnum.Rgba2,
            ERenderBufferStorage.Rgba4 => GLEnum.Rgba4,
            ERenderBufferStorage.Rgba8 => GLEnum.Rgba8,
            ERenderBufferStorage.Rgb10A2 => GLEnum.Rgb10A2,
            ERenderBufferStorage.Rgba12 => GLEnum.Rgba12,
            ERenderBufferStorage.Rgba16 => GLEnum.Rgba16,
            ERenderBufferStorage.DepthComponent16 => GLEnum.DepthComponent16,
            ERenderBufferStorage.DepthComponent24 => GLEnum.DepthComponent24,
            ERenderBufferStorage.DepthComponent32 => GLEnum.DepthComponent32,
            ERenderBufferStorage.R8 => GLEnum.R8,
            ERenderBufferStorage.R16 => GLEnum.R16,
            ERenderBufferStorage.R16f => GLEnum.R16f,
            ERenderBufferStorage.R32f => GLEnum.R32f,
            ERenderBufferStorage.R8i => GLEnum.R8i,
            ERenderBufferStorage.R8ui => GLEnum.R8ui,
            ERenderBufferStorage.R16i => GLEnum.R16i,
            ERenderBufferStorage.R16ui => GLEnum.R16ui,
            ERenderBufferStorage.R32i => GLEnum.R32i,
            ERenderBufferStorage.R32ui => GLEnum.R32ui,
            ERenderBufferStorage.DepthStencil => GLEnum.DepthStencil,
            ERenderBufferStorage.Rgba32f => GLEnum.Rgba32f,
            ERenderBufferStorage.Rgb32f => GLEnum.Rgb32f,
            ERenderBufferStorage.Rgba16f => GLEnum.Rgba16f,
            ERenderBufferStorage.Rgb16f => GLEnum.Rgb16f,
            ERenderBufferStorage.Depth24Stencil8 => GLEnum.Depth24Stencil8,
            ERenderBufferStorage.R11fG11fB10f => GLEnum.R11fG11fB10f,
            ERenderBufferStorage.Rgb9E5 => GLEnum.Rgb9E5,
            ERenderBufferStorage.Srgb8 => GLEnum.Srgb8,
            ERenderBufferStorage.Srgb8Alpha8 => GLEnum.Srgb8Alpha8,
            ERenderBufferStorage.DepthComponent32f => GLEnum.DepthComponent32f,
            ERenderBufferStorage.Depth32fStencil8 => GLEnum.Depth32fStencil8,
            ERenderBufferStorage.StencilIndex1 => GLEnum.StencilIndex1,
            ERenderBufferStorage.StencilIndex4 => GLEnum.StencilIndex4,
            ERenderBufferStorage.StencilIndex8 => GLEnum.StencilIndex8,
            ERenderBufferStorage.StencilIndex16 => GLEnum.StencilIndex16,
            ERenderBufferStorage.Rgba32ui => GLEnum.Rgba32ui,
            ERenderBufferStorage.Rgb32ui => GLEnum.Rgb32ui,
            ERenderBufferStorage.Rgba16ui => GLEnum.Rgba16ui,
            ERenderBufferStorage.Rgb16ui => GLEnum.Rgb16ui,
            ERenderBufferStorage.Rgba8ui => GLEnum.Rgba8ui,
            ERenderBufferStorage.Rgb8ui => GLEnum.Rgb8ui,
            ERenderBufferStorage.Rgba32i => GLEnum.Rgba32i,
            ERenderBufferStorage.Rgb32i => GLEnum.Rgb32i,
            ERenderBufferStorage.Rgba16i => GLEnum.Rgba16i,
            ERenderBufferStorage.Rgb16i => GLEnum.Rgb16i,
            ERenderBufferStorage.Rgba8i => GLEnum.Rgba8i,
            ERenderBufferStorage.Rgb8i => GLEnum.Rgb8i,
            ERenderBufferStorage.Rgb10A2ui => GLEnum.Rgb10A2ui,
            _ => GLEnum.Rgba8
        };
    }
}