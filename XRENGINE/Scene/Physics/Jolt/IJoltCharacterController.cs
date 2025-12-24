using System.Numerics;

namespace XREngine.Scene.Physics.Jolt
{
    public interface IJoltCharacterController : IAbstractRigidPhysicsActor
    {
        Vector3 Position { get; set; }
        Vector3 FootPosition { get; set; }
        Vector3 UpDirection { get; set; }

        float Radius { get; set; }
        float Height { get; set; }
        float SlopeLimit { get; set; }
        float StepOffset { get; set; }
        float ContactOffset { get; set; }

        bool CollidingUp { get; }
        bool CollidingDown { get; }
        bool CollidingSides { get; }

        void Move(Vector3 delta, float minDist, float elapsedTime);
        void Resize(float height);

        void ConsumeInputBuffer(float fixedDelta);
        void RequestRelease();
    }
}
