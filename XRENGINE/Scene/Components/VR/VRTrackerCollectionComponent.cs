using OpenVR.NET.Devices;
using Valve.VR;
using XREngine.Components;
using XREngine.Scene.Components.VR;

namespace XREngine.Data.Components.Scene
{
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

        public Dictionary<uint, (VrDevice, VRTrackerTransform)> Trackers { get; } = [];

        private void OnDeviceDetected(VrDevice device)
            => ReverifyTrackedDevices();

        private void ReverifyTrackedDevices()
        {
            var devices = Engine.VRState.Api.TrackedDevices;
            foreach (var dev in devices)
            {
                if (!dev.IsEnabled || Trackers.ContainsKey(dev.DeviceIndex))
                    continue;

                var c = Engine.VRState.Api.CVR.GetTrackedDeviceClass(dev.DeviceIndex);
                if (c != ETrackedDeviceClass.GenericTracker)
                    continue;
                
                var trackerNode = SceneNode.NewChild<VRTrackerModelComponent>(out var modelComp);
                trackerNode.Name = $"Tracker {dev.DeviceIndex}";
                var tfm = trackerNode.SetTransform<VRTrackerTransform>();
                tfm.ForceManualRecalc = true;
                tfm.DeviceIndex = dev.DeviceIndex;
                tfm.Tracker = dev;
                modelComp.DeviceIndex = dev.DeviceIndex;
                modelComp.LoadModelAsync(dev.Model);
                Trackers.Add(dev.DeviceIndex, (dev, tfm));
            }
        }
    }
}
