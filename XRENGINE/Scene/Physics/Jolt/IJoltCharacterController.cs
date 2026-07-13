namespace XREngine.Scene.Physics.Jolt
{
    public interface IJoltCharacterController : IAbstractCharacterController
    {
        bool CollidingSides { get; }
        void ConsumeInputBuffer(float fixedDelta);
    }
}
