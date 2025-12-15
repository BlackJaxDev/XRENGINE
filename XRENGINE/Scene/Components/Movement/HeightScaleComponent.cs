using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering.Models;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    /// <summary>
    /// Handles scaling of an avatar's height using user-provided values (no VRState dependency).
    /// Scales the avatar root transform and optionally updates a character capsule (radius/standing/crouch/prone).
    /// </summary>
    public class HeightScaleComponent : HeightScaleBaseComponent
    {
        /// <summary>
        /// The avatar's measured (model-space) height in meters.
        /// Used together with <see cref="ModelToRealWorldHeightRatio"/> to compute the scaled height.
        /// </summary>
        protected override float ModelHeightMeters => _modelHeightMeters;
        private float _modelHeightMeters = 1.0f;

        /// <summary>
        /// Scalar applied to the avatar root transform to reach the desired height.
        /// A value of 1 means "no scaling".
        /// </summary>
        protected override float ModelToRealWorldHeightRatio => _heightScaleRatio;
        private float _heightScaleRatio = 1.0f;

        public float RealWorldHeightRatio
        {
            get => _heightScaleRatio;
            set => SetField(ref _heightScaleRatio, value, nameof(ModelToRealWorldHeightRatio));
        }

        public override void ApplyMeasuredHeight(float modelHeight)
            => SetField(ref _modelHeightMeters, modelHeight, nameof(ModelHeightMeters));
    }
}
