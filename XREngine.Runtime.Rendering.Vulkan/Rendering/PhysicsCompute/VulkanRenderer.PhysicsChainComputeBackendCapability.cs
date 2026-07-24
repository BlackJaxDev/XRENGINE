using XREngine.Rendering.Compute;

namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer : IPhysicsChainComputeBackendFactoryCapability
{
    bool IPhysicsChainComputeBackendFactoryCapability.TryCreatePhysicsChainComputeBackend(
        out IPhysicsChainComputeBackend? backend)
        => VulkanPhysicsChainComputeBackend.TryCreate(this, out backend);
}
