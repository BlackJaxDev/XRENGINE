namespace XREngine.Components.Animation;

/// <summary>Features that a Phase 10 humanoid conformance corpus must exercise explicitly.</summary>
[Flags]
public enum HumanoidConformanceCapability
{
    None = 0,
    InPlace = 1 << 0,
    Translation = 1 << 1,
    Turn = 1 << 2,
    VerticalMotion = 1 << 3,
    NonLooping = 1 << 4,
    Mirroring = 1 << 5,
    LoopPose = 1 << 6,
    InverseKinematics = 1 << 7,
    NoInverseKinematics = 1 << 8,
    Tangents = 1 << 9,
    Events = 1 << 10,
    ObjectReferenceBindings = 1 << 11,
    Compressed = 1 << 12,
    Dense = 1 << 13,
    Streamed = 1 << 14,
    ExactSeek = 1 << 15,
    ReversePlayback = 1 << 16,
    SignedLoopEpochs = 1 << 17,
    StateMachine = 1 << 18,
    Transitions = 1 << 19,
    InterruptedTransitions = 1 << 20,
    BlendTree1D = 1 << 21,
    BlendTree2D = 1 << 22,
    DirectBlendTree = 1 << 23,
    RenameMoveInvariance = 1 << 24,
    FootContact = 1 << 25,
}
