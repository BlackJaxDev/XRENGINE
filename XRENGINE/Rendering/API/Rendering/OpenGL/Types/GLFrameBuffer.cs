using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.NV;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public class GLFrameBuffer(OpenGLRenderer renderer, XRFrameBuffer data) : GLObject<XRFrameBuffer>(renderer, data)
    {
        public override GLObjectType Type => GLObjectType.Framebuffer;

        protected override void UnlinkData()
        {
            Data.SetDrawBuffersRequested -= SetDrawBuffers;
            Data.PreSetRenderTarget -= PreSetRenderTarget;
            Data.PostSetRenderTarget -= PostSetRenderTarget;
            Data.PreSetRenderTargets -= PreSetRenderTargets;
            Data.PostSetRenderTargets -= PostSetRenderTargets;
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
            Data.PreSetRenderTarget += PreSetRenderTarget;
            Data.PostSetRenderTarget += PostSetRenderTarget;
            Data.PreSetRenderTargets += PreSetRenderTargets;
            Data.PostSetRenderTargets += PostSetRenderTargets;
            Data.BindForReadRequested += BindForReading;
            Data.BindForWriteRequested += BindForWriting;
            Data.BindRequested += Bind;
            Data.UnbindFromReadRequested += UnbindFromReading;
            Data.UnbindFromWriteRequested += UnbindFromWriting;
            Data.UnbindRequested += Unbind;
        }

        private bool _invalidated = true;

        private void PreSetRenderTargets()
        {
            if (IsGenerated)
                Data.DetachAll();
            else
                _invalidated = true;
        }
        private void PostSetRenderTargets()
        {
            if (IsGenerated)
                Data.AttachAll();
            else
                _invalidated = true;
        }

        private void PreSetRenderTarget(int i)
        {
            if (IsGenerated)
            {
                Bind();
                Data.Detach(i);
            }
            else
                _invalidated = true;
        }
        private void PostSetRenderTarget(int i)
        {
            if (IsGenerated)
            {
                Data.Attach(i);
                Unbind();
            }
            else
                _invalidated = true;
        }

        private bool _verifying = false;
        private void VerifyAttached()
        {
            if (!_invalidated || _verifying)
                return;
            _verifying = true;
            _invalidated = false;
            Data.AttachAll();
            _verifying = false;
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
            Api.BindFramebuffer(GLEnum.ReadFramebuffer, BindingId);
        }

        public void UnbindFromReading()
        {
            //CheckErrors();
            Api.BindFramebuffer(GLEnum.ReadFramebuffer, 0);
        }

        public void BindForWriting()
        {
            Api.BindFramebuffer(GLEnum.DrawFramebuffer, BindingId);
        }
        public void UnbindFromWriting()
        {
            //CheckErrors();
            Api.BindFramebuffer(GLEnum.DrawFramebuffer, 0);
        }

        //Same as BindForWriting, technically
        public void Bind()
        {
            Api.BindFramebuffer(GLEnum.Framebuffer, BindingId);
        }
        //Same as UnbindFromWriting, technically
        public void Unbind()
        {
            //CheckErrors();
            Api.BindFramebuffer(GLEnum.Framebuffer, 0);
        }

        public unsafe void SetDrawBuffers()
        {
            var casted = Data.DrawBuffers?.Select(ToGLEnum)?.ToArray();
            if (casted != null)
            {
                fixed (GLEnum* drawBuffers = casted)
                {
                    Api.NamedFramebufferDrawBuffers(BindingId, (uint)casted.Length, drawBuffers);
                }
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
