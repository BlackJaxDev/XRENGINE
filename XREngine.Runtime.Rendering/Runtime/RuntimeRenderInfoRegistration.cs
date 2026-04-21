using XREngine;

namespace XREngine.Rendering;

public interface IRuntimeRenderInfo2DRegistrationItem
{
}

public interface IRuntimeRenderInfo3DRegistrationItem
{
}

public interface IRuntimeRenderInfo2DRegistrationTarget
{
    void AddRenderable2D(IRuntimeRenderInfo2DRegistrationItem renderable);
    void RemoveRenderable2D(IRuntimeRenderInfo2DRegistrationItem renderable);
}

public interface IRuntimeRenderInfo3DRegistrationTarget : IRuntimeWorldContext
{
    void AddRenderable3D(IRuntimeRenderInfo3DRegistrationItem renderable);
    void RemoveRenderable3D(IRuntimeRenderInfo3DRegistrationItem renderable);
}