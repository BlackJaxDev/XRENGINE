using System.Numerics;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// The transform for the VR headset.
    /// </summary>
    /// <param name="parent"></param>
    public class VRHeadsetTransform : TransformBase
    {
        public VRHeadsetTransform() { }
        public VRHeadsetTransform(TransformBase parent) : base(parent) { }

        protected internal override void OnSceneNodeActivated()
        {
            base.OnSceneNodeActivated();
            Engine.VRState.RecalcMatrixOnDraw += VRState_RecalcMatrixOnDraw;
            Engine.Time.Timer.PreUpdateFrame += MarkLocalModified;
        }
        protected internal override void OnSceneNodeDeactivated()
        {
            base.OnSceneNodeDeactivated();
            Engine.VRState.RecalcMatrixOnDraw -= VRState_RecalcMatrixOnDraw;
            Engine.Time.Timer.PreUpdateFrame -= MarkLocalModified;
        }

        private void VRState_RecalcMatrixOnDraw()
            => SetRenderMatrix((Engine.VRState.Api.Headset?.RenderDeviceToAbsoluteTrackingMatrix ?? Matrix4x4.Identity) * ParentRenderMatrix, true);

        protected override Matrix4x4 CreateLocalMatrix()
            => Engine.VRState.Api.Headset?.DeviceToAbsoluteTrackingMatrix ?? Matrix4x4.Identity;
    }
}