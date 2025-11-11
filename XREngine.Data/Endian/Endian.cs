namespace XREngine.Data;

public static class Endian
{
    public enum EOrder : byte
    {
        Little = 0,
        Big = 1,
    }

    /// <summary>
    /// This is the endian that the engine de/serializer will write files and expect files to be written in.
    /// </summary>
    public static EOrder SerializeOrder { get; set; } = EOrder.Little;
    /// <summary>
    /// <see langword="true"/> if the de/serializer will read/write with big endian.
    /// </summary>
    public static bool SerializeBig
    {
        get => SerializeOrder == EOrder.Big;
        set => SerializeOrder = value ? EOrder.Big : EOrder.Little;
    }
    /// <summary>
    /// <see langword="true"/> if the de/serializer will read/write with little endian.
    /// </summary>
    public static bool SerializeLittle
    {
        get => SerializeOrder == EOrder.Little;
        set => SerializeOrder = value ? EOrder.Little : EOrder.Big;
    }

    /// <summary>
    /// This is the endian of the host OS.
    /// </summary>
    public static readonly EOrder SystemOrder;
    /// <summary>
    /// <see langword="true"/> if the host OS endian is big.
    /// </summary>
    public static bool SystemBig => SystemOrder == EOrder.Big;
    /// <summary>
    /// <see langword="true"/> if the host OS endian is little.
    /// </summary>
    public static bool SystemLittle => SystemOrder == EOrder.Little;

    static Endian()
    {
        int intValue = 1;
        unsafe { SystemOrder = *((byte*)&intValue) == 1 ? EOrder.Little : EOrder.Big; }
    }
}
