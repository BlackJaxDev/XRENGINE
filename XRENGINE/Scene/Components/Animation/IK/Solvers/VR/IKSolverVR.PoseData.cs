using System.Numerics;

namespace XREngine.Components.Animation
{
public partial class IKSolverVR
    {
        public struct PoseData
        {
            public Vector3 Translation;
            public Quaternion Rotation;

            public PoseData()
            {
                Translation = Vector3.Zero;
                Rotation = Quaternion.Identity;
            }
            public PoseData(Vector3 translation, Quaternion rotation)
            {
                Translation = translation;
                Rotation = rotation;
            }
        }
    }
}
