namespace XREngine
{
    /// <summary>
    /// This object can be serialized as a string.
    /// </summary>
    public interface ISerializableString
    {
        string WriteToString();
        void ReadFromString(string str);
    }
}
