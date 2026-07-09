using XREngine.Extensions;
using System.Numerics;

namespace XREngine.Data.MMD
{
    public class CameraKeyFrameKey : IBinaryDataSource
    {
        public uint FrameNumber { get; private set; }
        public float Distance { get; private set; }
        public Vector3 Location { get; private set; }
        public Vector3 Rotation { get; private set; }
        public sbyte[] Interp { get; private set; } = new sbyte[24];
        public uint Angle { get; private set; }
        public bool Persp { get; private set; }

        public void Load(BinaryReader reader)
        {
            FrameNumber = reader.ReadUInt32();
            Distance = reader.ReadSingle();
            Location = new Vector3(reader.ReadBytes(12).SelectEvery(4, x => BitConverter.ToSingle([.. x], 0)).ToArray());
            Rotation = new Vector3(reader.ReadBytes(12).SelectEvery(4, x => BitConverter.ToSingle([.. x], 0)).ToArray());
            Interp = reader.ReadBytes(24).Select(b => (sbyte)b).ToArray();
            Angle = reader.ReadUInt32();
            Persp = reader.ReadByte() == 0;
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write(FrameNumber);
            writer.Write(Distance);
            writer.Write(new float[] { Location.X, Location.Y, Location.Z }.SelectMany(BitConverter.GetBytes).ToArray());
            writer.Write(new float[] { Rotation.X, Rotation.Y, Rotation.Z }.SelectMany(BitConverter.GetBytes).ToArray());
            writer.Write(Interp.Select(b => (byte)b).ToArray());
            writer.Write(Angle);
            writer.Write((byte)(Persp ? 0 : 1));
        }

        public override string ToString()
            => $"<CameraKeyFrameKey frame {FrameNumber}, distance {Distance}, loc {string.Join(", ", Location)}, rot {string.Join(", ", Rotation)}, angle {Angle}, persp {Persp}>";
    }
}
