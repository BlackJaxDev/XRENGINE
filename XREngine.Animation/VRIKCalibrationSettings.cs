using MemoryPack;
using System.Numerics;
using System.ComponentModel.DataAnnotations;
using XREngine.Core.Files;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Shared calibration settings for VRIK tracker calibration.
    /// Lives below the runtime integration layer so engine systems can hold settings
    /// without referencing the moved animation component assembly.
    /// </summary>
    [System.Serializable]
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class VRIKCalibrationSettings : XRAsset
    {
        private float _headTiltToleranceDegrees = 10.0f;
        public float HeadTiltToleranceDegrees
        {
            get => _headTiltToleranceDegrees;
            set => SetField(ref _headTiltToleranceDegrees, value);
        }

        private float _hipRotationWeight = 1.0f;
        /// <summary>Maximum headset pitch or roll, in degrees, allowed when capturing a standing pose.</summary>
        [Range(0f, 45f)]
        public float CalibrationHeadTiltTolerance
        {
            get => HeadTiltToleranceDegrees;
            set => HeadTiltToleranceDegrees = value;
        }
        [Range(0f, 1f)]
        public float HipRotationWeight
        {
            get => _hipRotationWeight;
            set => SetField(ref _hipRotationWeight, value);
        }

        private float _hipPositionWeight = 1.0f;
        [Range(0f, 1f)]
        public float HipPositionWeight
        {
            get => _hipPositionWeight;
            set => SetField(ref _hipPositionWeight, value);
        }

        private Vector3 _handOffset;
        public Vector3 HandOffset
        {
            get => _handOffset;
            set => SetField(ref _handOffset, value);
        }
        private Vector3 _handTrackerUp = Globals.Up;
        public Vector3 HandTrackerUp
        {
            get => _handTrackerUp;
            set => SetField(ref _handTrackerUp, value);
        }

        private Vector3 _handTrackerForward = Globals.Forward;
        public Vector3 HandTrackerForward
        {
            get => _handTrackerForward;
            set => SetField(ref _handTrackerForward, value);
        }
    }
}
