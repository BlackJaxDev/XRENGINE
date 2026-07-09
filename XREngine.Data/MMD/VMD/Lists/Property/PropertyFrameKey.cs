namespace XREngine.Data.MMD
{
    public class PropertyFrameKey : IBinaryDataSource
    {
        public uint FrameNumber { get; private set; }
        public bool Visible { get; private set; }
        public List<(string IkName, bool State)> IkStates { get; private set; } = [];

        public void Load(BinaryReader reader)
        {
            FrameNumber = reader.ReadUInt32();
            Visible = reader.ReadByte() == 1;
            uint count = reader.ReadUInt32();
            for (int i = 0; i < count; i++)
            {
                string ikName = VMDUtils.ToShiftJisString(reader.ReadBytes(20));
                if (VMDUtils.JP2EN.TryGetValue(ikName, out string? enName))
                    ikName = enName;
                bool state = reader.ReadByte() == 1;
                IkStates.Add((ikName, state));
            }
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write(FrameNumber);
            writer.Write(Visible ? (byte)1 : (byte)0);
            writer.Write((uint)IkStates.Count);
            foreach (var (ikName, state) in IkStates)
            {
                writer.Write(VMDUtils.ToShiftJisBytes(ikName).Concat(new byte[20 - ikName.Length]).ToArray());
                writer.Write(state ? (byte)1 : (byte)0);
            }
        }

        public override string ToString()
            => $"<PropertyFrameKey frame {FrameNumber}, visible {Visible}, ik_states {string.Join(", ", IkStates.Select(ik => $"{ik.IkName}: {ik.State}"))}>";
    }
}
