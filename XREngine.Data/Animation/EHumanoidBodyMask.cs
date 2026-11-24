using System;

namespace XREngine.Components.Animation;

/// <summary>
/// Per-layer body-part mask that restricts which humanoid muscles, IK goals, root-motion
/// tracks, and non-humanoid slots an <c>AnimLayer</c> writes into the state machine.
/// </summary>
[Flags]
public enum EHumanoidBodyMask : uint
{
    /// <summary>Blocks all channels on the layer.</summary>
    None = 0u,

    /// <summary>Humanoid root motion contributions (Body translation and rotation).</summary>
    Root = 1u << 0,

    /// <summary>Torso muscles (spine, chest, upper chest).</summary>
    Body = 1u << 1,

    /// <summary>Neck and head muscles.</summary>
    Head = 1u << 2,

    /// <summary>Left and right eye look muscles.</summary>
    Eyes = 1u << 3,

    /// <summary>Jaw open and lateral muscles.</summary>
    Jaw = 1u << 4,

    /// <summary>Left shoulder, upper arm, forearm, wrist, and left-hand IK channels.</summary>
    LeftArm = 1u << 5,

    /// <summary>Right shoulder, upper arm, forearm, wrist, and right-hand IK channels.</summary>
    RightArm = 1u << 6,

    /// <summary>Left-hand finger stretch and spread muscles.</summary>
    LeftFingers = 1u << 7,

    /// <summary>Right-hand finger stretch and spread muscles.</summary>
    RightFingers = 1u << 8,

    /// <summary>Left upper leg, lower leg, foot, toes, and left-foot IK channels.</summary>
    LeftLeg = 1u << 9,

    /// <summary>Right upper leg, lower leg, foot, toes, and right-foot IK channels.</summary>
    RightLeg = 1u << 10,

    /// <summary>Non-humanoid animated properties such as blendshapes, materials, and generic transforms.</summary>
    NonHumanoid = 1u << 11,

    /// <summary>Upper body excluding root motion and legs.</summary>
    UpperBody = Body | Head | Eyes | Jaw | LeftArm | RightArm | LeftFingers | RightFingers,

    /// <summary>Root motion and both legs.</summary>
    LowerBody = Root | LeftLeg | RightLeg,

    /// <summary>Finger channels for both hands.</summary>
    HandsOnly = LeftFingers | RightFingers,

    /// <summary>Head, eyes, and jaw channels.</summary>
    FaceOnly = Head | Eyes | Jaw,

    /// <summary>Allows every humanoid body part, root motion, and non-humanoid channel.</summary>
    All = Root | Body | Head | Eyes | Jaw | LeftArm | RightArm | LeftFingers | RightFingers | LeftLeg | RightLeg | NonHumanoid,
}
