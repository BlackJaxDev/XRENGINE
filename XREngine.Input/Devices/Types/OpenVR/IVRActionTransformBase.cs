using System.Numerics;

namespace XREngine.Input.Devices.Types.OpenVR
{
    public interface IVRActionTransformBase<TCategory, TName>
        where TCategory : struct, Enum
        where TName : struct, Enum
    {
        public TCategory ActionCategory { get; set; }
        public TName ActionName { get; set; }
        public string ActionPath { get; }
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
    }
}
