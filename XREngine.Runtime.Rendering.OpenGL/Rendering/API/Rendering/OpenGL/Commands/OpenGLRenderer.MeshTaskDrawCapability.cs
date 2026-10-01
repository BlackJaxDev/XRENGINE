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
    {
        if (NVMeshShader is null)
            throw new NotSupportedException("OpenGL.MeshTasks.DirectUnavailable: GL_NV_mesh_shader is not available on the active context.");
        NVMeshShader.DrawMeshTask(firstTask, taskCount);
    }
}
