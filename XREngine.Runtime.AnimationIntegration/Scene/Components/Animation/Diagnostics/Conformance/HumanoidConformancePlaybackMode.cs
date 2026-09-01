namespace XREngine.Components.Animation;

/// <summary>Playback route represented by a conformance matrix row.</summary>
public enum HumanoidConformancePlaybackMode
{
    DirectClip,
    StateMachine,
    Transition,
    InterruptedTransition,
    BlendTree1D,
    BlendTree2D,
    DirectBlendTree,
}
