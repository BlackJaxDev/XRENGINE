using System.Runtime.CompilerServices;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// Publishes the pass order of the OpenGL renderer to tools. OpenGL executes passes in the order that the command
/// chain records them, so the plan is the recorded order of the newest frame of each pipeline instance.
/// </summary>
public partial class OpenGLRenderer : IRenderGraphPlanDiagnosticsSource
{
    private readonly ConditionalWeakTable<XRRenderPipelineInstance, OpenGLPlanDiagnosticsEntry> _planSnapshots = new();

    /// <inheritdoc />
    public override IRenderGraphPlanDiagnosticsSource? PlanDiagnostics => this;

    /// <inheritdoc />
    public RenderGraphPlanSnapshot? GetPlanSnapshot(XRRenderPipelineInstance? instance)
    {
        if (instance is null)
            return null;

        RenderPipelineExecutionTrace trace = instance.ExecutionTrace;
        if (!trace.HasRecords)
            return null;

        IReadOnlyCollection<RenderPassMetadata>? metadata = instance.ActiveGeneration?.PassMetadata ?? instance.Pipeline?.PassMetadata;
        int version = trace.Version;
        lock (_planSnapshots)
        {
            if (_planSnapshots.TryGetValue(instance, out OpenGLPlanDiagnosticsEntry? cached) &&
                cached.TraceVersion == version &&
                ReferenceEquals(cached.Metadata, metadata))
            {
                return cached.Snapshot;
            }

            RenderGraphPlanSnapshot snapshot = RenderGraphRecordedPlanner.Plan(
                EPlanSnapshotOrigin.OpenGL,
                metadata,
                trace.LastExecutedPassOrder,
                (ulong)(uint)version);
            _planSnapshots.AddOrUpdate(instance, new OpenGLPlanDiagnosticsEntry(version, metadata, snapshot));
            return snapshot;
        }
    }
}
