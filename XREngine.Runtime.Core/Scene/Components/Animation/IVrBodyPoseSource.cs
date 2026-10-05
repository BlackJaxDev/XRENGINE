using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Optional body estimates in world space for the same simulation snapshot as tracked devices.
/// The player samples estimates before room-scale recentering and rebases them with that publication.
/// </summary>
public interface IVrBodyPoseSource
{
    bool TryGetTarget(EHumanoidIKTarget slot, long snapshotId, out Matrix4x4 targetWorld);
    void ResetHistory();
}
