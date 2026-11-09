namespace XREngine.Data.Runtime.Ecs;

public enum RuntimeSystemPhase
{
    InputSample,
    NetworkReceive,
    Prediction,
    AuthoritativeSimulation,
    Animation,
    IK,
    PhysicsBridge,
    ReplicationBuild,
    Interpolation,
    RenderPrepare,
    GpuUpload,
    Diagnostics,
}
