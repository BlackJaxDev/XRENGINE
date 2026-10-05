using System.Numerics;
using XREngine.Components.Animation;

namespace XREngine.Components.VR;

/// <summary>A body landmark, or a limb segment when Start and End differ.</summary>
public readonly record struct VrTrackerBindingSlot(EHumanoidIKTarget Slot, Vector3 Start, Vector3 End);
