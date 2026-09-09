namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies whether a diagnostic checkpoint brackets the beginning or end of an operation.</summary>
internal enum EVulkanNvCheckpointPhase : byte
{
    Before,
    After,
}
