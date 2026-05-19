using Silk.NET.OpenGL;
using System;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    public override XRGpuFence? InsertGpuFence()
    {
        IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        return sync == IntPtr.Zero ? null : new OpenGLGpuFence(this, sync);
    }

    private sealed class OpenGLGpuFence(OpenGLRenderer renderer, IntPtr sync) : XRGpuFence
    {
        private OpenGLRenderer? _renderer = renderer;
        private IntPtr _sync = sync;

        protected override EGpuFenceStatus PollCore()
        {
            if (_renderer is null || _sync == IntPtr.Zero)
                return EGpuFenceStatus.Signaled;

            GLEnum status = _renderer.Api.ClientWaitSync(_sync, 0u, 0u);
            return status switch
            {
                GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied => EGpuFenceStatus.Signaled,
                GLEnum.WaitFailed => EGpuFenceStatus.Failed,
                _ => EGpuFenceStatus.Pending
            };
        }

        protected override void DisposeCore()
        {
            if (_renderer is not null && _sync != IntPtr.Zero)
                _renderer.Api.DeleteSync(_sync);

            _renderer = null;
            _sync = IntPtr.Zero;
        }
    }
}
