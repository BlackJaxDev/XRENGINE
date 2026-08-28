namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable current-frame participant in a stable bin. It carries the exact
/// resident address, coalesced execution key, and late resource-use range; no
/// worker may re-resolve a template or inspect authoring state.
/// </summary>
internal readonly record struct VulkanPreparedStableBinRecord(
    VulkanRenderBinKey Key,
    VulkanResidentDrawTemplateHandle Template,
    int IngressIndex,
    int LateResourceUseOffset,
    int LateResourceUseCount,
    VulkanTemplateResourceManifest TemplateManifest,
    int VisibilityPayloadIndex = -1,
    VulkanPreparedVisibilityDirectDraw VisibilityDirectDraw = default,
    uint VisibilityMaterialIndex = 0u,
    uint VisibilityObjectIndex = 0u,
    VulkanResidentDrawTemplateNativeState VisibilityNativeState = default,
    VulkanVisibilityGeometryRecordClosure VisibilityGeometryClosure = default);
