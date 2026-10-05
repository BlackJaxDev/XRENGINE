using XREngine.Input;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Components.Scene
{
    /// <summary>
    /// The transform for the left or right VR controller.
    /// </summary>
    /// <param name="parent"></param>
    public class VRControllerTransform : VRDeviceTransformBase, IVrControllerPoseSource
    {
        public VRControllerTransform() { }
        public VRControllerTransform(TransformBase parent) : base(parent) { }

        private bool _leftHand;
        public bool LeftHand
        {
            get => _leftHand;
            set => SetField(ref _leftHand, value);
        }

        public RuntimeVrDeviceInfo? Controller => LeftHand
            ? RuntimeVrStateServices.LeftController
            : RuntimeVrStateServices.RightController;

        public override RuntimeVrDeviceInfo? Device => Controller;
        public string? InteractionProfile => RuntimeVrStateServices.GetControllerInteractionProfile(LeftHand);
        public System.Numerics.Vector3 GripToWristOffset => VrControllerWristPresets.Resolve(InteractionProfile, RuntimeVrStateServices.PlayerSettings);
    }
}
