using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>Captures one compiled shader stage for an immutable interface key.</summary>
internal readonly record struct VulkanProgramInterfaceStage(
    EProgramStageMask Slot,
    XRShader Shader,
    VkShader Wrapper,
    long SourceRevision,
    ulong ModuleHandle,
    string ArtifactIdentity,
    string EntryPoint);
