using XREngine.Components;
using XREngine.Components.VR;
using XREngine.Input;
using XREngine.Scene;

namespace XREngine.Data.Components.Scene
{
    /// <summary>
    /// Handles the connection and management of VR trackers in the scene.
    /// </summary>
    public class VRTrackerCollectionComponent : XRComponent
    {
        private DateTime _nextOpenXrTrackerReverifyUtc = DateTime.MinValue;
        private readonly HashSet<SceneNode> _ownedTrackerNodes = [];
        private readonly List<VRTrackerTransform> _retiredTrackers = [];
        private RuntimeVrTrackerPose[] _snapshotTrackers = new RuntimeVrTrackerPose[16];
        private RuntimeVrRuntimeKind _lastRuntime;
        private long _sessionGeneration;
        private long _referenceSpaceVersion;

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            ReverifyTrackedDevices();
            RuntimeVrStateServices.FrameAdvanced += ReverifyOpenXrTrackersThrottled;
        }

        protected override void OnComponentDeactivated()
        {
            RuntimeVrStateServices.FrameAdvanced -= ReverifyOpenXrTrackersThrottled;
            foreach (SceneNode node in _ownedTrackerNodes)
                node.Destroy(true);
            _ownedTrackerNodes.Clear();
            Trackers.Clear();
            OpenXrTrackers.Clear();
            base.OnComponentDeactivated();
        }

        public Dictionary<uint, (RuntimeVrDeviceInfo?, VRTrackerTransform)> Trackers { get; } = [];
        public Dictionary<string, VRTrackerTransform> OpenXrTrackers { get; } = new(StringComparer.Ordinal);

        private void ReverifyTrackedDevices()
        {
            if (RuntimeVrStateServices.IsOpenXRActive)
            {
                ReverifyOpenXrTrackers();
                return;
            }

            foreach (RuntimeVrDeviceInfo device in RuntimeVrStateServices.TrackedDevices)
            {
                if (device.DeviceClass != RuntimeVrDeviceClass.GenericTracker)
                    continue;
                if (Trackers.TryGetValue(device.DeviceIndex, out var existing))
                {
                    // A recycled index may refer to a different device. Never transfer its node/binding.
                    if (existing.Item1?.PersistentIdentity != device.PersistentIdentity)
                    {
                        RemoveOwnedTracker(existing.Item2);
                        if (device.IsConnected) AddRealTracker(device);
                    }
                    else
                    {
                        existing.Item2.Tracker = device;
                        Trackers[device.DeviceIndex] = (device, existing.Item2);
                    }
                }
                else if (device.IsConnected)
                    AddRealTracker(device);
            }
        }

        private void ReverifyOpenXrTrackersThrottled()
        {
            // FrameAdvanced is the host's PreUpdateFrame hook, never a native input callback.
            bool openXr = RuntimeVrStateServices.IsOpenXRActive;
            if (openXr)
            {
                bool copied = RuntimeVrStateServices.TryCopyTrackingSnapshot(_snapshotTrackers, out RuntimeVrTrackingSnapshot snapshot, out int count);
                if (count > _snapshotTrackers.Length)
                {
                    Array.Resize(ref _snapshotTrackers, count);
                    copied = RuntimeVrStateServices.TryCopyTrackingSnapshot(_snapshotTrackers, out snapshot, out count);
                }
                if (copied)
                {
                    if (_sessionGeneration != snapshot.SessionGeneration)
                    {
                        _sessionGeneration = snapshot.SessionGeneration;
                        _referenceSpaceVersion = snapshot.ReferenceSpaceVersion;
                        _nextOpenXrTrackerReverifyUtc = DateTime.MinValue;
                        RuntimeVrDiscontinuityServices.Publish(EVrPoseDiscontinuity.SessionGeneration);
                    }
                    else if (_referenceSpaceVersion != snapshot.ReferenceSpaceVersion)
                    {
                        _referenceSpaceVersion = snapshot.ReferenceSpaceVersion;
                        RuntimeVrDiscontinuityServices.Publish(EVrPoseDiscontinuity.Recenter);
                    }
                    for (int i = 0; i < count; i++)
                        if (OpenXrTrackers.TryGetValue(_snapshotTrackers[i].Info.UserPath, out VRTrackerTransform? tracker)
                            && tracker.OpenXrTrackerInfo.SessionGeneration == snapshot.SessionGeneration)
                            tracker.ApplyOpenXrTrackerInfo(_snapshotTrackers[i].Info);
                }
                else
                    foreach (VRTrackerTransform tracker in OpenXrTrackers.Values)
                        tracker.ApplyOpenXrTrackerInfo(tracker.OpenXrTrackerInfo with { PoseAvailable = false, IsStale = true });
            }

            DateTime now = DateTime.UtcNow;
            RuntimeVrRuntimeKind runtime = RuntimeVrStateServices.ActiveRuntime;
            if (now < _nextOpenXrTrackerReverifyUtc && _lastRuntime == runtime)
                return;
            _lastRuntime = runtime;
            _nextOpenXrTrackerReverifyUtc = now + TimeSpan.FromSeconds(1);
            if (openXr)
                ReverifyOpenXrTrackers();
            else
            {
                RetireMissingOpenXrTrackers([]);
                ReverifyTrackedDevices();
            }
        }

