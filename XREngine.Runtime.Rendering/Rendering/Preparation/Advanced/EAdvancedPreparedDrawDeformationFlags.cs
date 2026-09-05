namespace XREngine.Rendering;

[Flags]
public enum EAdvancedPreparedDrawDeformationFlags : uint
{
    None = 0,
    Active = 1u << 0,
    PreviousValid = 1u << 1,
    /// <summary>Marks a relation-checked temporal sidecar for this draw row.</summary>
    TemporalStatePresent = 1u << 2,
    TemporalReasonMask = AdvancedReconstructionTemporalFlags.VelocityReasonMask,
}
