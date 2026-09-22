using XREngine.Rendering.GI.DDGI;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Associates DDGI dispatches with their declared render-graph pass on every backend.</summary>
public abstract class VPRC_DDGIComputePass : ViewportRenderCommand
{
    private int _passIndex = int.MinValue;
    private string? _passName;
    private string? _allocationScopeName;
    protected string PassName => _passName ??= GetType().Name;
    private string AllocationScopeName => _allocationScopeName ??= $"DDGI.{PassName}";

    protected sealed override void Execute()
    {
        if (_passIndex == int.MinValue && ParentPipeline?.PassMetadata is { } metadata)
            foreach (RenderPassMetadata pass in metadata)
                if (pass.Name == PassName)
                {
                    _passIndex = pass.PassIndex;
                    break;
                }
        if (_passIndex == int.MinValue)
        {
            Debug.RenderingWarningEvery("DDGI.MissingPassMetadata", TimeSpan.FromSeconds(2),
                "DDGI cannot submit '{0}' because its render-graph pass is missing.", PassName);
            return;
        }
        // Track only the executed command. Setup, skipped commands, and MCP
        // diagnostics remain outside this rolling, backend-neutral sample.
#if !XRE_PUBLISHED
        using var allocationScope = RuntimeRenderingHostServices.Profiling.EnableThreadAllocationTracking
            ? DDGIManagedAllocationDiagnostics.Begin(AllocationScopeName)
            : default;
#endif
        using var scope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(_passIndex);
        ExecuteDDGI();
    }

    protected abstract void ExecuteDDGI();

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        => context.GetOrCreateSyntheticPass(PassName, ERenderGraphPassStage.Compute);
}
