using XREngine.Rendering.Profiling;

namespace XREngine.RenderBench;

public sealed record RenderBenchInputManifest(
    bool WorldLoaded,
    string SceneIdentity,
    string CameraIdentity,
    string[] LightIdentities,
    string AnimationIdentity,
    double FinalSimulationTimeSeconds,
    double FixedStepSeconds,
    int RandomSeed,
    RenderProfileMeshStrategy MeshStrategy,
    string[] RenderFeatures,
    RenderProfileStereoMode StereoMode,
    string[] OutputIdentities);
