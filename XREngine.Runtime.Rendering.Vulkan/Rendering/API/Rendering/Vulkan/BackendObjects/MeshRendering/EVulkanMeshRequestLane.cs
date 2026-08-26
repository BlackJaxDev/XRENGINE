namespace XREngine.Rendering.Vulkan;

/// <summary>Independent fixed-capacity lane for immutable mesh requests.</summary>
internal enum EVulkanMeshRequestLane : byte
{
    TerminalComposition,
    Ui,
    MainScene,
    Shadow,
}
