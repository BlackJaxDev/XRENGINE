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
        public VRTrackerTransform() { }
        public VRTrackerTransform(TransformBase parent) : base(parent) { }

        protected internal override void OnSceneNodeActivated()
        {
            base.OnSceneNodeActivated();
            Engine.VRState.RecalcMatrixOnDraw += VRState_RecalcMatrixOnDraw;
            Engine.Time.Timer.PreUpdateFrame += MarkLocalModified;
        }
        protected internal override void OnSceneNodeDeactivated()
        {
            base.OnSceneNodeDeactivated();
            Engine.VRState.RecalcMatrixOnDraw -= VRState_RecalcMatrixOnDraw;
            Engine.Time.Timer.PreUpdateFrame -= MarkLocalModified;
        }

        private void VRState_RecalcMatrixOnDraw()
            => SetRenderMatrix((Tracker?.RenderDeviceToAbsoluteTrackingMatrix ?? Matrix4x4.Identity) * ParentRenderMatrix, true);

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
            => Tracker?.DeviceToAbsoluteTrackingMatrix ?? Matrix4x4.Identity;
    }
}
