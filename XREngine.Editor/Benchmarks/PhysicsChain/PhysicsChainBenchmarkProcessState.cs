namespace XREngine.Editor.Benchmarks.PhysicsChain;

public readonly record struct PhysicsChainBenchmarkProcessState(
    bool DebuggerAttached,
    bool ValidationLayersEnabled,
    bool VerbosePerChainLoggingEnabled,
    bool DebugDrawingEnabled,
    bool EditorOnlyInstrumentationEnabled);
