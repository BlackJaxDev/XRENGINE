using OpenVR.NET.Devices;
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

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            ReverifyTrackedDevices();
            RuntimeVrStateServices.DeviceDetected += OnDeviceDetected;
            RuntimeVrStateServices.FrameAdvanced += ReverifyOpenXrTrackersThrottled;
        }

        protected override void OnComponentDeactivated()
        {
            RuntimeVrStateServices.FrameAdvanced -= ReverifyOpenXrTrackersThrottled;
            RuntimeVrStateServices.DeviceDetected -= OnDeviceDetected;
            Trackers.Clear();
            OpenXrTrackers.Clear();
            base.OnComponentDeactivated();
        }

        public Dictionary<uint, (VrDevice?, VRTrackerTransform)> Trackers { get; } = [];
        public Dictionary<string, VRTrackerTransform> OpenXrTrackers { get; } = new(StringComparer.Ordinal);

        private void OnDeviceDetected(VrDevice device)
            => ReverifyTrackedDevices();

        private void ReverifyTrackedDevices()
        {
            if (RuntimeVrStateServices.IsOpenXRActive)
            {
                ReverifyOpenXrTrackers();
                return;
            }

            foreach (VrDevice device in RuntimeVrStateServices.TrackedDevices)
            {
                if (!Trackers.ContainsKey(device.DeviceIndex) && RuntimeVrStateServices.IsGenericTracker(device.DeviceIndex))
                    AddRealTracker(device);
            }
        }

        private void ReverifyOpenXrTrackersThrottled()
        {
            if (!RuntimeVrStateServices.IsOpenXRActive)
                return;

            DateTime now = DateTime.UtcNow;
            if (now < _nextOpenXrTrackerReverifyUtc)
                return;

            _nextOpenXrTrackerReverifyUtc = now + TimeSpan.FromSeconds(1);
            ReverifyOpenXrTrackers();
        }

        private void ReverifyOpenXrTrackers()
        {
            RuntimeVrTrackerInfo[] trackers = RuntimeVrStateServices.GetKnownOpenXrTrackers();
            for (int i = 0; i < trackers.Length; i++)
            {
                RuntimeVrTrackerInfo tracker = trackers[i];
                if (string.IsNullOrWhiteSpace(tracker.UserPath))
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
        private void AddRealTracker(VrDevice device)
        {
            SceneNode trackerNode = SceneNode.NewChild();
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

        private static string GetOpenXrTrackerDisplayName(RuntimeVrTrackerInfo tracker)
        {
            if (!string.IsNullOrWhiteSpace(tracker.RoleName))
                return tracker.RoleName;

            string userPath = tracker.UserPath;
            int slash = userPath.LastIndexOf('/');
            return slash >= 0 && slash + 1 < userPath.Length
                ? userPath[(slash + 1)..]
                : userPath;
        }
    }
}
