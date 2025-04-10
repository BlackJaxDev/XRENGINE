using System.Numerics;

namespace XREngine.Data.MMD
{
    public class BoneFrameKey : IBinaryDataSource, IFramesKey
    {
        public uint FrameNumber { get; private set; }
        public Vector3 Translation { get; private set; }
        public Quaternion Rotation { get; private set; }
        public VMDBezier? TranslationXBezier { get; private set; }
        public VMDBezier? TranslationYBezier { get; private set; }
        public VMDBezier? TranslationZBezier { get; private set; }
        public VMDBezier? RotationBezier { get; private set; }

        public (Vector3 translation, Quaternion rotation) InterpolateTo(BoneFrameKey next, float time)
        {
            var pos = Interp.Lerp(Translation, next.Translation, new Vector3(
                TranslationXBezier?.EvalY(TranslationXBezier?.FindBezierX(time) ?? 0.0f) ?? 0.0f,
                TranslationYBezier?.EvalY(TranslationYBezier?.FindBezierX(time) ?? 0.0f) ?? 0.0f,
                TranslationZBezier?.EvalY(TranslationZBezier?.FindBezierX(time) ?? 0.0f) ?? 0.0f));

            var rot = Quaternion.Slerp(Rotation, next.Rotation, 
                RotationBezier?.EvalY(RotationBezier?.FindBezierX(time) ?? 0.0f) ?? 0.0f);

            return (pos, rot);
        }

        public void Load(BinaryReader reader)
        {
            FrameNumber = reader.ReadUInt32();
            Translation = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() * -1.0f) * VMDUtils.MMDUnitsToMeters;
            Rotation = InvertZAxisRotation(new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
            DeserializeInterp([.. reader.ReadBytes(64).Select(b => (sbyte)b)]);
        }

        public void Save(BinaryWriter writer)
        {
            Quaternion rotation = InvertZAxisRotation(Rotation);
            Vector3 translation = Translation * VMDUtils.MetersToMMDUnits;

            writer.Write(FrameNumber);
            writer.Write(new float[] { translation.X, translation.Y, translation.Z * -1.0f }.SelectMany(BitConverter.GetBytes).ToArray());
            writer.Write(new float[] { rotation.X, rotation.Y, rotation.Z, rotation.W }.SelectMany(BitConverter.GetBytes).ToArray());
            writer.Write(SerializeInterp().Select(b => (byte)b).ToArray());
        }

        private static void ReadEvery4Bytes(sbyte[] interp, int offset, out float x0, out float y0, out float x1, out float y1)
        {
            x0 = interp[offset] / 127.0f;
            y0 = interp[offset + 4] / 127.0f;
            x1 = interp[offset + 8] / 127.0f;
            y1 = interp[offset + 12] / 127.0f;
        }

        private void DeserializeInterp(sbyte[] interp)
        {
            ReadEvery4Bytes(interp, 0, out float x0, out float y0, out float x1, out float y1);
            TranslationXBezier = new VMDBezier(new Vector2(x0, y0), new Vector2(x1, y1));

            ReadEvery4Bytes(interp, 1, out float x2, out float y2, out float x3, out float y3);
            TranslationYBezier = new VMDBezier(new Vector2(x2, y2), new Vector2(x3, y3));

            ReadEvery4Bytes(interp, 2, out float x4, out float y4, out float x5, out float y5);
            TranslationZBezier = new VMDBezier(new Vector2(x4, y4), new Vector2(x5, y5));

            ReadEvery4Bytes(interp, 3, out float x6, out float y6, out float x7, out float y7);
            RotationBezier = new VMDBezier(new Vector2(x6, y6), new Vector2(x7, y7));
        }

        private static void WriteEvery4Bytes(sbyte[] interp, int offset, VMDBezier bezier)
        {
            interp[offset] = (sbyte)(bezier.StartControlPoint.X * 127.0f);
            interp[offset + 4] = (sbyte)(bezier.StartControlPoint.Y * 127.0f);
            interp[offset + 8] = (sbyte)(bezier.EndControlPoint.X * 127.0f);
            interp[offset + 12] = (sbyte)(bezier.EndControlPoint.Y * 127.0f);
        }

        private sbyte[] SerializeInterp()
        {
            sbyte[] interp = new sbyte[64];

            TranslationXBezier ??= new VMDBezier(Vector2.Zero, Vector2.Zero);
            WriteEvery4Bytes(interp, 0, TranslationXBezier);

            TranslationYBezier ??= new VMDBezier(Vector2.Zero, Vector2.Zero);
            WriteEvery4Bytes(interp, 1, TranslationYBezier);

            TranslationZBezier ??= new VMDBezier(Vector2.Zero, Vector2.Zero);
            WriteEvery4Bytes(interp, 2, TranslationZBezier);

            RotationBezier ??= new VMDBezier(Vector2.Zero, Vector2.Zero);
            WriteEvery4Bytes(interp, 3, RotationBezier);
            return interp;
        }

        public override string ToString()
            => $"<BoneFrameKey frame {FrameNumber}, loc {string.Join(", ", Translation)}, rot {string.Join(", ", Rotation)}>";

        public static Quaternion InvertZAxisRotation(Quaternion rot)
        {
            Matrix4x4 rotationMatrix = Matrix4x4.CreateFromQuaternion(rot);
            Matrix4x4 scaleMatrix = Matrix4x4.CreateScale(1, 1, -1);
            Matrix4x4 resultMatrix = scaleMatrix * rotationMatrix * scaleMatrix;
            return Quaternion.CreateFromRotationMatrix(resultMatrix);
        }
    }
}
