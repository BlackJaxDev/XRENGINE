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
        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            ReverifyTrackedDevices();
            RuntimeVrStateServices.DeviceDetected += OnDeviceDetected;
        }

        protected override void OnComponentDeactivated()
        {
            RuntimeVrStateServices.DeviceDetected -= OnDeviceDetected;
            Trackers.Clear();
            base.OnComponentDeactivated();
        }

        public Dictionary<uint, (VrDevice?, VRTrackerTransform)> Trackers { get; } = [];

        private void OnDeviceDetected(VrDevice device)
            => ReverifyTrackedDevices();

        private void ReverifyTrackedDevices()
        {
            foreach (VrDevice device in RuntimeVrStateServices.TrackedDevices)
            {
                if (!Trackers.ContainsKey(device.DeviceIndex) && RuntimeVrStateServices.IsGenericTracker(device.DeviceIndex))
                    AddRealTracker(device);
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
    }
}