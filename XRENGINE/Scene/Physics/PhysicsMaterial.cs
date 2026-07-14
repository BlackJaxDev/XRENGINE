using XREngine.Data.Core;

namespace XREngine.Scene.Physics;

public enum PhysicsMaterialCombineMode
{
    Average,
    Min,
    Multiply,
    Max,
}

/// <summary>
/// Backend-neutral runtime material contract. Native backends provide concrete adapters.
/// </summary>
public abstract class AbstractPhysicsMaterial : XRBase
{
    public abstract float StaticFriction { get; set; }
    public abstract float DynamicFriction { get; set; }
    public abstract float Restitution { get; set; }
    public abstract float Damping { get; set; }

    public abstract PhysicsMaterialCombineMode FrictionCombineMode { get; set; }
    public abstract PhysicsMaterialCombineMode RestitutionCombineMode { get; set; }

    public abstract bool DisableFriction { get; set; }
    public abstract bool DisableStrongFriction { get; set; }
    public abstract bool ImprovedPatchFriction { get; set; }
    public abstract bool CompliantContact { get; set; }
}