        private void ReverifyOpenXrTrackers()
        {
            RuntimeVrTrackerInfo[] trackers = RuntimeVrStateServices.GetKnownOpenXrTrackers();
            RetireMissingOpenXrTrackers(trackers);
            for (int i = 0; i < trackers.Length; i++)
            {
                RuntimeVrTrackerInfo tracker = trackers[i];
                if (string.IsNullOrWhiteSpace(tracker.PersistentPath))
                    continue;

                if (OpenXrTrackers.TryGetValue(tracker.UserPath, out VRTrackerTransform? existing))
                    existing.ApplyOpenXrTrackerInfo(tracker);
                else
                    AddOpenXrTracker(tracker);
            }
        }

        /// <summary>
        /// Adds a real VR tracker discovered from the VR API to the collection.
        /// </summary>
        /// <param name="device"></param>
        private void AddRealTracker(RuntimeVrDeviceInfo device)
        {
            SceneNode trackerNode = SceneNode.NewChild();
            _ownedTrackerNodes.Add(trackerNode);
            trackerNode.Name = $"Tracker {device.DeviceIndex}";

            VRTrackerTransform tfm = trackerNode.SetTransform<VRTrackerTransform>();
            tfm.Tracker = device;

            VRTrackerModelComponent modelComponent = trackerNode.AddComponent<VRTrackerModelComponent>()!;
            modelComponent.DeviceIndex = device.DeviceIndex;

            Trackers.Add(device.DeviceIndex, (device, tfm));
        }

        private void AddOpenXrTracker(RuntimeVrTrackerInfo tracker)
        {
            SceneNode trackerNode = SceneNode.NewChild();
            _ownedTrackerNodes.Add(trackerNode);
            trackerNode.Name = $"OpenXR Tracker {GetOpenXrTrackerDisplayName(tracker)}";

            VRTrackerTransform tfm = trackerNode.SetTransform<VRTrackerTransform>();
            tfm.ApplyOpenXrTrackerInfo(tracker);

            VRTrackerModelComponent modelComponent = trackerNode.AddComponent<VRTrackerModelComponent>()!;
            modelComponent.OpenXrTrackerUserPath = tracker.UserPath;

            uint syntheticDeviceIndex = uint.MaxValue - (uint)Trackers.Count;
            while (Trackers.ContainsKey(syntheticDeviceIndex))
                syntheticDeviceIndex--;

            Trackers.Add(syntheticDeviceIndex, (null, tfm));
            OpenXrTrackers.Add(tracker.UserPath, tfm);
        }

        /// <summary>
        /// Adds a manual tracker to the collection that does not correspond to any real VR device.
        /// This tracker can be used for custom tracking or testing.
        /// </summary>
        public VRTrackerTransform AddManualTracker(string? name = null)
        {
            SceneNode trackerNode = SceneNode.NewChild();
            _ownedTrackerNodes.Add(trackerNode);
            trackerNode.Name = name ?? "Manual Tracker";

            VRTrackerTransform tfm = trackerNode.SetTransform<VRTrackerTransform>();

            uint manualTrackerDeviceIndex = uint.MaxValue;
            while (Trackers.ContainsKey(manualTrackerDeviceIndex))
                manualTrackerDeviceIndex--;
            Trackers.Add(manualTrackerDeviceIndex, (null, tfm));
            return tfm;
        }

        public VRTrackerTransform? GetTrackerByNodeName(string name, StringComparison comp = StringComparison.InvariantCultureIgnoreCase)
        {
            foreach (var tracker in Trackers.Values)
            {
                VRTrackerTransform nodeTransform = tracker.Item2;
                SceneNode? node = nodeTransform.SceneNode;
                if (node is not null && string.Equals(node.Name, name, comp))
                    return nodeTransform;
            }
            return null;
        }

        private void RetireMissingOpenXrTrackers(RuntimeVrTrackerInfo[] trackers)
        {
            _retiredTrackers.Clear();
            foreach (var entry in OpenXrTrackers)
            {
                bool present = false;
                for (int i = 0; i < trackers.Length; i++)
                    if (trackers[i].UserPath == entry.Key && trackers[i].SessionGeneration == entry.Value.OpenXrTrackerInfo.SessionGeneration)
                    {
                        present = true;
                        break;
                    }
                if (!present)
                    _retiredTrackers.Add(entry.Value);
            }
            foreach (VRTrackerTransform tracker in _retiredTrackers)
            {
                if (tracker.OpenXrTrackerUserPath is { } path)
                    OpenXrTrackers.Remove(path);
                RemoveOwnedTracker(tracker);
            }
        }

        private void RemoveOwnedTracker(VRTrackerTransform tracker)
        {
            foreach (var entry in Trackers)
                if (ReferenceEquals(entry.Value.Item2, tracker))
                {
                    Trackers.Remove(entry.Key);
                    break;
                }
            if (tracker.SceneNode is { } node && _ownedTrackerNodes.Remove(node))
                node.Destroy(true);
        }

        private static string GetOpenXrTrackerDisplayName(RuntimeVrTrackerInfo tracker)
        {
            string path = tracker.PersistentPath ?? tracker.UserPath;
            int slash = path.LastIndexOf('/');
            return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
        }
    }
}
