using XREngine.Data;

namespace XREngine
{
    /// <summary>
    /// This object can be serialized to/from a pointer.
    /// </summary>
    public interface ISerializablePointer
    {
        int GetSize();
        void WriteToPointer(VoidPtr address);
        void ReadFromPointer(VoidPtr address, int size);
    }
}
