using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Captures a panel slot before the source closes its dock tab.</summary>
internal readonly record struct NativeDockClosedSlotReceipt(
    string Panel,
    SceneNode? Slot,
    TransformBase? Transform,
    ObjectConstructionIdentity? SlotIdentity,
    ObjectConstructionIdentity? TransformIdentity,
    ObjectConstructionIdentity RootIdentity,
    ObjectConstructionIdentity RegistrationIdentity,
    ulong RegistrationRevision,
    ulong ExpectedDockRevision)
{
    internal static NativeDockClosedSlotReceipt Capture(string panel, SceneNode? slot,
        SceneNode root, NativeDockWindowRegistration registration, ulong expectedDockRevision)
    {
        TransformBase? transform = slot?.Transform;
        return new(panel, slot, transform,
            slot is null ? null : ObjectConstructionIdentity.Capture(slot),
            transform is null ? null : ObjectConstructionIdentity.Capture(transform),
            ObjectConstructionIdentity.Capture(root),
            ObjectConstructionIdentity.Capture(registration),
            registration.RegistrationRevision, expectedDockRevision);
    }
}
