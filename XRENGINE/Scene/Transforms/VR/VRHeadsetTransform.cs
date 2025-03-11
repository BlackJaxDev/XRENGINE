using System.Numerics;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// The transform for the VR headset.
    /// </summary>
    /// <param name="parent"></param>
    public class VRHeadsetTransform : TransformBase
    {
        public VRHeadsetTransform()
            => Engine.VRState.RecalcMatrixOnDraw += VRState_RecalcMatrixOnDraw;
        public VRHeadsetTransform(TransformBase parent)
            : base(parent)
            => Engine.VRState.RecalcMatrixOnDraw += VRState_RecalcMatrixOnDraw;

        private void VRState_RecalcMatrixOnDraw()
        {
            _lastVRMatrixUpdate = Engine.VRState.Api.Headset?.RenderDeviceToAbsoluteTrackingMatrix ?? Matrix4x4.Identity;
            MarkLocalModified();
            RecalculateMatrixHeirarchy(true, true, true);
        }

        private Matrix4x4 _lastVRMatrixUpdate = Matrix4x4.Identity;
        protected override Matrix4x4 CreateLocalMatrix()
            => _lastVRMatrixUpdate;
    }
}