using XREngine.Input.Devices;
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
    }
}