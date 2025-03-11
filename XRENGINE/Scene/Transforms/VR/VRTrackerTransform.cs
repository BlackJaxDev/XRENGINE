using OpenVR.NET.Devices;
using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Components.Scene
{
    /// <summary>
    /// The transform for a VR tracker.
    /// </summary>
    /// <param name="parent"></param>
    public class VRTrackerTransform : TransformBase
    {
        public VRTrackerTransform()
            => Engine.VRState.RecalcMatrixOnDraw += VRState_RecalcMatrixOnDraw;

        public VRTrackerTransform(TransformBase parent)
            : base(parent)
            => Engine.VRState.RecalcMatrixOnDraw += VRState_RecalcMatrixOnDraw;

        private Matrix4x4 _lastVRMatrixUpdate = Matrix4x4.Identity;
        private void VRState_RecalcMatrixOnDraw()
        {
            _lastVRMatrixUpdate = Tracker?.RenderDeviceToAbsoluteTrackingMatrix ?? Matrix4x4.Identity;
            MarkLocalModified();
            RecalculateMatrixHeirarchy(true, true, true);
        }

        private uint? _deviceIndex;
        public uint? DeviceIndex
        {
            get => _deviceIndex;
            set => SetField(ref _deviceIndex, value);
        }

        private VrDevice? _tracker = null;
        public VrDevice? Tracker
        {
            get => _tracker ?? SetFieldReturn(ref _tracker, DeviceIndex is null ? null : Engine.VRState.Api.TrackedDevices.FirstOrDefault(d => d.DeviceIndex == DeviceIndex && Engine.VRState.Api.CVR.GetTrackedDeviceClass(d.DeviceIndex) == Valve.VR.ETrackedDeviceClass.GenericTracker));
            set => SetField(ref _tracker, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Tracker):
                    DeviceIndex = _tracker?.DeviceIndex;
                    break;
            }
        }

        protected override Matrix4x4 CreateLocalMatrix()
            => _lastVRMatrixUpdate;
    }
}
