namespace XREngine.Rendering.API.Rendering.OpenXR;

public sealed class OpenXrSmokeLifecycleFaultSnapshot
{
    public OpenXrSmokeLifecycleFaultStage Stage { get; set; }
    public bool Armed { get; set; }
    public bool Consumed { get; set; }
    public long TriggerFrame { get; set; }
    public long LifecycleEpoch { get; set; }
    public int ObservedRetiredGenerationCount { get; set; }
    public int ObservedGenerationCapacity { get; set; }
    public string? ObservedAdmission { get; set; }
    public string? Source { get; set; }
    public bool PendingWorkObserved { get; set; }
    public int PendingWorkCount { get; set; }
    public string? PendingWorkReceiptSource { get; set; }
    public bool TerminalHoldReleased { get; set; }
    public string? Diagnostic { get; set; }
}
