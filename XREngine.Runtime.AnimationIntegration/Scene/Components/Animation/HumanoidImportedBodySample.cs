using System.Numerics;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Allocation-free staging storage for the RootT and RootQ channels of one imported Unity humanoid sample.
    /// </summary>
    public struct HumanoidImportedBodySample
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public EHumanoidImportedBodySampleChannels Channels;

        public static HumanoidImportedBodySample Neutral => new()
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Channels = EHumanoidImportedBodySampleChannels.None,
        };

        public void SetPositionComponent(EHumanoidImportedBodySampleChannels channel, float value)
        {
            switch (channel)
            {
                case EHumanoidImportedBodySampleChannels.PositionX:
                    Position.X = value;
                    break;
                case EHumanoidImportedBodySampleChannels.PositionY:
                    Position.Y = value;
                    break;
                case EHumanoidImportedBodySampleChannels.PositionZ:
                    Position.Z = value;
                    break;
                default:
                    return;
            }

            Channels |= channel;
        }

        public void SetRotationComponent(EHumanoidImportedBodySampleChannels channel, float value)
        {
            switch (channel)
            {
                case EHumanoidImportedBodySampleChannels.RotationX:
                    Rotation.X = value;
                    break;
                case EHumanoidImportedBodySampleChannels.RotationY:
                    Rotation.Y = value;
                    break;
                case EHumanoidImportedBodySampleChannels.RotationZ:
                    Rotation.Z = value;
                    break;
                case EHumanoidImportedBodySampleChannels.RotationW:
                    Rotation.W = value;
                    break;
                default:
                    return;
            }

            Channels |= channel;
        }
    }
}
