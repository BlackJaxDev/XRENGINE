using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Animation
{
    public struct TransformState
    {
        public Vector3 Translation = Vector3.Zero;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Scale = Vector3.One;
        public Transform.EOrder Order = Transform.EOrder.TRS;

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
