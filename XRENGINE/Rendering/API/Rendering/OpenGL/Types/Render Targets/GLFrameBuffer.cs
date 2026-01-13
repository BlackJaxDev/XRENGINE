using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.NV;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public class GLFrameBuffer(OpenGLRenderer renderer, XRFrameBuffer data) : GLObject<XRFrameBuffer>(renderer, data)
    {
        public override EGLObjectType Type => EGLObjectType.Framebuffer;

        protected override void UnlinkData()
        {
            Data.SetDrawBuffersRequested -= SetDrawBuffers;
            Data.PropertyChanged -= DataOnPropertyChanged;
            Data.BindForReadRequested -= BindForReading;
            Data.BindForWriteRequested -= BindForWriting;
            Data.BindRequested -= Bind;
            Data.UnbindFromReadRequested -= UnbindFromReading;
            Data.UnbindFromWriteRequested -= UnbindFromWriting;
            Data.UnbindRequested -= Unbind;
        }

        protected override void LinkData()
        {
            Data.SetDrawBuffersRequested += SetDrawBuffers;
            Data.PropertyChanged += DataOnPropertyChanged;
            Data.BindForReadRequested += BindForReading;
            Data.BindForWriteRequested += BindForWriting;
            Data.BindRequested += Bind;
            Data.UnbindFromReadRequested += UnbindFromReading;
            Data.UnbindFromWriteRequested += UnbindFromWriting;
            Data.UnbindRequested += Unbind;
        }

        private volatile bool _invalidated = true;
        private (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[]? _attachedTargetsCache;

        private void DataOnPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            // NOTE: PropertyChanged can occur from any thread. Do not perform GL calls here.
            if (e.PropertyName == nameof(XRFrameBuffer.Targets) || e.PropertyName == nameof(XRFrameBuffer.DrawBuffers))
                _invalidated = true;
        }

        private bool _verifying = false;
        private void VerifyAttached()
        {
            if (!_invalidated || _verifying)
                return;

            // We intentionally defer attachment changes to the render thread.
            if (!Engine.IsRenderThread)
                return;

            _verifying = true;
            try
            {
                _invalidated = false;

                if (_attachedTargetsCache is not null && _attachedTargetsCache.Length > 0)
                    Data.DetachTargets(_attachedTargetsCache);

                Data.AttachAll();

                // Snapshot currently attached targets so future changes can detach the previous set.
                _attachedTargetsCache = Data.Targets?.ToArray();
            }
            finally
            {
                _verifying = false;
            }
        }

        public override bool TryGetBindingId(out uint bindingId)
        {
            bool success = base.TryGetBindingId(out bindingId);
            if (success)
                VerifyAttached();
            return success;
        }

        public void BindForReading()
        {
            if (!Engine.IsRenderThread)
            {
                Debug.LogWarning("Can't bind framebuffer from non-render thread.");
                return;
            }
            
            Api.BindFramebuffer(GLEnum.ReadFramebuffer, BindingId);
        }

        public void UnbindFromReading()
        {
            if (!Engine.IsRenderThread)
            {
                Debug.LogWarning("Can't unbind framebuffer from non-render thread.");
                return;
            }

            Api.BindFramebuffer(GLEnum.ReadFramebuffer, 0);
        }

        public void BindForWriting()
        {
            if (!Engine.IsRenderThread)
            {
                Debug.LogWarning("Can't bind framebuffer from non-render thread.");
                return;
            }

            Api.BindFramebuffer(GLEnum.DrawFramebuffer, BindingId);
        }
        public void UnbindFromWriting()
        {
            if (!Engine.IsRenderThread)
            {
                Debug.LogWarning("Can't unbind framebuffer from non-render thread.");
                return;
            }

            Api.BindFramebuffer(GLEnum.DrawFramebuffer, 0);
        }

        //Same as BindForWriting, technically
        public void Bind()
        {
            if (!Engine.IsRenderThread)
            {
                Debug.LogWarning("Can't bind framebuffer from non-render thread.");
                return;
            }

            Api.BindFramebuffer(GLEnum.Framebuffer, BindingId);
        }
        //Same as UnbindFromWriting, technically
        public void Unbind()
        {
            if (!Engine.IsRenderThread)
            {
                Debug.LogWarning("Can't unbind framebuffer from non-render thread.");
                return;
            }

            Api.BindFramebuffer(GLEnum.Framebuffer, 0);
        }

        public unsafe void SetDrawBuffers()
        {
            if (!Engine.IsRenderThread)
            {
                Debug.LogWarning("Can't set framebuffer draw buffers from non-render thread.");
                return;
            }

            var casted = Data.DrawBuffers?.Select(ToGLEnum)?.ToArray();
            if (casted is null || casted.Length == 0)
                casted = [GLEnum.None];

            fixed (GLEnum* drawBuffers = casted)
            {
                Api.NamedFramebufferDrawBuffers(BindingId, (uint)casted.Length, drawBuffers);
            }
            Api.NamedFramebufferReadBuffer(BindingId, GLEnum.None);
            CheckErrors();
        }

        private static GLEnum ToGLEnum(EDrawBuffersAttachment x)
            => x switch
            {
                EDrawBuffersAttachment.FrontLeft => GLEnum.FrontLeft,
                EDrawBuffersAttachment.FrontRight => GLEnum.FrontRight,
                EDrawBuffersAttachment.BackLeft => GLEnum.BackLeft,
                EDrawBuffersAttachment.BackRight => GLEnum.BackRight,

                EDrawBuffersAttachment.ColorAttachment0 => GLEnum.ColorAttachment0,
                EDrawBuffersAttachment.ColorAttachment1 => GLEnum.ColorAttachment1,
                EDrawBuffersAttachment.ColorAttachment2 => GLEnum.ColorAttachment2,
                EDrawBuffersAttachment.ColorAttachment3 => GLEnum.ColorAttachment3,
                EDrawBuffersAttachment.ColorAttachment4 => GLEnum.ColorAttachment4,
                EDrawBuffersAttachment.ColorAttachment5 => GLEnum.ColorAttachment5,
                EDrawBuffersAttachment.ColorAttachment6 => GLEnum.ColorAttachment6,
                EDrawBuffersAttachment.ColorAttachment7 => GLEnum.ColorAttachment7,
                EDrawBuffersAttachment.ColorAttachment8 => GLEnum.ColorAttachment8,
                EDrawBuffersAttachment.ColorAttachment9 => GLEnum.ColorAttachment9,
                EDrawBuffersAttachment.ColorAttachment10 => GLEnum.ColorAttachment10,
                EDrawBuffersAttachment.ColorAttachment11 => GLEnum.ColorAttachment11,
                EDrawBuffersAttachment.ColorAttachment12 => GLEnum.ColorAttachment12,
                EDrawBuffersAttachment.ColorAttachment13 => GLEnum.ColorAttachment13,
                EDrawBuffersAttachment.ColorAttachment14 => GLEnum.ColorAttachment14,
                EDrawBuffersAttachment.ColorAttachment15 => GLEnum.ColorAttachment15,
                EDrawBuffersAttachment.ColorAttachment16 => GLEnum.ColorAttachment16,
                EDrawBuffersAttachment.ColorAttachment17 => GLEnum.ColorAttachment17,
                EDrawBuffersAttachment.ColorAttachment18 => GLEnum.ColorAttachment18,
                EDrawBuffersAttachment.ColorAttachment19 => GLEnum.ColorAttachment19,
                EDrawBuffersAttachment.ColorAttachment20 => GLEnum.ColorAttachment20,
                EDrawBuffersAttachment.ColorAttachment21 => GLEnum.ColorAttachment21,
                EDrawBuffersAttachment.ColorAttachment22 => GLEnum.ColorAttachment22,
                EDrawBuffersAttachment.ColorAttachment23 => GLEnum.ColorAttachment23,
                EDrawBuffersAttachment.ColorAttachment24 => GLEnum.ColorAttachment24,
                EDrawBuffersAttachment.ColorAttachment25 => GLEnum.ColorAttachment25,
                EDrawBuffersAttachment.ColorAttachment26 => GLEnum.ColorAttachment26,
                EDrawBuffersAttachment.ColorAttachment27 => GLEnum.ColorAttachment27,
                EDrawBuffersAttachment.ColorAttachment28 => GLEnum.ColorAttachment28,
                EDrawBuffersAttachment.ColorAttachment29 => GLEnum.ColorAttachment29,
                EDrawBuffersAttachment.ColorAttachment30 => GLEnum.ColorAttachment30,
                EDrawBuffersAttachment.ColorAttachment31 => GLEnum.ColorAttachment31,

                EDrawBuffersAttachment.None => GLEnum.None,
                _ => GLEnum.None,
            };

        public void CheckErrors()
            => Renderer.CheckFrameBufferErrors(this);

        public int GetInteger(GLEnum parameter)
        {
            Api.GetNamedFramebufferParameter(BindingId, parameter, out int value);
            return value;
        }

        public int GetAttachmentParameter(GLEnum attachment, GLEnum parameter)
        {
            Api.GetNamedFramebufferAttachmentParameter(BindingId, attachment, parameter, out int value);
            return value;
        }
    }
}
