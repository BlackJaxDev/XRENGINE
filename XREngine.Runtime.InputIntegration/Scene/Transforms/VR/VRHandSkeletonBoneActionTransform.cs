using XREngine.Input.Devices;
using XREngine.Input;
using static XREngine.Input.Devices.InputInterface;

namespace XREngine.Data.Components.Scene
{
    public class VRHandSkeletonBoneActionTransform<TCategory, TName> : VRActionTransformBase<TCategory, TName>
        where TCategory : struct, Enum
        where TName : struct, Enum
    {
        private bool _leftHand = false;
        private EVRHandSkeletonBone _bone = EVRHandSkeletonBone.Root;
        private EVRSkeletalTransformSpace _transformSpace = EVRSkeletalTransformSpace.Model;
        private EVRSkeletalMotionRange _motionRange = EVRSkeletalMotionRange.WithController;
        private EVRSkeletalReferencePose? _referencePose = null;

        public bool LeftHand
        {
            get => _leftHand;
            set => SetField(ref _leftHand, value);
        }
        public EVRHandSkeletonBone Bone
        {
            get => _bone;
            set => SetField(ref _bone, value);
        }
        public EVRSkeletalTransformSpace TransformSpace
        {
            get => _transformSpace;
            set => SetField(ref _transformSpace, value);
        }
        public EVRSkeletalMotionRange MotionRange
        {
            get => _motionRange;
            set => SetField(ref _motionRange, value);
        }
        public EVRSkeletalReferencePose? ReferencePose
        {
            get => _referencePose;
            set => SetField(ref _referencePose, value);
        }

        protected override void OnSceneNodeActivated()
        {
            base.OnSceneNodeActivated();
            RuntimeVrStateServices.FrameAdvanced += UpdateRuntimeHandJoint;
        }

        protected override void OnSceneNodeDeactivated()
        {
            RuntimeVrStateServices.FrameAdvanced -= UpdateRuntimeHandJoint;
            base.OnSceneNodeDeactivated();
        }

        private void UpdateRuntimeHandJoint()
        {
            if (!RuntimeVrInputServices.TryGetHandJoint(LeftHand, MapBone(Bone), out RuntimeVrHandJointState state))
                return;

            Position = state.Position;
            Rotation = state.Rotation;
        }

        private static RuntimeVrHandJoint MapBone(EVRHandSkeletonBone bone)
            => bone switch
            {
                EVRHandSkeletonBone.Root => RuntimeVrHandJoint.Palm,
                EVRHandSkeletonBone.Wrist => RuntimeVrHandJoint.Wrist,
                EVRHandSkeletonBone.Thumb0 => RuntimeVrHandJoint.ThumbMetacarpal,
                EVRHandSkeletonBone.Thumb1 => RuntimeVrHandJoint.ThumbProximal,
                EVRHandSkeletonBone.Thumb2 => RuntimeVrHandJoint.ThumbDistal,
                EVRHandSkeletonBone.Thumb3 => RuntimeVrHandJoint.ThumbTip,
                EVRHandSkeletonBone.IndexFinger0 => RuntimeVrHandJoint.IndexMetacarpal,
                EVRHandSkeletonBone.IndexFinger1 => RuntimeVrHandJoint.IndexProximal,
                EVRHandSkeletonBone.IndexFinger2 => RuntimeVrHandJoint.IndexIntermediate,
                EVRHandSkeletonBone.IndexFinger3 => RuntimeVrHandJoint.IndexDistal,
                EVRHandSkeletonBone.IndexFinger4 => RuntimeVrHandJoint.IndexTip,
                EVRHandSkeletonBone.MiddleFinger0 => RuntimeVrHandJoint.MiddleMetacarpal,
                EVRHandSkeletonBone.MiddleFinger1 => RuntimeVrHandJoint.MiddleProximal,
                EVRHandSkeletonBone.MiddleFinger2 => RuntimeVrHandJoint.MiddleIntermediate,
                EVRHandSkeletonBone.MiddleFinger3 => RuntimeVrHandJoint.MiddleDistal,
                EVRHandSkeletonBone.MiddleFinger4 => RuntimeVrHandJoint.MiddleTip,
                EVRHandSkeletonBone.RingFinger0 => RuntimeVrHandJoint.RingMetacarpal,
                EVRHandSkeletonBone.RingFinger1 => RuntimeVrHandJoint.RingProximal,
                EVRHandSkeletonBone.RingFinger2 => RuntimeVrHandJoint.RingIntermediate,
                EVRHandSkeletonBone.RingFinger3 => RuntimeVrHandJoint.RingDistal,
                EVRHandSkeletonBone.RingFinger4 => RuntimeVrHandJoint.RingTip,
                EVRHandSkeletonBone.PinkyFinger0 => RuntimeVrHandJoint.LittleMetacarpal,
                EVRHandSkeletonBone.PinkyFinger1 => RuntimeVrHandJoint.LittleProximal,
                EVRHandSkeletonBone.PinkyFinger2 => RuntimeVrHandJoint.LittleIntermediate,
                EVRHandSkeletonBone.PinkyFinger3 => RuntimeVrHandJoint.LittleDistal,
                EVRHandSkeletonBone.PinkyFinger4 => RuntimeVrHandJoint.LittleTip,
                EVRHandSkeletonBone.Aux_Thumb => RuntimeVrHandJoint.ThumbTip,
                EVRHandSkeletonBone.Aux_IndexFinger => RuntimeVrHandJoint.IndexTip,
                EVRHandSkeletonBone.Aux_MiddleFinger => RuntimeVrHandJoint.MiddleTip,
                EVRHandSkeletonBone.Aux_RingFinger => RuntimeVrHandJoint.RingTip,
                EVRHandSkeletonBone.Aux_PinkyFinger => RuntimeVrHandJoint.LittleTip,
                _ => RuntimeVrHandJoint.Palm,
            };
    }
}
