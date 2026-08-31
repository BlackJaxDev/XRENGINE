namespace XREngine.Rendering.Vulkan;

/// <summary>Exact explicit-production checkpoint at which a one-shot buffer growth is requested.</summary>
public enum EVulkanExplicitProductionBufferStressCheckpoint
{
    AfterLogicalSeal,
    AfterNativeRecording,
}
