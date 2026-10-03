namespace XREngine.Scene.Transforms;

public static partial class TransformFactoryRegistry
{
    static partial void RegisterPlatformTransforms()
        => Register<RigidBodyTransform>(static () => new RigidBodyTransform());
}
