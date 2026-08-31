namespace XREngine.Rendering.Vulkan;

/// <summary>Outcome of preparing exactly one bounded imported-texture chunk.</summary>
internal enum EVulkanImportedTextureChunkPreparation
{
    Prepared,
    Deferred,
    Failed,
}
