namespace XREngine.Components;

/// <summary>Authored behavior when an automatic-quality chain is not visible.</summary>
public enum PhysicsChainOffscreenBehavior : byte
{
    AutomaticByImportance,
    Simulate,
    DecayThenSleep,
    SleepImmediately,
}
