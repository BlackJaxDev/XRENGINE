using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Transforms.Rotations;

namespace XREngine.Scene.Components.Animation
{
    public static partial class InverseKinematics
    {
        /// <summary>
        /// Contains constraint settings for a bone's IK.
        /// </summary>
        public class BoneIKConstraints : XRBase
        {
            public float MaxYaw { get; set; } = 360.0f;
            public float MinYaw { get; set; } = 360.0f;
            public float MaxPitch { get; set; } = 360.0f;
            public float MinPitch { get; set; } = 360.0f;
            public float MaxRoll { get; set; } = 360.0f;
            public float MinRoll { get; set; } = 360.0f;

            /// <summary>
            /// Given an IK solve for the child bone in local bind space, constrain the child bone's local position.
            /// </summary>
            /// <param name="childVector"></param>
            /// <returns></returns>
            public Vector3 ConstrainChildLocalPosition(Vector3 childVector)
            {
                //First, pitch the child vector 90 deg down to convert from an up vector (typical bone orientation) to a forward vector (camera-like orientation)
                childVector = Vector3.Transform(childVector, Quaternion.CreateFromYawPitchRoll(0.0f, -90.0f * XRMath.DegToRadMultf, 0.0f));

                Rotator r = XRMath.LookatAngles(childVector);
                Quaternion preConstrained = Quaternion.CreateFromYawPitchRoll(
                    r.Yaw * XRMath.DegToRadMultf,
                    r.Pitch * XRMath.DegToRadMultf,
                    r.Roll * XRMath.DegToRadMultf);
                r.Yaw = Math.Clamp(r.Yaw, MinYaw, MaxYaw);
                r.Pitch = Math.Clamp(r.Pitch, MinPitch, MaxPitch);
                Quaternion postConstrained = Quaternion.CreateFromYawPitchRoll(
                    r.Yaw * XRMath.DegToRadMultf,
                    r.Pitch * XRMath.DegToRadMultf,
                    0.0f);
                childVector = Vector3.Transform(childVector, Quaternion.Inverse(preConstrained) * postConstrained);

                //Now, pitch the child vector 90 deg up
                childVector = Vector3.Transform(childVector, Quaternion.CreateFromYawPitchRoll(0.0f, 90.0f * XRMath.DegToRadMultf, 0.0f));
                return childVector;
            }
        }
    }
}