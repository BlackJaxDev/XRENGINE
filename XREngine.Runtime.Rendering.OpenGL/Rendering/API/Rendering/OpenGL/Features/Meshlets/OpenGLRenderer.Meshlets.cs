using Silk.NET.Core.Contexts;
using System;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL;

public unsafe partial class OpenGLRenderer
{
    private GlMultiDrawMeshTasksIndirectCountExtDelegate? _glMultiDrawMeshTasksIndirectCountExt;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GlMultiDrawMeshTasksIndirectCountExtDelegate(void* indirect, nint drawCountOffset, int maxDrawCount, int stride);

    public override EMeshShaderDialect MeshShaderDialect
        => Api.IsExtensionPresent("GL_EXT_mesh_shader")
            ? EMeshShaderDialect.OpenGLEXT
            : NVMeshShader is not null
            ? EMeshShaderDialect.OpenGLNV
            : EMeshShaderDialect.None;

    public override bool SupportsDirectMeshTaskDispatch()
        => NVMeshShader is not null;

    public override bool SupportsIndirectCountMeshTaskDispatch()
        => MeshShaderDialect == EMeshShaderDialect.OpenGLEXT &&
           SupportsIndirectCountDraw() &&
           _glMultiDrawMeshTasksIndirectCountExt is not null;

    public override bool SupportsProductionMeshletShaders()
        => MeshShaderDialect == EMeshShaderDialect.OpenGLEXT;

    public override bool TryDrawMeshTasksIndirectCount(
        XRDataBuffer indirectBuffer,
        XRDataBuffer countBuffer,
        uint maxDrawCount,
        uint stride,
        out string failureReason,
        nuint byteOffset = 0,
        nuint countByteOffset = 0)
    {
        if (!ValidateMeshTasksIndirectCountArgs(
            indirectBuffer,
            countBuffer,
            maxDrawCount,
            stride,
            byteOffset,
            countByteOffset,
            out failureReason))
        {
            return false;
        }

        if (!SupportsIndirectCountMeshTaskDispatch() || _glMultiDrawMeshTasksIndirectCountExt is null)
        {
            failureReason = MeshletDispatchUnsupportedReason;
            Debug.Out(failureReason);
            return false;
        }

        if (maxDrawCount > int.MaxValue || stride > int.MaxValue)
        {
            failureReason = "OpenGL mesh-task indirect-count dispatch requires maxDrawCount and stride to fit GLsizei.";
            Debug.Out(failureReason);
            return false;
        }

        BindDrawIndirectBuffer(indirectBuffer);
        BindParameterBuffer(countBuffer);
        _glMultiDrawMeshTasksIndirectCountExt((void*)byteOffset, (nint)countByteOffset, (int)maxDrawCount, (int)stride);

        RuntimeEngine.Rendering.Stats.Frame.IncrementMultiDrawCalls();
        failureReason = string.Empty;
        return true;
    }

    public override string MeshletDispatchUnsupportedReason
        => MeshShaderDialect switch
        {
            EMeshShaderDialect.OpenGLEXT when _glMultiDrawMeshTasksIndirectCountExt is null =>
                "GL_EXT_mesh_shader is visible, but glMultiDrawMeshTasksIndirectCountEXT is unavailable on the active OpenGL context.",
            EMeshShaderDialect.OpenGLEXT when !SupportsIndirectCountDraw() =>
                "GL_EXT_mesh_shader is visible, but GL 4.6/GL_ARB_indirect_parameters count buffers are unavailable.",
            EMeshShaderDialect.OpenGLEXT =>
                "OpenGL EXT mesh-task indirect-count dispatch is available.",
            EMeshShaderDialect.OpenGLNV =>
                "OpenGL NV mesh shader support is diagnostic-only because production indirect-count task dispatch is not implemented.",
            _ =>
                "GL_EXT_mesh_shader/GL_NV_mesh_shader is not available on the active OpenGL context."
        };

    private void LoadMeshTaskDispatchDelegates()
    {
        if (Window.GLContext is not INativeContext nativeContext)
            return;

        if (_glMultiDrawMeshTasksIndirectCountExt is null &&
            nativeContext.TryGetProcAddress("glMultiDrawMeshTasksIndirectCountEXT", out IntPtr indirectCountProc) &&
            indirectCountProc != IntPtr.Zero)
        {
            _glMultiDrawMeshTasksIndirectCountExt =
                Marshal.GetDelegateForFunctionPointer<GlMultiDrawMeshTasksIndirectCountExtDelegate>(indirectCountProc);
        }
    }
}
