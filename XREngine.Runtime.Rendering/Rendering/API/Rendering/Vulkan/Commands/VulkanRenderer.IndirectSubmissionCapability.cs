using System.Numerics;
using XREngine.Data.Vectors;

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
    {
        token = _pendingIndirectDrawState is { } previous
            ? new(previous.Program, previous.Material, previous.ModelMatrix, true)
            : default;

        if (material is null)
        {
            Debug.RenderingWarningEvery(
                "RenderDispatch.VulkanIndirectDrawStateMissingMaterial",
                TimeSpan.FromSeconds(2),
                "[RenderDispatch] Vulkan indirect draw skipped because no material was provided for captured draw state.");
            return false;
        }

        _pendingIndirectDrawState = new(program, material, modelMatrix);
        return true;
    }

    void IIndirectDrawStateBackendCapability.EndIndirectDrawState(in IndirectDrawStateToken token)
        => _pendingIndirectDrawState = token.HadPreviousState
            ? new(token.PreviousProgram!, token.PreviousMaterial!, token.PreviousModelMatrix)
            : null;

    bool ISceneDatabaseDeviceAddressBackendCapability.TryBindSceneDatabaseDeviceAddressUniforms(
        XRRenderProgram program,
        XRDataBuffer drawMetadataBuffer,
        XRDataBuffer? instanceTransformBuffer,
        bool useInstanceTransformBuffer,
        string consumer)
    {
        if (!program.HasUniform("XRE_DrawMetadataBufferAddress"))
            return true;

        if (!SupportsBufferDeviceAddress)
        {
            Debug.RenderingWarningEvery(
                $"RenderDispatch.SceneDatabaseBda.Unsupported.{consumer}",
                TimeSpan.FromSeconds(2),
                "[RenderDispatch] Scene-database buffer-device-address shader '{0}' is active, but bufferDeviceAddress is unavailable.",
                consumer);
            return false;
        }

        program.Uniform("XRE_DrawMetadataCount", drawMetadataBuffer.ElementCount);
        if (!TryBindSceneDatabaseBufferDeviceAddress(
            program,
            drawMetadataBuffer,
            "XRE_DrawMetadataBufferAddress",
            consumer))
        {
            return false;
        }

        if (!useInstanceTransformBuffer || instanceTransformBuffer is null)
        {
            program.Uniform("XRE_TransformBufferAddress", new UVector2(0u, 0u));
            program.Uniform("XRE_TransformFloatCount", 0u);
            return true;
        }

        program.Uniform(
            "XRE_TransformFloatCount",
            instanceTransformBuffer.Length / (uint)sizeof(float));
        return TryBindSceneDatabaseBufferDeviceAddress(
            program,
            instanceTransformBuffer,
            "XRE_TransformBufferAddress",
            consumer);
    }

    private bool TryBindSceneDatabaseBufferDeviceAddress(
        XRRenderProgram program,
        XRDataBuffer buffer,
        string uniformName,
        string consumer)
    {
        if (GetOrCreateAPIRenderObject(buffer, generateNow: true) is not VkDataBuffer apiBuffer)
        {
            RecordSceneDatabaseDeviceAddressConsumer(
                buffer,
                0ul,
                consumer,
                consumed: false,
                "wrapper-unavailable");
            WarnSceneDatabaseDeviceAddressUnavailable(buffer, consumer, "wrapper-unavailable");
            return false;
        }

        apiBuffer.Generate();
        if (!apiBuffer.TryGetDeviceAddress(out ulong address) || address == 0ul)
        {
            apiBuffer.PushData();
            apiBuffer.TryGetDeviceAddress(out address);
        }

        bool consumed = address != 0ul;
        RecordSceneDatabaseDeviceAddressConsumer(
            buffer,
            address,
            consumer,
            consumed,
            consumed ? "resolved" : "address-unresolved");
        if (!consumed)
        {
            WarnSceneDatabaseDeviceAddressUnavailable(buffer, consumer, "address-unresolved");
            return false;
        }

        program.Uniform(
            uniformName,
            new UVector2((uint)(address & 0xFFFFFFFFul), (uint)(address >> 32)));
        return true;
    }

    private static void WarnSceneDatabaseDeviceAddressUnavailable(
        XRDataBuffer buffer,
        string consumer,
        string reason)
        => Debug.RenderingWarningEvery(
            $"RenderDispatch.SceneDatabaseBda.Unresolved.{consumer}.{buffer.AttributeName}.{reason}",
            TimeSpan.FromSeconds(2),
            "[RenderDispatch] Scene-database buffer-device-address consumer '{0}' cannot resolve buffer '{1}' ({2}); skipping this Vulkan prototype draw bucket instead of falling back silently.",
            consumer,
            buffer.AttributeName,
            reason);
}
