namespace XREngine.Components;

public delegate void WorldTick();

public enum ETickGroup
{
    Normal,
    Late,
    PrePhysics,
    DuringPhysics,
    PostPhysics,
}

public enum ETickOrder
{
    Timers = 0,
    Input = 200000,
    Animation = 400000,
    Logic = 600000,
    Scene = 800000,
}
