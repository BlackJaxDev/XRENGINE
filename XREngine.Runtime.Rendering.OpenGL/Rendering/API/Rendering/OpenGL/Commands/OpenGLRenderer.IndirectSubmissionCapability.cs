using System.Numerics;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer :
    IIndirectDrawStateBackendCapability,
    ISceneDatabaseDeviceAddressBackendCapability
{
    bool IIndirectDrawStateBackendCapability.TryBeginIndirectDrawState(
        XRRenderProgram program,
        XRMaterial? material,
        in Matrix4x4 modelMatrix,
        out IndirectDrawStateToken token)
    {
        token = default;
        return true;
    }

    void IIndirectDrawStateBackendCapability.EndIndirectDrawState(in IndirectDrawStateToken token)
    {
    }

    bool ISceneDatabaseDeviceAddressBackendCapability.TryBindSceneDatabaseDeviceAddressUniforms(
        XRRenderProgram program,
        XRDataBuffer drawMetadataBuffer,
        XRDataBuffer? instanceTransformBuffer,
        bool useInstanceTransformBuffer,
        string consumer)
    {
        if (!program.HasUniform("XRE_DrawMetadataBufferAddress"))
            return true;

        Debug.RenderingWarningEvery(
            $"RenderDispatch.SceneDatabaseBda.UnsupportedBackend.{consumer}",
            TimeSpan.FromSeconds(2),
            "[RenderDispatch] Scene-database buffer-device-address shader '{0}' is active on a backend without buffer-device-address support.",
            consumer);
        return false;
    }
}
