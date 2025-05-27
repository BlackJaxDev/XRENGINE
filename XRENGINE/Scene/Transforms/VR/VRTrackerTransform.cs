using OpenVR.NET.Devices;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Components.Scene
{
    /// <summary>
    /// The transform for a VR tracker.
    /// </summary>
    public class VRTrackerTransform : VRDeviceTransformBase
    {
        public VRTrackerTransform() { }
        public VRTrackerTransform(TransformBase parent) : base(parent) { }

        private VrDevice? _tracker = null;
        public VrDevice? Tracker
        {
            get => _tracker;
            set => SetField(ref _tracker, value);
        }

        public override VrDevice? Device => Tracker;

        public void SetTrackerByDeviceIndex(uint deviceIndex)
        {
            var d = Engine.VRState.Api.TrackedDevices.FirstOrDefault(x => x.DeviceIndex == deviceIndex);
            if (d is null)
                return;

            if (Engine.VRState.Api.CVR.GetTrackedDeviceClass(d.DeviceIndex) != Valve.VR.ETrackedDeviceClass.GenericTracker)
                return;

            Tracker = d;
        }
    }
}
