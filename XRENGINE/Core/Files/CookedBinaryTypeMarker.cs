namespace XREngine.Core.Files;

internal enum CookedBinaryTypeMarker : byte
{
    Null = 0,
    Boolean = 1,
    Byte = 2,
    SByte = 3,
    Int16 = 4,
    UInt16 = 5,
    Int32 = 6,
    UInt32 = 7,
    Int64 = 8,
    UInt64 = 9,
    Single = 10,
    Double = 11,
    Decimal = 12,
    Char = 13,
    String = 14,
    Guid = 15,
    DateTime = 16,
    TimeSpan = 17,
    ByteArray = 18,
    Enum = 19,
    Array = 20,
    List = 21,
    Dictionary = 22,
    Object = 23,
    CustomObject = 24,
    DataSource = 25
}
