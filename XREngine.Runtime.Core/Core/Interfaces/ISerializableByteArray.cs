namespace XREngine
{
    /// <summary>
    /// This object can be serialized as a byte array.
    /// </summary>
    public interface ISerializableByteArray
    {
        byte[] WriteToBytes();
        void ReadFromBytes(byte[] bytes);
    }
}
