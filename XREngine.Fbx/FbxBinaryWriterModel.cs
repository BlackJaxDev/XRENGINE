namespace XREngine.Fbx;

public enum FbxBinaryArrayEncodingMode
{
    Raw,
    ZlibCompressed,
}

public sealed record FbxBinaryExportOptions
{
    public int BinaryVersion { get; init; } = 7400;
    public bool BigEndian { get; init; }
    public bool IncludeFooter { get; init; } = true;
    public FbxBinaryArrayEncodingMode ArrayEncodingMode { get; init; } = FbxBinaryArrayEncodingMode.Raw;
    public bool IncludeGlobalSettings { get; init; } = true;
    public bool IncludeDefinitions { get; init; } = true;
    public bool IncludeTakes { get; init; } = true;
}

public readonly record struct FbxBinaryProperty(FbxPropertyKind Kind, object Value)
{
    public static FbxBinaryProperty Int8(sbyte value) => new(FbxPropertyKind.Int8, value);
    public static FbxBinaryProperty Int16(short value) => new(FbxPropertyKind.Int16, value);
    public static FbxBinaryProperty Boolean(bool value) => new(FbxPropertyKind.Boolean, value);
    public static FbxBinaryProperty Byte(byte value) => new(FbxPropertyKind.Byte, value);
    public static FbxBinaryProperty Int32(int value) => new(FbxPropertyKind.Int32, value);
    public static FbxBinaryProperty Int64(long value) => new(FbxPropertyKind.Int64, value);
    public static FbxBinaryProperty Float32(float value) => new(FbxPropertyKind.Float32, value);
    public static FbxBinaryProperty Float64(double value) => new(FbxPropertyKind.Float64, value);
    public static FbxBinaryProperty String(string value) => new(FbxPropertyKind.String, value);
    public static FbxBinaryProperty Raw(byte[] value) => new(FbxPropertyKind.Raw, value);
    public static FbxBinaryProperty BooleanArray(bool[] value) => new(FbxPropertyKind.BooleanArray, value);
    public static FbxBinaryProperty ByteArray(byte[] value) => new(FbxPropertyKind.ByteArray, value);
    public static FbxBinaryProperty Int32Array(int[] value) => new(FbxPropertyKind.Int32Array, value);
    public static FbxBinaryProperty Int64Array(long[] value) => new(FbxPropertyKind.Int64Array, value);
    public static FbxBinaryProperty Float32Array(float[] value) => new(FbxPropertyKind.Float32Array, value);
    public static FbxBinaryProperty Float64Array(double[] value) => new(FbxPropertyKind.Float64Array, value);
}

public sealed class FbxBinaryNode
{
    public FbxBinaryNode(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }
    public List<FbxBinaryProperty> Properties { get; } = [];
    public List<FbxBinaryNode> Children { get; } = [];
}