using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Components.Animation
{
public static partial class VRIKCalibrator
    {
        /// <summary>
        /// When VRIK is calibrated by calibration settings, will store CalibrationData that can be used to set up another character with the exact same calibration.
        /// </summary>
        [Serializable]
        public partial class CalibrationData : XRBase
        {
            private float _scale;
            public float Scale
            {
                get => _scale;
                set => SetField(ref _scale, value);
			}

			private Target? _head, _leftHand, _rightHand, _pelvis, _leftFoot, _rightFoot, _leftLegGoal, _rightLegGoal;
            public Target? Head
            {
                get => _head;
                set => SetField(ref _head, value);
			}
            public Target? LeftHand
            {
                get => _leftHand;
                set => SetField(ref _leftHand, value);
			}
            public Target? RightHand
            {
                get => _rightHand;
                set => SetField(ref _rightHand, value);
            }
            public Target? Hips
            {
                get => _pelvis;
                set => SetField(ref _pelvis, value);
            }
            public Target? LeftFoot
            {
                get => _leftFoot;
                set => SetField(ref _leftFoot, value);
            }
            public Target? RightFoot
            {
                get => _rightFoot;
                set => SetField(ref _rightFoot, value);
            }
            public Target? LeftLegGoal
            {
                get => _leftLegGoal;
                set => SetField(ref _leftLegGoal, value);
            }
            public Target? RightLegGoal
            {
                get => _rightLegGoal;
                set => SetField(ref _rightLegGoal, value);
			}

			private Vector3 _hipsTargetRight;
            public Vector3 HipsTargetRight
            {
                get => _hipsTargetRight;
                set => SetField(ref _hipsTargetRight, value);
			}

			private float _pelvisPositionWeight;
            public float HipsPositionWeight
            {
                get => _pelvisPositionWeight;
                set => SetField(ref _pelvisPositionWeight, value);
			}

			private float _pelvisRotationWeight;
            public float HipsRotationWeight
            {
                get => _pelvisRotationWeight;
                set => SetField(ref _pelvisRotationWeight, value);
			}
		}
    }
}
