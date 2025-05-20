using Extensions;
using System.Numerics;
using Valve.VR;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// Transforms from the headset to the left or right eye.
    /// </summary>
    /// <param name="parent"></param>
    public class VREyeTransform : TransformBase
    {
        public bool IsLeftEye { get; }

        /// <summary>
        /// The distance between the eyes in meters.
        /// </summary>
        public static float IPD
        {
            get
            {
                if (Engine.VRState.Api.IsHeadsetPresent && Engine.VRState.Api.CVR is not null)
                {
                    ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
                    return (float)Engine.VRState.Api.CVR.GetFloatTrackedDeviceProperty(Engine.VRState.Api.Headset!.DeviceIndex, ETrackedDeviceProperty.Prop_UserIpdMeters_Float, ref error);
                }
                else
                {
                    return (float)0f;
                }
            }
        }

        /// <summary>
        /// Half the distance between the eyes in meters.
        /// </summary>
        public static float EyeSeparation => IPD / 2.0f;

        private float _ipdScale = 1.0f;
        public float IPDScale
        {
            get => _ipdScale;
            set
            {
                SetField(ref _ipdScale, value);
                MarkLocalModified();
            }
        }

        public VREyeTransform() { }
        public VREyeTransform(TransformBase? parent)
            : base(parent) { }
        public VREyeTransform(bool isLeftEye, TransformBase? parent = null)
            : this(parent) => IsLeftEye = isLeftEye;

        protected override Matrix4x4 CreateLocalMatrix()
        {
            var eyeEnum = IsLeftEye 
                ? EVREye.Eye_Left 
                : EVREye.Eye_Right;

            return Engine.VRState.Api.IsHeadsetPresent && Engine.VRState.Api.CVR is not null
                ? Engine.VRState.Api.CVR.GetEyeToHeadTransform(eyeEnum).ToNumerics().Transposed().Inverted()
                : Matrix4x4.Identity;
        }
    }
}