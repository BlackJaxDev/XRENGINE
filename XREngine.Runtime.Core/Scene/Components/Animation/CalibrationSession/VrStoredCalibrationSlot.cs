using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>Value-only committed binding; physical identity is provider-scoped, never a role, device index, or scene-node reference.</summary>
public readonly record struct VrStoredCalibrationSlot(EHumanoidIKTarget Slot, string PhysicalIdentity, Matrix4x4 Offset);
