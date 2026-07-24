using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IGpuFenceBackendCapability
{
    /// <inheritdoc />
    public IntPtr CreateCompletionFence()
        => RawGL.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);

    /// <inheritdoc />
    public bool IsFenceComplete(IntPtr fence)
    {
        if (fence == IntPtr.Zero)
            return true;

        GLEnum status = RawGL.ClientWaitSync(fence, 0u, 0u);
        return status is GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied;
    }

    /// <inheritdoc />
    public void DeleteFence(IntPtr fence)
    {
        if (fence != IntPtr.Zero)
            RawGL.DeleteSync(fence);
    }
}
