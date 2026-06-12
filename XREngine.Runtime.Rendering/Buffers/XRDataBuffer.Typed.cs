using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;

namespace XREngine.Rendering;

/// <summary>
/// Typed facade over <see cref="XRDataBuffer"/>. The engine-facing resource identity remains the base buffer.
/// </summary>
public class XRDataBuffer<T> : XRDataBuffer where T : unmanaged
{
    private uint _typedElementCount;

    public XRDataBuffer()
    {
        ConfigureTypedDefaults();
    }

    public XRDataBuffer(
        string bindingName,
        EBufferTarget target,
        uint elementCount = 0u,
        bool normalize = false,
        bool integral = false,
        bool padEndingToVec4 = false,
        bool alignClientSourceToPowerOf2 = false)
        : base(
            bindingName,
            target,
            Math.Max(elementCount, 1u),
            ResolveComponentType(),
            ResolveComponentCount(),
            normalize,
            integral,
            alignClientSourceToPowerOf2)
    {
        _typedElementCount = elementCount;
        ConfigureTypedDefaults();
        PadEndingToVec4 = padEndingToVec4;
        ElementCount = elementCount;
    }

    public uint TypedElementCount => _typedElementCount;
    public uint TypedElementSize => (uint)Unsafe.SizeOf<T>();

    public XRBufferWriter<T> Alloc(uint count)
        => base.Alloc<T>(count);

    public XRBufferWriter<T> Alloc(uint count, XRBufferWriteMode mode)
        => base.Alloc<T>(count, mode);

    public XRBufferWriter<T> Alloc(uint count, XRBufferWriteOptions options)
        => base.Alloc<T>(count, options);

    public unsafe Span<T> GetCpuMirrorSpan()
    {
        if (ClientSideSource is not { } source || source.Address == VoidPtr.Zero || source.Length == 0)
            return Span<T>.Empty;

        uint count = Math.Min(ElementCount, source.Length / TypedElementSize);
        return new Span<T>(source.Address.Pointer, checked((int)count));
    }

    public void SetData(ReadOnlySpan<T> data)
    {
        if (IsCpuMirrorUnchanged(data))
            return;

        using XRBufferWriter<T> writer = Alloc((uint)data.Length, XRBufferWriteMode.Discard);
        data.CopyTo(writer.Span);
        SetField(ref _typedElementCount, (uint)data.Length, nameof(TypedElementCount));
    }

    public void Write(uint elementOffset, ReadOnlySpan<T> data)
    {
        if (data.IsEmpty)
            return;

        XRBufferWriteOptions options = XRBufferWriteOptions.FromBuffer(this).WithWriteMode(XRBufferWriteMode.Preserve);
        using XRBufferWriter<T> writer = base.Alloc<T>(Math.Max(ElementCount, elementOffset + (uint)data.Length), options);
        data.CopyTo(writer.Span.Slice(checked((int)elementOffset), data.Length));
        writer.MarkDirty(elementOffset, (uint)data.Length);
        SetField(ref _typedElementCount, Math.Max(_typedElementCount, elementOffset + (uint)data.Length), nameof(TypedElementCount));
    }

    private unsafe bool IsCpuMirrorUnchanged(ReadOnlySpan<T> data)
    {
        if (ClientSideSource is not { } source)
            return false;

        uint byteLength = checked((uint)(data.Length * Unsafe.SizeOf<T>()));
        if (source.Length < byteLength || _typedElementCount != (uint)data.Length)
            return false;

        if (byteLength == 0u)
            return true;

        ReadOnlySpan<byte> sourceBytes = new(source.Address.Pointer, checked((int)byteLength));
        ReadOnlySpan<byte> incomingBytes = MemoryMarshal.AsBytes(data);
        return sourceBytes.SequenceEqual(incomingBytes);
    }

    private void ConfigureTypedDefaults()
    {
        PadEndingToVec4 = false;
        DefaultAlignmentBytes = TypedElementSize;
        DefaultWriteMode = XRBufferWriteMode.DiscardOrRing;
    }

    private static EComponentType ResolveComponentType()
    {
        Type t = typeof(T);
        if (t == typeof(sbyte)) return EComponentType.SByte;
        if (t == typeof(byte)) return EComponentType.Byte;
        if (t == typeof(short)) return EComponentType.Short;
        if (t == typeof(ushort)) return EComponentType.UShort;
        if (t == typeof(int)) return EComponentType.Int;
        if (t == typeof(uint)) return EComponentType.UInt;
        if (t == typeof(float)) return EComponentType.Float;
        if (t == typeof(double)) return EComponentType.Double;
        if (t == typeof(Vector2)) return EComponentType.Float;
        if (t == typeof(Vector3)) return EComponentType.Float;
        if (t == typeof(Vector4)) return EComponentType.Float;
        if (t == typeof(Matrix4x4)) return EComponentType.Float;
        if (t == typeof(Quaternion)) return EComponentType.Float;
        if (t == typeof(IVector2)) return EComponentType.Int;
        return EComponentType.Struct;
    }

    private static uint ResolveComponentCount()
    {
        Type t = typeof(T);
        if (t == typeof(Vector2) || t == typeof(IVector2)) return 2u;
        if (t == typeof(Vector3)) return 3u;
        if (t == typeof(Vector4) || t == typeof(Quaternion)) return 4u;
        if (t == typeof(Matrix4x4)) return 16u;
        return ResolveComponentType() == EComponentType.Struct ? (uint)Unsafe.SizeOf<T>() : 1u;
    }
}
