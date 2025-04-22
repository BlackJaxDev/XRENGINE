using System.Numerics;

namespace XREngine.Animation
{
    public enum ETransformOrder
    {
        TRS,
        RST,
        STR,
        TSR,
        SRT,
        RTS,
    }

    public struct TransformState
    {
        public Vector3 Translation = Vector3.Zero;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Scale = Vector3.One;
        public ETransformOrder Order = ETransformOrder.TRS;

        public TransformState()
        {

        }

        public void SetAll(Vector3 translation, Quaternion rotation, Vector3 scale)
        {
            Translation = translation;
            Rotation = rotation;
            Scale = scale;
        }
    }
}
