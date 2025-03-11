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
        public VRControllerTransform()
            => Engine.VRState.RecalcMatrixOnDraw += VRState_RecalcMatrixOnDraw;

        public VRControllerTransform(TransformBase parent)
            : base(parent)
            => Engine.VRState.RecalcMatrixOnDraw += VRState_RecalcMatrixOnDraw;

        private void VRState_RecalcMatrixOnDraw()
        {
            _lastVRMatrixUpdate = Controller?.RenderDeviceToAbsoluteTrackingMatrix ?? Matrix4x4.Identity;
            MarkLocalModified();
            RecalculateMatrixHeirarchy(true, true, true);
        }

        private bool _leftHand;
        public bool LeftHand
        {
            get => _leftHand;
            set => SetField(ref _leftHand, value);
        }

        public Controller? Controller => LeftHand 
            ? Engine.VRState.Api.LeftController 
            : Engine.VRState.Api.RightController;

        private Matrix4x4 _lastVRMatrixUpdate = Matrix4x4.Identity;
        protected override Matrix4x4 CreateLocalMatrix()
            => _lastVRMatrixUpdate;
    }
}
