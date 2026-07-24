namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer : IMaterialTableBackendCapability
{
    bool IMaterialTableBackendCapability.SupportsBufferDeviceAddress => SupportsBufferDeviceAddress;
    bool IMaterialTableBackendCapability.SupportsBindlessMaterialTable
        => BindlessMaterialCapability.Tier >= EVulkanBindlessMaterialCapabilityTier.BindlessMaterialTableShaderReady;
    bool IMaterialTableBackendCapability.SupportsBindlessTextureHandles => false;
    string IMaterialTableBackendCapability.BindlessMaterialUnavailableReason
    {
        get
        {
            VulkanBindlessMaterialCapability capability = BindlessMaterialCapability;
            return $"Vulkan capability tier={capability.Tier}, mode={capability.Mode}, reason='{capability.Reason}'.";
        }
    }

    bool IMaterialTableBackendCapability.TryEnsureMaterialTextureTable(out string reason)
        => TryEnsureGlobalMaterialTextureDescriptorTable(out reason);

    bool IMaterialTableBackendCapability.TryResolveMaterialTextureReference(
        XRTexture texture,
        string semantic,
        out Materials.GPUMaterialTextureReference reference)
    {
        if (TryGetOrCreateMaterialTextureDescriptorIndex(
            texture,
            semantic,
            out uint descriptorIndex,
            out _))
        {
            reference = Materials.GPUMaterialTextureReference.FromVulkanDescriptorIndex(descriptorIndex);
            return true;
        }

        reference = Materials.GPUMaterialTextureReference.None;
        return false;
    }

    void IMaterialTableBackendCapability.FlushMaterialTextureTableUpdates()
        => FlushGlobalMaterialTextureDescriptorUpdates();

    void IMaterialTableBackendCapability.ReleaseMaterialTextureReference(
        in Materials.GPUMaterialRetiredHandle retired)
    {
    }

    bool IMaterialTableBackendCapability.BeginGlobalMaterialTextureDescriptorScope(
        XRRenderProgram program,
        string consumer)
        => BeginGlobalMaterialTextureDescriptorScope(program, consumer);

    void IMaterialTableBackendCapability.EndGlobalMaterialTextureDescriptorScope(XRRenderProgram program)
        => EndGlobalMaterialTextureDescriptorScope(program);
}
