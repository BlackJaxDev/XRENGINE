using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Core.Files;

namespace XREngine.Components.Animation
{
    public static partial class VRIKCalibrator
    {
        /// <summary>
        /// The settings for VRIK tracker calibration.
        /// </summary>
        [System.Serializable]
        public class Settings : XRAsset
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
            /// <summary>
            /// Used for adjusting foot yaw relative to the foot trackers.
            /// </summary>
            [Range(-180f, 180f)]
            public float FootYawOffset
            {
                get => _footYawOffset;
                set => SetField(ref _footYawOffset, value);
            }

            private float _footInwardOffset;
            /// <summary>
            /// Inward offset of the foot bones from the foot trackers.
            /// </summary>
            public float FootInwardOffset
            {
                get => _footInwardOffset;
                set => SetField(ref _footInwardOffset, value);
            }

            private float _footForwardOffset;
            /// <summary>
            /// Forward offset of the foot bones from the foot trackers.
            /// </summary>
            public float FootForwardOffset
            {
                get => _footForwardOffset;
                set => SetField(ref _footForwardOffset, value);
            }

            private Vector3 _handOffset;
            /// <summary>
            /// Offset of the hand bones from the hand trackers in (handTrackerForward, handTrackerUp) space relative to the hand trackers.
            /// </summary>
            public Vector3 HandOffset
            {
                get => _handOffset;
                set => SetField(ref _handOffset, value);
            }

            private Vector3 _headOffset = Vector3.Zero;
            /// <summary>
            /// Offset of the head bone from the HMD in (headTrackerForward, headTrackerUp) space relative to the head tracker.
            /// </summary>
            public Vector3 HeadOffset
            {
                get => _headOffset;
                set => SetField(ref _headOffset, value);
            }

            private Vector3 _footTrackerUp = Globals.Up;
            /// <summary>
            /// Local axis of the foot tracker towards the up direction.
            /// </summary>
            public Vector3 FootTrackerUp
            {
                get => _footTrackerUp;
                set => SetField(ref _footTrackerUp, value);
            }

            private Vector3 _footTrackerForward = Globals.Backward;
            /// <summary>
            /// Local axis of the foot trackers towards the player's forward direction.
            /// </summary>
            public Vector3 FootTrackerForward
            {
                get => _footTrackerForward;
                set => SetField(ref _footTrackerForward, value);
            }

            private Vector3 _handTrackerUp = Globals.Up;
            /// <summary>
            /// Local axis of the hand trackers pointing in the direction of the surface normal of the back of the hand.
            /// </summary>
            public Vector3 HandTrackerUp
            {
                get => _handTrackerUp;
                set => SetField(ref _handTrackerUp, value);
            }

            private Vector3 _handTrackerForward = Globals.Backward;
            /// <summary>
            /// Local axis of the hand trackers pointing from the wrist towards the palm.
            /// </summary>
            public Vector3 HandTrackerForward
            {
                get => _handTrackerForward;
                set => SetField(ref _handTrackerForward, value);
            }

            private Vector3 _headTrackerUp = Globals.Up;
            /// <summary>
            /// Local axis of the HMD facing up.
            /// </summary>
            public Vector3 HeadTrackerUp
            {
                get => _headTrackerUp;
                set => SetField(ref _headTrackerUp, value);
            }

            private Vector3 _headTrackerForward = Globals.Backward;
            /// <summary>
            /// Local axis of the HMD facing forward.
            /// </summary>
            public Vector3 HeadTrackerForward
            {
                get => _headTrackerForward;
                set => SetField(ref _headTrackerForward, value);
            }

            private float _scaleMultiplier = 1.0f;
            /// <summary>
            /// Multiplies character scale.
            /// </summary>
            public float ScaleMultiplier
            {
                get => _scaleMultiplier;
                set => SetField(ref _scaleMultiplier, value);
            }
        }
    }
}
