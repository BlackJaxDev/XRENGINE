using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IMeshTaskDrawBackendCapability
{
    /// <inheritdoc />
    public bool SupportsMeshTaskDraw => NVMeshShader is not null;

    /// <inheritdoc />
    public void PrepareMeshTaskDraw()
    {
        RawGL.Disable(EnableCap.CullFace);
        RawGL.Disable(EnableCap.StencilTest);
        RawGL.Disable(EnableCap.Blend);
    }

    /// <inheritdoc />
    public void DrawMeshTasks(uint firstTask, uint taskCount)
        => NVMeshShader?.DrawMeshTask(firstTask, taskCount);
}
