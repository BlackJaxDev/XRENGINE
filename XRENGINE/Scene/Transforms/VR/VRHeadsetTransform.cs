using OpenVR.NET.Devices;
using XREngine.Data.Components.Scene;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// The transform for the VR headset.
    /// </summary>
    /// <param name="parent"></param>
    public class VRHeadsetTransform : VRDeviceTransformBase
    {
        public VRHeadsetTransform() { }
        public VRHeadsetTransform(TransformBase parent) : base(parent) { }

        public override VrDevice? Device => Engine.VRState.Api.Headset;
    }
}