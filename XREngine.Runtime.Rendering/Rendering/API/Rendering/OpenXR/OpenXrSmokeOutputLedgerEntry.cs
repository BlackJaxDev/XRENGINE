namespace XREngine.Rendering.API.Rendering.OpenXR;

public struct OpenXrSmokeOutputLedgerEntry
{
    public int RetainedIndex { get; set; }
    public ulong ManifestFrameId { get; set; }
    public ulong RenderFrameId { get; set; }
    public ulong OutputId { get; set; }
    public ulong ViewFamilyId { get; set; }
    public string? OutputKind { get; set; }
    public string? ViewKind { get; set; }
    public string? OutputClass { get; set; }
    public string? Name { get; set; }
    public string? PipelineName { get; set; }
    public int PipelineInstanceId { get; set; }
    public ulong ResourcePlanGeneration { get; set; }
    public ulong CommandGeneration { get; set; }
    public string? AntiAliasingMode { get; set; }
    public string? TargetClass { get; set; }
    public ulong StableTargetId { get; set; }
    public ulong TargetGeneration { get; set; }
    public uint DisplayWidth { get; set; }
    public uint DisplayHeight { get; set; }
    public uint InternalWidth { get; set; }
    public uint InternalHeight { get; set; }
    public uint LayerCount { get; set; }
    public uint ViewMask { get; set; }
    public int ExternalImageSlot { get; set; }
    public ulong TargetCompatibilityKey { get; set; }
    public bool Active { get; set; }
    public bool Rendered { get; set; }
    public bool SceneRendered { get; set; }
    public bool RenderPhaseSceneRendered { get; set; }
    public bool LifetimeValidationPassed { get; set; }
    public bool SubmitObserved { get; set; }
    public bool FinalWriteObserved { get; set; }
    public bool PresentObserved { get; set; }
    public bool PresentAccepted { get; set; }
    public string? PresentResult { get; set; }
    public bool Due { get; set; }
    public bool Skipped { get; set; }
    public string? WorkDisposition { get; set; }
    public uint ContentAgeFrames { get; set; }
    public bool PolicyAuthorized { get; set; }
    public int CommandCount { get; set; }
    public int DrawCalls { get; set; }
    public double SubmitCpuMilliseconds { get; set; }
    public double PresentCpuMilliseconds { get; set; }
}
