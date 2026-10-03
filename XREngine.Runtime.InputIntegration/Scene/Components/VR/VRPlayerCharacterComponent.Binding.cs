using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components.Animation;
using XREngine.Data.Components.Scene;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR;

public partial class VRPlayerCharacterComponent
{
    private static readonly EHumanoidIKTarget[] TrackerSlots =
    [
        EHumanoidIKTarget.Hips, EHumanoidIKTarget.Chest, EHumanoidIKTarget.LeftFoot, EHumanoidIKTarget.RightFoot,
        EHumanoidIKTarget.LeftElbow, EHumanoidIKTarget.RightElbow, EHumanoidIKTarget.LeftKnee, EHumanoidIKTarget.RightKnee,
    ];
    private readonly VRTrackerTransform?[] _proposedDevices = new VRTrackerTransform?[11];
    private readonly List<VrTrackerBindingPreview> _bindingPreview = new(16);
    private readonly List<(VRTrackerTransform Device, Matrix4x4 World)> _bindingSamples = new(16);
    private long _bindingSnapshot = -1;
    private long _bindingSampleTime;
    private bool _bindingSamplesCoherent;
    public IReadOnlyList<VrTrackerBindingPreview> TrackerBindingPreview => _bindingPreview;

    private VRTrackerTransform? FindTracker(string identity)
    {
        VRTrackerCollectionComponent? collection = GetTrackerCollection();
        if (collection is null) return null;
        foreach (var entry in collection.Trackers.Values)
            if (string.Equals(entry.Item2.SessionIdentity, identity, StringComparison.Ordinal)) return entry.Item2;
        return null;
    }

    /// <summary>Greedy global nearest-pair assignment with identity and slot tie breaks.</summary>
    private void UpdateBindingPreview()
    {
        Array.Clear(_proposedDevices);
        _bindingPreview.Clear();
        _bindingSamples.Clear();
        _bindingSnapshot = -1;
        _bindingSamplesCoherent = true;
        VRTrackerCollectionComponent? collection = GetTrackerCollection();
        if (collection is null || _humanoid is null) return;
        foreach (var entry in collection.Trackers.Values)
        {
            VRTrackerTransform tracker = entry.Item2;
            if (string.IsNullOrEmpty(tracker.SessionIdentity) ||
                !tracker.TryGetCurrentWorldPose(out Matrix4x4 pose, out long snapshot, out long sampleTime)) continue;
            if (_bindingSnapshot >= 0 && (snapshot != _bindingSnapshot || sampleTime != _bindingSampleTime))
                _bindingSamplesCoherent = false;
            _bindingSnapshot = snapshot;
            _bindingSampleTime = sampleTime;
            _bindingSamples.Add((tracker, pose));
        }
        float scale = (HeightScaleComponent as VRHeightScaleComponent)?.AvatarScale ?? 1.0f;
        float cutoff = (PlayerSettings?.TrackerBindingCutoff ?? CalibrationRadius) * scale;
        float cutoffSquared = cutoff * cutoff;
        if (_bindingSamplesCoherent)
        {
            Span<Vector3> starts = stackalloc Vector3[TrackerSlots.Length];
            Span<Vector3> ends = stackalloc Vector3[TrackerSlots.Length];
            Span<bool> available = stackalloc bool[TrackerSlots.Length];
            for (int s = 0; s < TrackerSlots.Length; s++)
            {
                available[s] = TryGetSlotSegment(TrackerSlots[s], out starts[s], out ends[s]);
            }
            VrTrackerNearestAssignment.Assign(CollectionsMarshal.AsSpan(_bindingSamples), TrackerSlots,
                starts, ends, available, cutoffSquared, _proposedDevices);
        }
        foreach (var entry in collection.Trackers.Values)
        {
            EHumanoidIKTarget? slot = null;
            for (int s = 0; s < TrackerSlots.Length; s++)
                if (ReferenceEquals(_proposedDevices[(int)TrackerSlots[s]], entry.Item2)) { slot = TrackerSlots[s]; break; }
            _bindingPreview.Add(new(entry.Item2, slot));
        }
    }

    private bool TryGetSlotSegment(EHumanoidIKTarget slot, out Vector3 a, out Vector3 b)
    {
        a = b = default;
        if (_humanoid is null) return false;
        SceneNode? bone = slot switch
        {
            EHumanoidIKTarget.Hips => _humanoid.HipsNode,
            EHumanoidIKTarget.Chest => _humanoid.ChestNode,
            EHumanoidIKTarget.LeftFoot => _humanoid.LeftToesNode ?? _humanoid.LeftFootNode,
            EHumanoidIKTarget.RightFoot => _humanoid.RightToesNode ?? _humanoid.RightFootNode,
            EHumanoidIKTarget.LeftElbow => _humanoid.LeftElbowNode,
            EHumanoidIKTarget.RightElbow => _humanoid.RightElbowNode,
            EHumanoidIKTarget.LeftKnee => _humanoid.LeftKneeNode,
            EHumanoidIKTarget.RightKnee => _humanoid.RightKneeNode,
            _ => null,
        };
        if (bone is null) return false;
        a = b = bone.Transform.WorldTranslation;
        if (slot is EHumanoidIKTarget.LeftElbow or EHumanoidIKTarget.RightElbow)
        {
            SceneNode? shoulder = slot == EHumanoidIKTarget.LeftElbow ? _humanoid.LeftUpperArmNode : _humanoid.RightUpperArmNode;
            if (shoulder is not null) a = shoulder.Transform.WorldTranslation;
        }
        else if (slot is EHumanoidIKTarget.LeftKnee or EHumanoidIKTarget.RightKnee)
        {
            TransformBase? thigh = bone.Transform.Parent;
            SceneNode? foot = slot == EHumanoidIKTarget.LeftKnee ? _humanoid.LeftFootNode : _humanoid.RightFootNode;
            if (thigh is not null) a = (thigh.WorldTranslation + a) * 0.5f;
            if (foot is not null) b = (foot.Transform.WorldTranslation + b) * 0.5f;
        }
        return true;
    }

}
