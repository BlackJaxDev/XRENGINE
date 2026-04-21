using System.Numerics;
using XREngine.Input.Devices.Types.OpenVR;

namespace XREngine.Data.Components.Scene
{
    public class VRActionPoseTransform<TCategory, TName> : VRActionTransformBase<TCategory, TName>, IVRActionPoseTransform<TCategory, TName>
        where TCategory : struct, Enum
        where TName : struct, Enum
    {
        private bool _nextFrame = false;
        private float _secondsFromNow = 0.0f;

        private Vector3 _velocity;
        private Vector3 _angularVelocity;

        public bool QueryNextFrame
        {
            get => _nextFrame;
            set => SetField(ref _nextFrame, value);
        }
        public float QuerySecondsFromNow
        {
            get => _secondsFromNow;
            set => SetField(ref _secondsFromNow, value);
        }

        public Vector3 Velocity
        {
            get => _velocity;
            internal set => SetField(ref _velocity, value);
        }
        public Vector3 AngularVelocity
        {
            get => _angularVelocity;
            internal set => SetField(ref _angularVelocity, value);
        }
    }
}