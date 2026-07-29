using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Aggregate compute specialization family. Per-job material and renderer
/// identity never participate in this key.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedDeformationDispatchKey(
    ulong VertexLayoutId,
    EAdvancedDeformationPrecision Precision,
    EAdvancedDeformationFeatureFlags ShaderFeatures);
