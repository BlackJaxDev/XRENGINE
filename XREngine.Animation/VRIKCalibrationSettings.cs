using MemoryPack;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
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

        private float _footYawOffset;
        [Range(-180f, 180f)]
        public float FootYawOffset
        {
            get => _footYawOffset;
            set => SetField(ref _footYawOffset, value);
        }

        private float _footInwardOffset;
        public float FootInwardOffset
        {
            get => _footInwardOffset;
            set => SetField(ref _footInwardOffset, value);
        }

        private float _footForwardOffset;
        public float FootForwardOffset
        {
            get => _footForwardOffset;
            set => SetField(ref _footForwardOffset, value);
        }

        private Vector3 _handOffset;
        public Vector3 HandOffset
        {
            get => _handOffset;
            set => SetField(ref _handOffset, value);
        }

        private Vector3 _headOffset = Vector3.Zero;
        public Vector3 HeadOffset
        {
            get => _headOffset;
            set => SetField(ref _headOffset, value);
        }

        private Vector3 _footTrackerUp = Globals.Up;
        public Vector3 FootTrackerUp
        {
            get => _footTrackerUp;
            set => SetField(ref _footTrackerUp, value);
        }

        private Vector3 _footTrackerForward = Globals.Forward;
        public Vector3 FootTrackerForward
        {
            get => _footTrackerForward;
            set => SetField(ref _footTrackerForward, value);
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

        private Vector3 _headTrackerUp = Globals.Up;
        public Vector3 HeadTrackerUp
        {
            get => _headTrackerUp;
            set => SetField(ref _headTrackerUp, value);
        }

        private Vector3 _headTrackerForward = Globals.Forward;
        public Vector3 HeadTrackerForward
        {
            get => _headTrackerForward;
            set => SetField(ref _headTrackerForward, value);
        }

        private float _scaleMultiplier = 1.0f;
        public float ScaleMultiplier
        {
            get => _scaleMultiplier;
            set => SetField(ref _scaleMultiplier, value);
        }
    }
}