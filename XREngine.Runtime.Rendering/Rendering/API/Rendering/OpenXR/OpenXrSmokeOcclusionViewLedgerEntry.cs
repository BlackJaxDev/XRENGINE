using XREngine.Rendering.Occlusion;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public struct OpenXrSmokeOcclusionViewLedgerEntry
{
    public int RetainedIndex { get; set; }
    public int RenderPass { get; set; }
    public EOcclusionViewScope Scope { get; set; }
    public int ViewId { get; set; }
    public int PipelineInstanceId { get; set; }
    public ulong OutputId { get; set; }
    public int PovId { get; set; }
    public uint CoverageMask { get; set; }
    public uint RequiredCoverageMask { get; set; }
    public int DeclaredViewCount { get; set; }
    public int ResourceGeneration { get; set; }
    public int CandidateCount { get; set; }
    public int Submissions { get; set; }
    public int Resolutions { get; set; }
    public int Skips { get; set; }
    public int BudgetSkipped { get; set; }
    public int ForcedVisible { get; set; }
    public int RecoveryStarts { get; set; }
    public int RecoveryCompletions { get; set; }
    public int CurrentRecoveryAgeFrames { get; set; }
    public int MaxRecoveryAgeFrames { get; set; }
    public int CurrentResultAgeFrames { get; set; }
    public int MaxResultAgeFrames { get; set; }
    public int RecoveryLatencyFrames { get; set; }
}
