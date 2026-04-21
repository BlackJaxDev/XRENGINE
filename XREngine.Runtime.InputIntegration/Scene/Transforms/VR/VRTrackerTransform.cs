using OpenVR.NET.Devices;
using System.Linq;
using XREngine.Input;
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

        private string? _openXrTrackerUserPath;
        /// <summary>
        /// OpenXR tracker user path (e.g. "/user/vive_tracker_htcx/role/waist").
        /// When OpenXR is the active runtime, this is used to resolve tracker poses.
        /// </summary>
        public string? OpenXrTrackerUserPath
        {
            get => _openXrTrackerUserPath;
            set => SetField(ref _openXrTrackerUserPath, value);
        }

        public void SetTrackerByDeviceIndex(uint deviceIndex)
        {
            VrDevice? device = RuntimeVrStateServices.TrackedDevices.FirstOrDefault(x => x.DeviceIndex == deviceIndex);
            if (device is null || !RuntimeVrStateServices.IsGenericTracker(device.DeviceIndex))
                return;

            Tracker = device;
        }
    }
}