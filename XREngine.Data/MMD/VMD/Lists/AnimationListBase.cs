namespace XREngine.Data.MMD
{
    public abstract class AnimationListBase<T> : List<T> where T : IBinaryDataSource, new()
    {
        public void Load(BinaryReader reader)
        {
            uint count = reader.ReadUInt32();
            for (int i = 0; i < count; i++)
            {
                T frameKey = new();
                frameKey.Load(reader);
                Add(frameKey);
            }
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write((uint)Count);
            foreach (var frameKey in this)
                frameKey.Save(writer);
        }
    }
}
