using System.IO.MemoryMappedFiles;
using System.Text;

namespace XREngine.Fbx;

public enum FbxReaderStrictness
{
    Strict,
    Tolerant,
}

[Flags]
public enum FbxNodeFlags
{
    None = 0,
    SkippedSubtree = 1 << 0,
}

public enum FbxPropertyKind
{
    Int8,
    Int16,
    Boolean,
    Byte,
    Int32,
    Float32,
    Float64,
    Int64,
    String,
    Raw,
    BooleanArray,
    ByteArray,
    Int32Array,
    Int64Array,
    Float32Array,
    Float64Array,
    AsciiScalar,
    AsciiArray,
}

public sealed record FbxReaderOptions
{
    public static FbxReaderOptions Strict { get; } = new();

    public FbxReaderStrictness Strictness { get; init; } = FbxReaderStrictness.Strict;
    public IReadOnlyList<string> SkippedNodeNames { get; init; } = Array.Empty<string>();
}

public sealed class FbxParseException : IOException
{
    public FbxParseException(string message, long offset)
        : base($"{message} (offset {offset}).")
        => Offset = offset;

    public long Offset { get; }
}

public readonly record struct FbxHeaderInfo(
    FbxTransportEncoding Encoding,
    string? VersionText,
    int? BinaryVersion,
    bool IsBigEndian,
    int HeaderLength);

public readonly record struct FbxFooterInfo(
    int Offset,
    int Length,
    int PaddingLength,
    int Version,
    bool VersionMatchesHeader);

public readonly record struct FbxNodeRecord(
    int Index,
    int ParentIndex,
    int Depth,
    int NameOffset,
    int NameLength,
    int FirstPropertyIndex,
    int PropertyCount,
    long EndOffset,
    FbxNodeFlags Flags);

public readonly record struct FbxPropertyRecord(
    int NodeIndex,
    FbxPropertyKind Kind,
    int DataOffset,
    int DataLength,
    uint ArrayLength,
    uint Encoding,
    int ElementSize);

public readonly record struct FbxArrayWorkItem(
    int PropertyIndex,
    int PayloadOffset,
    int PayloadLength,
    uint ArrayLength,
    uint Encoding,
    int ElementSize)
{
    public int ExpectedDecodedLength => checked((int)ArrayLength * ElementSize);
}

public sealed class FbxStructuralDocument
    : IDisposable
{
    private readonly FbxSourceBuffer _source;

    internal FbxStructuralDocument(
        FbxSourceBuffer source,
        FbxHeaderInfo header,
        FbxNodeRecord[] nodes,
        FbxPropertyRecord[] properties,
        FbxArrayWorkItem[] arrayWorkItems,
        FbxFooterInfo? footer,
        int maxDepth)
    {
        _source = source;
        Header = header;
        Nodes = nodes;
        Properties = properties;
        ArrayWorkItems = arrayWorkItems;
        Footer = footer;
        MaxDepth = maxDepth;
    }

    public FbxHeaderInfo Header { get; }
    public IReadOnlyList<FbxNodeRecord> Nodes { get; }
    public IReadOnlyList<FbxPropertyRecord> Properties { get; }
    public IReadOnlyList<FbxArrayWorkItem> ArrayWorkItems { get; }
    public FbxFooterInfo? Footer { get; }
    public int MaxDepth { get; }
    public int RootNodeCount => Nodes.Count(static node => node.ParentIndex < 0);

    public ReadOnlySpan<byte> Source => _source.Span;

    public ReadOnlySpan<byte> GetNodeNameBytes(FbxNodeRecord node)
        => _source.Slice(node.NameOffset, node.NameLength);

    public string GetNodeName(FbxNodeRecord node)
        => Encoding.UTF8.GetString(GetNodeNameBytes(node));

    public ReadOnlySpan<byte> GetPropertyData(FbxPropertyRecord property)
        => _source.Slice(property.DataOffset, property.DataLength);

    public string GetAsciiPropertyText(FbxPropertyRecord property)
        => Encoding.UTF8.GetString(GetPropertyData(property));

    public void Dispose()
        => _source.Dispose();
}

internal abstract unsafe class FbxSourceBuffer : IDisposable
{
    public abstract int Length { get; }
    public abstract ReadOnlySpan<byte> Span { get; }

    public ReadOnlySpan<byte> Slice(int offset, int length)
        => Span.Slice(offset, length);

    public abstract byte[] ToArray();

    public abstract void Dispose();
}

internal sealed class ManagedFbxSourceBuffer(byte[] buffer) : FbxSourceBuffer
{
    private readonly byte[] _buffer = buffer;

    public override int Length => _buffer.Length;
    public override ReadOnlySpan<byte> Span => _buffer;
    public override byte[] ToArray() => _buffer;
    public override void Dispose() { }
}

internal sealed unsafe class MemoryMappedFbxSourceBuffer : FbxSourceBuffer
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly int _length;
    private byte* _pointer;
    private bool _disposed;

    public MemoryMappedFbxSourceBuffer(string path)
    {
        FileInfo fileInfo = new(path);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"FBX file '{path}' does not exist.", path);
        if (fileInfo.Length > int.MaxValue)
            throw new IOException($"FBX file '{path}' exceeds the supported 2 GB structural-scan limit.");

        _length = checked((int)fileInfo.Length);
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        _view = _file.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _pointer = pointer + _view.PointerOffset;
    }

    public override int Length => _length;

    public override ReadOnlySpan<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new ReadOnlySpan<byte>(_pointer, _length);
        }
    }

    public override byte[] ToArray()
    {
        byte[] buffer = GC.AllocateUninitializedArray<byte>(_length);
        Span.CopyTo(buffer);
        return buffer;
    }

    public override void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_pointer is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        _view.Dispose();
        _file.Dispose();
    }
}
