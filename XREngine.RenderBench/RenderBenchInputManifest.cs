namespace XREngine.RenderBench;

public sealed record RenderBenchInputManifest(
    bool WorldLoaded,
    string WorldIdentity,
    string CameraIdentity,
    string AnimationIdentity,
    double FinalSimulationTimeSeconds,
    double FixedStepSeconds,
    int RandomSeed,
    bool FrozenWorld);
