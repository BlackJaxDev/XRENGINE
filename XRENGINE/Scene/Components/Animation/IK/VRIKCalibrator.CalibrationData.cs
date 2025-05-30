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
                set => _scale = value;
			}

			private Target? _head, _leftHand, _rightHand, _pelvis, _leftFoot, _rightFoot, _leftLegGoal, _rightLegGoal;
            public Target? Head
            {
                get => _head;
                set => _head = value;
			}
            public Target? LeftHand
            {
                get => _leftHand;
                set => _leftHand = value;
			}
            public Target? RightHand
            {
                get => _rightHand;
                set => _rightHand = value;
            }
            public Target? Hips
            {
                get => _pelvis;
                set => _pelvis = value;
            }
            public Target? LeftFoot
            {
                get => _leftFoot;
                set => _leftFoot = value;
            }
            public Target? RightFoot
            {
                get => _rightFoot;
                set => _rightFoot = value;
            }
            public Target? LeftLegGoal
            {
                get => _leftLegGoal;
                set => _leftLegGoal = value;
            }
            public Target? RightLegGoal
            {
                get => _rightLegGoal;
                set => _rightLegGoal = value;
			}

			private Vector3 _hipsTargetRight;
            public Vector3 HipsTargetRight
            {
                get => _hipsTargetRight;
                set => _hipsTargetRight = value;
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
