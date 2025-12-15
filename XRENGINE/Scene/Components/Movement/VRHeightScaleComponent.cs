namespace XREngine.Components
{
    /// <summary>
    /// Handles all scaling of the avatar's height to synchronize it with the real-world height of the user.
    /// </summary>
    public class VRHeightScaleComponent : HeightScaleBaseComponent
    {
        public VRHeightScaleComponent()
        {
            Engine.VRState.ModelHeightChanged += UpdateHeightScale;
            Engine.VRState.DesiredAvatarHeightChanged += UpdateHeightScale;
            Engine.VRState.RealWorldHeightChanged += UpdateHeightScale;
        }
        protected override void OnDestroying()
        {
            base.OnDestroying();
            Engine.VRState.ModelHeightChanged -= UpdateHeightScale;
            Engine.VRState.DesiredAvatarHeightChanged -= UpdateHeightScale;
            Engine.VRState.RealWorldHeightChanged -= UpdateHeightScale;
        }

        private void UpdateHeightScale(float _)
            => UpdateHeightScale();

        protected override float ModelToRealWorldHeightRatio
            => Engine.VRState.ModelToRealWorldHeightRatio;
        protected override float ModelHeightMeters
            => Engine.VRState.ModelHeight;

        public override void ApplyMeasuredHeight(float modelHeight)
            => Engine.VRState.ModelHeight = modelHeight;
    }
}