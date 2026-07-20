namespace XREngine.Scene.Physics.Jolt
{
    public interface IJoltCharacterController : IAbstractCharacterController, IAdvancedCharacterControllerSettings, ICharacterControllerCollisionSettings
    {
        void ConsumeInputBuffer(float fixedDelta);
    }
}
