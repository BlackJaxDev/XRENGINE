namespace XREngine.Components;

/// <summary>Execution pool measured by delayed quality-controller feedback.</summary>
public enum PhysicsChainQualityFeedbackBackend : byte
{
    Cpu,
    Gpu,
}
