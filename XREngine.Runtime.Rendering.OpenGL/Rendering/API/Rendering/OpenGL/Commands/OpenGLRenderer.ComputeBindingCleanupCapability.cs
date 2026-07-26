namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IComputeBindingCleanupBackendCapability
{
    /// <inheritdoc />
    public void ClearStorageBufferBindings(uint maxBinding)
    {
        for (uint binding = 0u; binding <= maxBinding; binding++)
            RawGL.BindBufferBase(Silk.NET.OpenGL.GLEnum.ShaderStorageBuffer, binding, 0);
    }
}
