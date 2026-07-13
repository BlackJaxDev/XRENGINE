using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Components.Physics;

/// <summary>
/// Backend-neutral configuration for a transient runtime distance constraint.
/// </summary>
public readonly record struct RuntimeDistanceConstraintSettings(
    float MinDistance,
    float MaxDistance,
    bool EnableMinDistance,
    bool EnableMaxDistance,
    float Stiffness,
    float Damping,
    float Tolerance,
    bool EnableCollision = false,
    bool EnablePreprocessing = true,
    float BreakForce = float.MaxValue,
    float BreakTorque = float.MaxValue);

/// <summary>
/// Owns the complete create, configure, and release lifecycle of one transient distance
/// constraint. Components use this owner instead of retaining backend-created joints directly.
/// </summary>
public sealed class RuntimeDistanceConstraintOwner : IDisposable
{
    private AbstractPhysicsScene? _scene;
    private IAbstractDistanceJoint? _constraint;

    /// <summary>
    /// The currently owned constraint, or <see langword="null"/> when unbound.
    /// </summary>
    public IAbstractDistanceJoint? Constraint => _constraint;

    /// <summary>
    /// Whether this owner currently has a live distance constraint.
    /// </summary>
    public bool IsBound => _constraint is not null;

    /// <summary>
    /// Releases any previous constraint, creates a new one, and applies all portable settings.
    /// </summary>
    public IAbstractDistanceJoint Bind(
        AbstractPhysicsScene scene,
        IAbstractPhysicsActor? actorA,
        JointAnchor localFrameA,
        IAbstractPhysicsActor? actorB,
        JointAnchor localFrameB,
        in RuntimeDistanceConstraintSettings settings)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Release();

        IAbstractDistanceJoint constraint = scene.CreateDistanceJoint(
            actorA,
            localFrameA,
            actorB,
            localFrameB);
        try
        {
            ApplySettings(constraint, settings);
            _scene = scene;
            _constraint = constraint;
            return constraint;
        }
        catch
        {
            scene.RemoveJoint(constraint);
            throw;
        }
    }

    /// <summary>
    /// Applies a complete settings snapshot to the currently owned constraint.
    /// </summary>
    public void Configure(in RuntimeDistanceConstraintSettings settings)
    {
        IAbstractDistanceJoint constraint = _constraint
            ?? throw new InvalidOperationException("Cannot configure an unbound distance constraint owner.");
        ApplySettings(constraint, settings);
    }

    /// <summary>
    /// Removes the owned constraint from its scene and releases its native resources.
    /// This operation is idempotent.
    /// </summary>
    public bool Release()
    {
        IAbstractDistanceJoint? constraint = _constraint;
        if (constraint is null)
            return false;

        AbstractPhysicsScene? scene = _scene;
        _constraint = null;
        _scene = null;

        if (scene is not null)
            scene.RemoveJoint(constraint);
        else
            constraint.Release();
        return true;
    }

    public void Dispose()
        => Release();

    private static void ApplySettings(
        IAbstractDistanceJoint constraint,
        in RuntimeDistanceConstraintSettings settings)
    {
        constraint.MinDistance = settings.MinDistance;
        constraint.MaxDistance = settings.MaxDistance;
        constraint.Stiffness = settings.Stiffness;
        constraint.Damping = settings.Damping;
        constraint.Tolerance = settings.Tolerance;
        constraint.EnableMinDistance = settings.EnableMinDistance;
        constraint.EnableMaxDistance = settings.EnableMaxDistance;
        constraint.EnableCollision = settings.EnableCollision;
        constraint.EnablePreprocessing = settings.EnablePreprocessing;
        constraint.BreakForce = settings.BreakForce;
        constraint.BreakTorque = settings.BreakTorque;
    }
}
