using XREngine.Extensions;
using System.Numerics;
using XREngine.Data.Colors;

namespace XREngine.Data.MMD
{
    public class LampKeyFrameKey : IBinaryDataSource
    {
        public uint FrameNumber { get; private set; }
        public ColorF3 Color { get; private set; }
        public Vector3 Direction { get; private set; }

        public void Load(BinaryReader reader)
        {
            FrameNumber = reader.ReadUInt32();
            Color = new Vector3(reader.ReadBytes(12).SelectEvery(4, x => BitConverter.ToSingle([.. x], 0)).ToArray());
            Direction = new Vector3(reader.ReadBytes(12).SelectEvery(4, x => BitConverter.ToSingle([.. x], 0)).ToArray());
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write(FrameNumber);
            writer.Write(new float[] { Color.R, Color.G, Color.B }.SelectMany(BitConverter.GetBytes).ToArray());
            writer.Write(new float[] { Direction.X, Direction.Y, Direction.Z }.SelectMany(BitConverter.GetBytes).ToArray());
        }

        public override string ToString()
            => $"<LampKeyFrameKey frame {FrameNumber}, color {string.Join(", ", Color)}, direction {string.Join(", ", Direction)}>";
    }
}
