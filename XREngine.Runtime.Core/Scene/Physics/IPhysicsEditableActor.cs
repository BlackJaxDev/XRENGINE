using System.Numerics;

namespace XREngine.Scene.Physics;

/// <summary>Optional live pose editing for physics actors in authoring tools.</summary>
public interface IPhysicsEditableActor : IAbstractRigidPhysicsActor
{
    bool IsAvailable { get; }
    void SetEditorPose(Vector3 position, Quaternion rotation);
}
