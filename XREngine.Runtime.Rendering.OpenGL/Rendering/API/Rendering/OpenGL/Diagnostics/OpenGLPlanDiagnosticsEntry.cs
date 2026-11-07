using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Rendering.OpenGL;

/// <summary>A cached OpenGL plan snapshot and the trace version and metadata that it was built from.</summary>
internal sealed record OpenGLPlanDiagnosticsEntry(
    int TraceVersion,
    IReadOnlyCollection<RenderPassMetadata>? Metadata,
    RenderGraphPlanSnapshot Snapshot);
