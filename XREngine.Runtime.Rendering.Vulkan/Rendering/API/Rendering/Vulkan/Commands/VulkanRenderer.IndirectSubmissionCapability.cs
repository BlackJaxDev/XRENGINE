using System.Numerics;

namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer :
    IIndirectDrawStateBackendCapability,
    ISceneDatabaseDeviceAddressBackendCapability
{
    bool IIndirectDrawStateBackendCapability.TryBeginIndirectDrawState(
        XRRenderProgram program,
        XRMaterial? material,
        in Matrix4x4 modelMatrix,
        out IndirectDrawStateToken token)
        => _commandRuntime.TryBeginIndirectDrawState(program, material, modelMatrix, out token);

    void IIndirectDrawStateBackendCapability.EndIndirectDrawState(in IndirectDrawStateToken token)
        => _commandRuntime.EndIndirectDrawState(token);

    bool ISceneDatabaseDeviceAddressBackendCapability.TryBindSceneDatabaseDeviceAddressUniforms(
        XRRenderProgram program,
        XRDataBuffer drawMetadataBuffer,
        XRDataBuffer? instanceTransformBuffer,
        bool useInstanceTransformBuffer,
        string consumer)
        => VulkanSceneDatabaseAddressBindingService.TryBind(
            BackendObjectContext,
            ResourceRuntime.Buffers,
            program,
            drawMetadataBuffer,
            instanceTransformBuffer,
            useInstanceTransformBuffer,
            consumer);
}
