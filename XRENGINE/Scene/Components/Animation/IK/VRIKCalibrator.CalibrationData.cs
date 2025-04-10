using System.Numerics;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Scene.Components.Animation
{
public static partial class VRIKCalibrator
    {
        /// <summary>
        /// When VRIK is calibrated by calibration settings, will store CalibrationData that can be used to set up another character with the exact same calibration.
        /// </summary>
        [System.Serializable]
        public class CalibrationData
        {
            [System.Serializable]
            public class Target
            {
                public bool _used;
                public Vector3 localPosition;
                public Quaternion localRotation;

                public Target(Transform? t)
                {
                    _used = t != null;

                    if (!_used)
                        return;

                    localPosition = t!.Translation;
                    localRotation = t!.Rotation;
                }

                public void ApplyLocalTransformTo(Transform t)
                {
                    if (!_used)
                        return;

                    t.Translation = localPosition;
                    t.Rotation = localRotation;
                }
            }

            public float _scale;
            public Target? _head, _leftHand, _rightHand, _pelvis, _leftFoot, _rightFoot, _leftLegGoal, _rightLegGoal;
            public Vector3 _pelvisTargetRight;
            public float _pelvisPositionWeight;
            public float _pelvisRotationWeight;
        }
    }
}
