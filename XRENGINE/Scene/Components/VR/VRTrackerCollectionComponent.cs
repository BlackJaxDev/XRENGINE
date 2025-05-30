using OpenVR.NET.Devices;
using Valve.VR;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Components.VR;

namespace XREngine.Data.Components.Scene
{
    /// <summary>
    /// Handles the connection and management of VR trackers in the scene.
    /// </summary>
    public class VRTrackerCollectionComponent : XRComponent
    {
        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            ReverifyTrackedDevices();
            Engine.VRState.Api.DeviceDetected += OnDeviceDetected;
        }

        protected internal override void OnComponentDeactivated()
        {
            Engine.VRState.Api.DeviceDetected -= OnDeviceDetected;
            Trackers.Clear();
            base.OnComponentDeactivated();
        }

        public Dictionary<uint, (VrDevice?, VRTrackerTransform)> Trackers { get; } = [];

        private void OnDeviceDetected(VrDevice device)
            => ReverifyTrackedDevices();

        private void ReverifyTrackedDevices()
        {
            foreach (var dev in Engine.VRState.Api.TrackedDevices)
                if (!Trackers.ContainsKey(dev.DeviceIndex) && Engine.VRState.Api.CVR.GetTrackedDeviceClass(dev.DeviceIndex) == ETrackedDeviceClass.GenericTracker)
                    AddRealTracker(dev);
        }

        /// <summary>
        /// Adds a real VR tracker discovered from the VR API to the collection.
        /// </summary>
        /// <param name="dev"></param>
        private void AddRealTracker(VrDevice dev)
        {
            SceneNode trackerNode = SceneNode.NewChild<VRTrackerModelComponent>(out VRTrackerModelComponent? modelComp);
            trackerNode.Name = $"Tracker {dev.DeviceIndex}";

            VRTrackerTransform tfm = trackerNode.SetTransform<VRTrackerTransform>();
            tfm.Tracker = dev;

            modelComp.DeviceIndex = dev.DeviceIndex;
            modelComp.LoadModelAsync(dev.Model);

            Trackers.Add(dev.DeviceIndex, (dev, tfm));
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
                var nodeTransform = tracker.Item2;
                var node = nodeTransform.SceneNode;
                if (node is not null && string.Equals(node.Name, name, comp))
                    return nodeTransform;
            }
            return null;
        }
    }
}
