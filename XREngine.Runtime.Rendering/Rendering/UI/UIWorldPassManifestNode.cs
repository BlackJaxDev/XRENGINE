using XREngine.Rendering.Pipelines.Commands;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.UI;

internal struct UIWorldPassManifestNode
{
    internal ViewportRenderCommand Command;
    internal ViewportRenderCommandContainer Container;
    internal int ContainerIndex;
    internal int ContainerCommandCount;
    internal int ParentBranchIndex;
    internal bool ParentBranchValue;
    internal VPRC_IfElse? Branch;
    internal Func<bool>? Condition;
    internal ViewportRenderCommandContainer? TrueCommands;
    internal ViewportRenderCommandContainer? FalseCommands;
    internal int TrueCommandCount;
    internal int FalseCommandCount;
    internal VPRC_RenderMeshesPassShared? Pass;
    internal int RenderPass;
    internal EMeshRenderingPathIntent PathIntent;
    internal EMeshSubmissionStrategy MeshSubmissionStrategy;
    internal bool PreserveMeshSubmissionStrategy;
    internal bool EnforceAdvancedLatePassEligibility;
    internal string? RenderGraphPassName;
    internal bool ShouldExecute;
    internal bool ExecuteInShadowPass;
    internal int ExpectedUiDraws;
    internal EUIWorldPassNodeState State;
}
