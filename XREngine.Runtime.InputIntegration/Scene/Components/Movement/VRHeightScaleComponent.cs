using XREngine.Input;

namespace XREngine.Components
{
    /// <summary>
    /// Handles all scaling of the avatar's height to synchronize it with the real-world height of the user.
    /// </summary>
    public class VRHeightScaleComponent : HeightScaleBaseComponent
    {
        public VRHeightScaleComponent()
        {
            RuntimeVrStateServices.ModelHeightChanged += UpdateHeightScale;
            RuntimeVrStateServices.DesiredAvatarHeightChanged += UpdateHeightScale;
            RuntimeVrStateServices.RealWorldHeightChanged += UpdateHeightScale;
        }
        protected override void OnDestroying()
        {
            base.OnDestroying();
            RuntimeVrStateServices.ModelHeightChanged -= UpdateHeightScale;
            RuntimeVrStateServices.DesiredAvatarHeightChanged -= UpdateHeightScale;
            RuntimeVrStateServices.RealWorldHeightChanged -= UpdateHeightScale;
        }

        private void UpdateHeightScale(float _)
            => UpdateHeightScale();

        protected override float ModelToRealWorldHeightRatio
            => RuntimeVrStateServices.ModelToRealWorldHeightRatio;
        protected override float ModelHeightMeters
            => RuntimeVrStateServices.ModelHeight;

        public override void ApplyMeasuredHeight(float modelHeight)
            => RuntimeVrStateServices.ModelHeight = modelHeight;
    }
}