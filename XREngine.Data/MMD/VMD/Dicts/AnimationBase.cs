using System.Diagnostics;
using System.Text;

namespace XREngine.Data.MMD
{
    public abstract class AnimationBase<T> : Dictionary<string, FrameDictionary<T>>, IBinaryDataSource where T : class, IBinaryDataSource, IFramesKey, new()
    {
        public uint MaxFrameCount { get; set; }

        public void Load(BinaryReader reader)
        {
            uint count = reader.ReadUInt32();
            HashSet<string> boneNames = [];
            for (int i = 0; i < count; i++)
            {
                string name = VMDUtils.ToShiftJisString(reader.ReadBytes(15));
                if (VMDUtils.JP2EN.TryGetValue(name, out string? value))
                    name = value;
                else
                    boneNames.Add(name);
                T frameKey = new();
                frameKey.Load(reader);
                if (!ContainsKey(name))
                    this[name] = [];
                this[name].Add(frameKey);
                MaxFrameCount = Math.Max(MaxFrameCount, frameKey.FrameNumber);
            }
            if (boneNames.Count > 0)
                Debug.WriteLine($"Unknown bone names: {string.Join(", ", boneNames)}");
        }

        public void Save(BinaryWriter writer)
        {
            uint count = (uint)this.Sum(kv => kv.Value.Count);
            writer.Write(count);
            foreach (var kv in this)
            {
                byte[] nameBytes = [.. VMDUtils.ToShiftJisBytes(kv.Key), .. new byte[15 - kv.Key.Length]];
                foreach (var frameKey in kv.Value)
                {
                    writer.Write(nameBytes);
                    frameKey.Value.Save(writer);
                }
            }
        }

        public override string ToString()
        {
            StringBuilder sb = new();
            foreach (var kv in this)
            {
                sb.AppendLine($"Bone: {kv.Key}");
                foreach (var frameKey in kv.Value)
                    sb.AppendLine($"  {frameKey.Value}");
            }
            return sb.ToString();
        }
    }
}
