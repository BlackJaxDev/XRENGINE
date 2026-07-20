using XREngine.Scene.Physics;

namespace XREngine.Components.Physics;

/// <summary>Contact flags reported by a character controller after a physics step.</summary>
public readonly record struct CharacterControllerContactState(
    bool CollidingUp,
    bool CollidingDown,
    bool CollidingSides,
    CharacterSupportState SupportState);