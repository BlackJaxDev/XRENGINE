using OpenVR.NET.Devices;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Components.Scene
{
    /// <summary>
    /// The transform for the left or right VR controller.
    /// </summary>
    /// <param name="parent"></param>
    public class VRControllerTransform : VRDeviceTransformBase
    {
        public VRControllerTransform() { }
        public VRControllerTransform(TransformBase parent) : base(parent) { }

        private bool _leftHand;
        public bool LeftHand
        {
            get => _leftHand;
            set => SetField(ref _leftHand, value);
        }

        public Controller? Controller => LeftHand
            ? Engine.VRState.Api.LeftController
            : Engine.VRState.Api.RightController;

        public override VrDevice? Device => Controller;
    }
}
