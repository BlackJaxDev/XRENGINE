namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Typed outcome for best-effort advanced-scene native realization. During
/// dual-feed these outcomes leave the ordered legacy draw path selected.
/// </summary>
internal enum EVulkanAdvancedSceneResourceFailure : byte
{
    None,
    RuntimeUnavailable,
    DescriptorIndexingUnavailable,
    DescriptorHeapUnsupported,
    InvalidFrameOwner,
    InvalidPublication,
    PublicationSnapshotUnavailable,
    DependencyManifestInconsistent,
    IncompleteSourceImage,
    FrameSlotStillInUse,
    PublicationCapacity,
    ReceiptCapacity,
    TextureDescriptorCapacity,
    SamplerDescriptorCapacity,
    SourceMismatch,
    TextureWrapperUnavailable,
    TextureDescriptorNotReady,
    UnsupportedTextureShape,
    UnsupportedSamplerState,
    SamplerCacheCapacity,
    NativeSamplerCreationFailed,
    FrameStorageCapacity,
    TransactionIntegrityFailure,
    DescriptorUpdateFailed,
    DeviceLost,
    NativeFault,
}
