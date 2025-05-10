using OpenVR.NET.Devices;
using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Components.Scene
{
    /// <summary>
    /// The transform for the left or right VR controller.
    /// </summary>
    /// <param name="parent"></param>
    public class VRControllerTransform : TransformBase
    {
        public VRControllerTransform() { }
        public VRControllerTransform(TransformBase parent) : base(parent) { }

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
            => SetRenderMatrix((Controller?.RenderDeviceToAbsoluteTrackingMatrix ?? Matrix4x4.Identity) * ParentRenderMatrix, true);

        private bool _leftHand;
        public bool LeftHand
        {
            get => _leftHand;
            set => SetField(ref _leftHand, value);
        }

        public Controller? Controller => LeftHand 
            ? Engine.VRState.Api.LeftController 
            : Engine.VRState.Api.RightController;

        protected override Matrix4x4 CreateLocalMatrix()
            => Controller?.DeviceToAbsoluteTrackingMatrix ?? Matrix4x4.Identity;
    }
}
