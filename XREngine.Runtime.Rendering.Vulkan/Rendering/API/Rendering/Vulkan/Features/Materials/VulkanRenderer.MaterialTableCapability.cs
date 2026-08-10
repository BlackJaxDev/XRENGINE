namespace XREngine.Rendering.Vulkan;

/// <summary>Composition boundary between the material system and descriptor services.</summary>
public partial class VulkanRenderer : IMaterialTableBackendCapability
{
    bool IMaterialTableBackendCapability.SupportsBufferDeviceAddress => SupportsBufferDeviceAddress;
    bool IMaterialTableBackendCapability.SupportsBindlessMaterialTable
        => ResourceRuntime.Descriptors.BindlessMaterialCapability.Tier >= EVulkanBindlessMaterialCapabilityTier.BindlessMaterialTableShaderReady;
    bool IMaterialTableBackendCapability.SupportsBindlessTextureHandles => false;
    string IMaterialTableBackendCapability.BindlessMaterialUnavailableReason
    {
        get
        {
            VulkanBindlessMaterialCapability capability = ResourceRuntime.Descriptors.BindlessMaterialCapability;
            return $"Vulkan capability tier={capability.Tier}, mode={capability.Mode}, reason='{capability.Reason}'.";
        }
    }
    bool IMaterialTableBackendCapability.TryEnsureMaterialTextureTable(out string reason) => ResourceRuntime.Descriptors.TryEnsureGlobalMaterialTextureDescriptorTable(out reason);
    Materials.MaterialTextureReferenceResolution IMaterialTableBackendCapability.ResolveMaterialTextureReference(XRTexture texture, string semantic) => ResourceRuntime.Descriptors.ResolveMaterialTextureDescriptorReference(texture, semantic);
    void IMaterialTableBackendCapability.FlushMaterialTextureTableUpdates() => ResourceRuntime.Descriptors.FlushGlobalMaterialTextureDescriptorUpdates();
    void IMaterialTableBackendCapability.ReleaseMaterialTextureReference(in Materials.GPUMaterialRetiredHandle retired) { }
    bool IMaterialTableBackendCapability.BeginGlobalMaterialTextureDescriptorScope(XRRenderProgram program, string consumer) => ResourceRuntime.Descriptors.BeginGlobalMaterialTextureDescriptorScope(program, consumer);
    void IMaterialTableBackendCapability.EndGlobalMaterialTextureDescriptorScope(XRRenderProgram program) => ResourceRuntime.Descriptors.EndGlobalMaterialTextureDescriptorScope(program);
}
