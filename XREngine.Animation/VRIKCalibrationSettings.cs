using MemoryPack;
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
        private float _hipRotationWeight = 1.0f;
        private float _calibrationHeadTiltTolerance = 10f;
        /// <summary>Maximum headset pitch or roll, in degrees, allowed when capturing a standing pose.</summary>
        [Range(0f, 45f)]
        public float CalibrationHeadTiltTolerance
        {
            get => _calibrationHeadTiltTolerance;
            set => SetField(ref _calibrationHeadTiltTolerance, value);
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

    }
}
