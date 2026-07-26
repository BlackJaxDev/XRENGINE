using XREngine.Rendering.Compute;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IPhysicsChainComputeBackendFactoryCapability
{
    public bool TryCreatePhysicsChainComputeBackend(out IPhysicsChainComputeBackend? backend)
        => OpenGLPhysicsChainComputeBackend.TryCreate(this, out backend);
}
