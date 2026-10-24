using XREngine.Data.Profiling;

namespace XREngine.Editor;

internal readonly record struct NativeGpuTimingTraversalFrame(
    GpuPipelineTimingNodeData[] Nodes,
    int NextIndex,
    int Depth);
