using OpenVR.NET.Devices;
using System.Linq;
using Valve.VR;
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
        private string? _openVrSessionIdentity;
        private string? _syntheticIdentity;

        /// <summary>Explicit session identity for a manually supplied tracker pose.</summary>
        public string? SyntheticIdentity
        {
            get => _syntheticIdentity;
            set => SetField(ref _syntheticIdentity, value);
        }
        public VrDevice? Tracker
        {
            get => _tracker;
            set
            {
                if (SetField(ref _tracker, value))
                    _openVrSessionIdentity = null;
            }
        }

        public override VrDevice? Device => Tracker;

        /// <summary>Session-scoped physical path used to keep a binding with the same tracker after reconnection.</summary>
        public string? SessionIdentity
        {
            get
            {
                if (RuntimeVrStateServices.IsOpenXRActive)
                    return SyntheticPoseEnabled ? SyntheticIdentity : OpenXrTrackerPersistentPath;
                if (SyntheticPoseEnabled)
                    return SyntheticIdentity;
                if (_openVrSessionIdentity is not null || Tracker is null)
                    return _openVrSessionIdentity;

                try
                {
                    string serial = Tracker.GetString(ETrackedDeviceProperty.Prop_SerialNumber_String);
                    if (!string.IsNullOrWhiteSpace(serial))
                        _openVrSessionIdentity = serial;
                }
                catch
                {
                    // A tracker without a serial cannot safely inherit a calibrated slot.
                }
                return _openVrSessionIdentity;
            }
        }

        private string? _openXrTrackerUserPath;
        /// <summary>
        /// OpenXR persistent tracker user path.
        /// When OpenXR is the active runtime, this is used to resolve tracker poses.
        /// </summary>
        public string? OpenXrTrackerUserPath
        {
            get => _openXrTrackerUserPath;
            set => SetField(ref _openXrTrackerUserPath, value);
        }

        private string? _openXrTrackerPersistentPath;
        public string? OpenXrTrackerPersistentPath
        {
            get => _openXrTrackerPersistentPath;
            set => SetField(ref _openXrTrackerPersistentPath, value);
        }

        private string? _openXrTrackerRolePath;
        public string? OpenXrTrackerRolePath
        {
            get => _openXrTrackerRolePath;
            set => SetField(ref _openXrTrackerRolePath, value);
        }

        private string? _openXrTrackerRoleName;
        public string? OpenXrTrackerRoleName
        {
            get => _openXrTrackerRoleName;
            set => SetField(ref _openXrTrackerRoleName, value);
        }

        private bool _openXrTrackerPoseAvailable;
        public bool OpenXrTrackerPoseAvailable
        {
            get => _openXrTrackerPoseAvailable;
            set => SetField(ref _openXrTrackerPoseAvailable, value);
        }

        public void ApplyOpenXrTrackerInfo(RuntimeVrTrackerInfo tracker)
        {
            OpenXrTrackerUserPath = tracker.PersistentPath ?? tracker.UserPath;
            OpenXrTrackerPersistentPath = tracker.PersistentPath;
            OpenXrTrackerRolePath = tracker.RolePath;
            OpenXrTrackerRoleName = tracker.RoleName;
            OpenXrTrackerPoseAvailable = tracker.PoseAvailable;
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
