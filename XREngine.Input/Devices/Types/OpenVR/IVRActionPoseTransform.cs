using System.Numerics;

namespace XREngine.Input.Devices.Types.OpenVR
{
    public interface IVRActionPoseTransform<TCategory, TName> : IVRActionTransformBase<TCategory, TName>
        where TCategory : struct, Enum
        where TName : struct, Enum
    {
        public bool QueryNextFrame { get; set; }
        public float QuerySecondsFromNow { get; set; }
        public Vector3 Velocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
    }
}
