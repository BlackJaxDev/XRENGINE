using Silk.NET.OpenGL;
using System;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private const int GpuFencePoolCapacity = 64;
    private readonly Stack<OpenGLGpuFence> _gpuFencePool = new(GpuFencePoolCapacity);
    private OpenGLGpuFence? _liveGpuFences;
    private bool _gpuFencesRetired;

    public override XRGpuFence? InsertGpuFence()
    {
        if (!AcceptsBackendWork || _gpuFencesRetired)
            return null;

        IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        if (sync == IntPtr.Zero)
            return null;
        OpenGLGpuFence fence = _gpuFencePool.Count > 0 ? _gpuFencePool.Pop() : new OpenGLGpuFence(this);
        fence.Initialize(sync);
        fence.Next = _liveGpuFences;
        if (_liveGpuFences is not null)
            _liveGpuFences.Previous = fence;
        _liveGpuFences = fence;
        return fence;
    }

    /// <summary>Releases outstanding syncs before the owning native API is disposed.</summary>
    private void RetireGpuFences(bool orphanNativeHandles)
    {
        _gpuFencesRetired = true;
        while (_liveGpuFences is { } fence)
        {
            _liveGpuFences = fence.Next;
            fence.ReleaseNativeHandle(orphanNativeHandles);
            fence.Previous = null;
            fence.Next = null;
        }
        _gpuFencePool.Clear();
    }

    private sealed class OpenGLGpuFence(OpenGLRenderer renderer) : XRGpuFence
    {
        private readonly OpenGLRenderer _renderer = renderer;
        private IntPtr _sync;
        public OpenGLGpuFence? Previous;
        public OpenGLGpuFence? Next;

        public override EGpuFenceSubmissionStatus SubmissionStatus
            => _renderer.AcceptsBackendWork && !_renderer._gpuFencesRetired
                ? EGpuFenceSubmissionStatus.Submitted
                : EGpuFenceSubmissionStatus.Failed;

        public void Initialize(IntPtr sync)
        {
            ResetForReuse();
            _sync = sync;
            Previous = null;
            Next = null;
        }

        protected override EGpuFenceStatus PollCore()
        {
            if (!_renderer.AcceptsBackendWork || _renderer._gpuFencesRetired ||
                !ReferenceEquals(Current, _renderer))
                return EGpuFenceStatus.Failed;

            if (_sync == IntPtr.Zero)
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
            if (Previous is not null)
                Previous.Next = Next;
            else if (ReferenceEquals(_renderer._liveGpuFences, this))
                _renderer._liveGpuFences = Next;
            if (Next is not null)
                Next.Previous = Previous;
            Previous = null;
            Next = null;

            ReleaseNativeHandle(_renderer._gpuFencesRetired || _renderer.ShouldOrphanGLHandlesForShutdown);
            // Fence receipts are consumed every DDGI update. Reuse the managed
            // wrapper after disposal rather than allocating a per-frame object.
            if (_renderer.AcceptsBackendWork && !_renderer._gpuFencesRetired &&
                _renderer._gpuFencePool.Count < GpuFencePoolCapacity)
                _renderer._gpuFencePool.Push(this);
        }

        public void ReleaseNativeHandle(bool orphanNativeHandle)
        {
            if (_sync != IntPtr.Zero && !orphanNativeHandle)
                _renderer.Api.DeleteSync(_sync);
            _sync = IntPtr.Zero;
        }
    }
}
