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

/// <summary>
/// Focused visual-scene publication capability. It is intentionally separate from
/// <see cref="IRuntimeWorldContext"/> so Core objects never acquire a Rendering identity.
/// </summary>
public interface IRuntimeRenderInfo3DRegistrationTarget
{
    /// <summary>The Core world identity receiving this visual publication.</summary>
    IRuntimeWorldContext WorldContext
        => throw new NotSupportedException("Legacy render registration targets do not expose a Core world context.");
    void AddRenderable3D(IRuntimeRenderInfo3DRegistrationItem renderable);
    void RemoveRenderable3D(IRuntimeRenderInfo3DRegistrationItem renderable);
}
