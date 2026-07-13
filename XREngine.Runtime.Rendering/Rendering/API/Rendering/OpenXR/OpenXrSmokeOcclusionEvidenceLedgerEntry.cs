namespace XREngine.Rendering.API.Rendering.OpenXR;

public struct OpenXrSmokeOcclusionEvidenceLedgerEntry
{
    public int RetainedIndex { get; set; }
    public ulong RenderFrameId { get; set; }
    public int RenderPass { get; set; }
    public string Scope { get; set; }
    public int ViewId { get; set; }
    public int PipelineInstanceId { get; set; }
    public ulong OutputId { get; set; }
    public int PovId { get; set; }
    public uint CoverageMask { get; set; }
    public uint RequiredCoverageMask { get; set; }
    public int DeclaredViewCount { get; set; }
    public int ResourceGeneration { get; set; }
    public uint StableQueryKey { get; set; }
    public string Role { get; set; }
    public string Mode { get; set; }
    public bool CandidateObserved { get; set; }
    public bool Rendered { get; set; }
    public bool Culled { get; set; }
    public uint OcclusionProofCoverageMask { get; set; }
    public bool HasDecision { get; set; }
    public string Decision { get; set; }
}
