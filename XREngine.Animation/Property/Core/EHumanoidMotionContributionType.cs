namespace XREngine.Animation;

/// <summary>
/// Distinguishes absolute override motion from an additive Body/root delta.
/// The two domains are composed separately by the runtime avatar evaluator.
/// </summary>
public enum EHumanoidMotionContributionType : byte
{
    Override,
    Additive,
}
