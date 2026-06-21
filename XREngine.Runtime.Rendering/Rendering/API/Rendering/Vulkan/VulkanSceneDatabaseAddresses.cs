using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private static readonly HashSet<string> SceneDatabaseDeviceAddressBuffers = new(StringComparer.Ordinal)
    {
        "DrawMetadataBuffer",
        "TransformBuffer",
        "PrevTransformBuffer",
        "BoundsBuffer",
        "SkinningPaletteBuffer",
        "MaterialStateBuffer",
        "MaterialTable",
        "MaterialTextureHandleTable",
    };

    internal bool ShouldEnableDeviceAddressForSceneDatabaseBuffer(XRDataBuffer buffer)
        => !SupportsBufferDeviceAddress ? false : IsSceneDatabaseDeviceAddressCandidate(buffer);

    internal bool IsSceneDatabaseDeviceAddressCandidate(XRDataBuffer buffer)
        => buffer.Target != EBufferTarget.ShaderStorageBuffer ? false : SceneDatabaseDeviceAddressBuffers.Contains(buffer.AttributeName);

    internal string ResolveSceneDatabaseDeviceAddressStatus(XRDataBuffer buffer, ulong resolvedAddress)
    {
        if (!IsSceneDatabaseDeviceAddressCandidate(buffer))
            return "not-scene-database-buffer";

        if (!SupportsBufferDeviceAddress)
            return "fallback-descriptor-buffer-device-address-unsupported";

        return resolvedAddress != 0ul
            ? "resolved-device-address"
            : "fallback-descriptor-address-unresolved";
    }

    internal void RecordSceneDatabaseDeviceAddressConsumer(
        XRDataBuffer buffer,
        ulong resolvedAddress,
        string consumer,
        bool consumed,
        string reason)
    {
        string status = ResolveSceneDatabaseDeviceAddressStatus(buffer, resolvedAddress);
        XRBufferWriteTelemetry.RecordDeviceAddressConsumer(consumed);
        if (consumed)
        {
            if (RenderDiagnosticsFlags.UploadStageLogging ||
                RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging)
            {
                Debug.Vulkan(
                    "[VkSceneDatabaseBDA] consumer={0} buffer='{1}' status={2} address=0x{3:X} bytes={4} reason={5}.",
                    consumer,
                    buffer.AttributeName,
                    status,
                    resolvedAddress,
                    buffer.Length,
                    reason);
            }

            return;
        }

        Debug.VulkanWarningEvery(
            $"VkSceneDatabaseBDA.Fallback.{consumer}.{buffer.AttributeName}.{reason}",
            TimeSpan.FromSeconds(2),
            "[VkSceneDatabaseBDA] consumer={0} buffer='{1}' consumed=false status={2} reason={3} supportsBda={4} bytes={5}.",
            consumer,
            buffer.AttributeName,
            status,
            reason,
            SupportsBufferDeviceAddress,
            buffer.Length);
    }
}
