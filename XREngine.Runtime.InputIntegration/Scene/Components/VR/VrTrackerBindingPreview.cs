using XREngine.Components.Animation;
using XREngine.Data.Components.Scene;

namespace XREngine.Components.VR;

/// <summary>A current proximity assignment shown to the player before capture.</summary>
public readonly record struct VrTrackerBindingPreview(VRTrackerTransform Tracker, EHumanoidIKTarget? Slot);
