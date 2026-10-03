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

        private RuntimeVrDeviceInfo? _tracker;
        private string? _syntheticIdentity;

        /// <summary>Explicit session identity for a manually supplied tracker pose.</summary>
        public string? SyntheticIdentity
        {
            get => _syntheticIdentity;
            set => SetField(ref _syntheticIdentity, value);
        }
        public RuntimeVrDeviceInfo? Tracker
        {
            get => _tracker;
            set => SetField(ref _tracker, value);
        }

        public override RuntimeVrDeviceInfo? Device => Tracker;

        /// <summary>Session-scoped physical path used to keep a binding with the same tracker after reconnection.</summary>
        public string? SessionIdentity
        {
            get
            {
                if (RuntimeVrStateServices.IsOpenXRActive)
                    return SyntheticPoseEnabled ? SyntheticIdentity : OpenXrTrackerPersistentPath;
                if (SyntheticPoseEnabled)
                    return SyntheticIdentity;
                return Tracker?.PersistentIdentity;
            }
        }

        private string? _openXrTrackerUserPath;
        /// <summary>
        /// Opaque physical tracker path used to resolve OpenXR poses. Never a body role.
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
            OpenXrTrackerPoseAvailable = tracker.PoseCurrentlyUsable;
            OpenXrTrackerInfo = tracker;
        }

        private RuntimeVrTrackerInfo _openXrTrackerInfo;
        public RuntimeVrTrackerInfo OpenXrTrackerInfo
        {
            get => _openXrTrackerInfo;
            private set => SetField(ref _openXrTrackerInfo, value);
        }

        public void SetTrackerByDeviceIndex(uint deviceIndex)
        {
            foreach (RuntimeVrDeviceInfo device in RuntimeVrStateServices.TrackedDevices)
            {
                if (device.DeviceIndex != deviceIndex || device.DeviceClass != RuntimeVrDeviceClass.GenericTracker)
                    continue;

                Tracker = device;
                return;
            }
        }
    }
}
