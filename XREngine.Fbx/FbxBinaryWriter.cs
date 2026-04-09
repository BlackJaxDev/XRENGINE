using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace XREngine.Fbx;

public static class FbxBinaryWriter
{
    private static readonly byte[] BinaryHeaderMagic =
    [
        (byte)'K', (byte)'a', (byte)'y', (byte)'d', (byte)'a', (byte)'r', (byte)'a', (byte)' ',
        (byte)'F', (byte)'B', (byte)'X', (byte)' ', (byte)'B', (byte)'i', (byte)'n', (byte)'a',
        (byte)'r', (byte)'y', (byte)' ', (byte)' ', 0x00, 0x1A,
    ];

    private static readonly byte[] FooterIdMagic =
    [
        0xFA, 0xBC, 0xAB, 0x09, 0xD0, 0xC8, 0xD4, 0x66,
        0xB1, 0x76, 0xFB, 0x83, 0x1C, 0xF7, 0x26, 0x7E,
    ];

    private static readonly byte[] FooterTerminalMagic =
    [
        0xF8, 0x5A, 0x8C, 0x6A, 0xDE, 0xF5, 0xD9, 0x7E,
        0xEC, 0xE9, 0x0C, 0xE3, 0x75, 0x8F, 0x29, 0x0B,
    ];

    public static byte[] WriteToArray(IReadOnlyList<FbxBinaryNode> rootNodes, FbxBinaryExportOptions? options = null)
    {
        using MemoryStream stream = new();
        Write(stream, rootNodes, options);
        return stream.ToArray();
    }

    public static void Write(Stream stream, IReadOnlyList<FbxBinaryNode> rootNodes, FbxBinaryExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(rootNodes);

        FbxTrace.TraceOperation(
            "BinaryWriter",
            $"Writing binary FBX stream (rootNodes={rootNodes.Count:N0}, options={DescribeOptions(options)}).",
            () =>
            {
                options ??= new FbxBinaryExportOptions();
                ValidateOptions(options);

                EncodedNode[] encodedRoots = [.. rootNodes.Select(node => PrepareNode(node, options))];

                if (FbxTrace.IsEnabled(FbxLogVerbosity.Verbose))
                {
                    long serializedPayloadBytes = encodedRoots.Sum(static node => node.SerializedSize);
                    FbxTrace.Verbose(
                        "BinaryWriter",
                        $"Prepared {encodedRoots.Length:N0} encoded root node(s) with serializedPayloadBytes={serializedPayloadBytes:N0} before header/footer emission.");
                }

                WriteHeader(stream, options);
                long absoluteOffset = stream.Position;
                foreach (EncodedNode root in encodedRoots)
                    WriteNode(stream, root, ref absoluteOffset, options);

                WriteSentinel(stream, options.BinaryVersion);
                if (options.IncludeFooter)
                    WriteFooter(stream, options);
            },
            () => $"Wrote binary FBX stream with rootNodes={rootNodes.Count:N0}, finalPosition={(stream.CanSeek ? stream.Position.ToString("N0") : "n/a")}");
    }

    private static string DescribeOptions(FbxBinaryExportOptions? options)
    {
        FbxBinaryExportOptions resolvedOptions = options ?? new FbxBinaryExportOptions();
        return $"version={resolvedOptions.BinaryVersion}, bigEndian={resolvedOptions.BigEndian}, footer={resolvedOptions.IncludeFooter}, arrayEncoding={resolvedOptions.ArrayEncodingMode}";
    }

    private static void ValidateOptions(FbxBinaryExportOptions options)
    {
        if (options.BinaryVersion is not (7400 or 7500))
            throw new ArgumentOutOfRangeException(nameof(options), "FBX binary export currently supports only versions 7400 and 7500.");
    }

    private static EncodedNode PrepareNode(FbxBinaryNode node, FbxBinaryExportOptions options)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(node.Name);
        EncodedProperty[] properties = new EncodedProperty[node.Properties.Count];
        int propertyListLength = 0;
        for (int index = 0; index < node.Properties.Count; index++)
        {
            properties[index] = EncodeProperty(node.Properties[index], options);
            propertyListLength += properties[index].SerializedSize;
        }

        EncodedNode[] children = new EncodedNode[node.Children.Count];
        long childLength = 0;
        for (int index = 0; index < node.Children.Count; index++)
        {
            children[index] = PrepareNode(node.Children[index], options);
            childLength += children[index].SerializedSize;
        }

        long serializedSize = GetNodeHeaderSize(options.BinaryVersion) + nameBytes.Length + propertyListLength + childLength + GetNodeHeaderSize(options.BinaryVersion);
        return new EncodedNode(nameBytes, properties, children, serializedSize, propertyListLength);
    }

    private static EncodedProperty EncodeProperty(FbxBinaryProperty property, FbxBinaryExportOptions options)
    {
        byte typeCode = MapBinaryTypeCode(property.Kind);
        byte[] payload = property.Kind switch
        {
            FbxPropertyKind.Int8 => [(byte)(sbyte)property.Value],
            FbxPropertyKind.Int16 => EncodeInt16((short)property.Value, options.BigEndian),
            FbxPropertyKind.Boolean => [(bool)property.Value ? (byte)1 : (byte)0],
            FbxPropertyKind.Byte => [(byte)property.Value],
            FbxPropertyKind.Int32 => EncodeInt32((int)property.Value, options.BigEndian),
            FbxPropertyKind.Float32 => EncodeFloat32((float)property.Value, options.BigEndian),
            FbxPropertyKind.Float64 => EncodeFloat64((double)property.Value, options.BigEndian),
            FbxPropertyKind.Int64 => EncodeInt64((long)property.Value, options.BigEndian),
            FbxPropertyKind.String => EncodeBlob(Encoding.UTF8.GetBytes((string)property.Value), options.BigEndian),
            FbxPropertyKind.Raw => EncodeBlob((byte[])property.Value, options.BigEndian),
            FbxPropertyKind.BooleanArray => EncodeArrayPayload((bool[])property.Value, options),
            FbxPropertyKind.ByteArray => EncodeArrayPayload((byte[])property.Value, options),
            FbxPropertyKind.Int32Array => EncodeArrayPayload((int[])property.Value, options),
            FbxPropertyKind.Int64Array => EncodeArrayPayload((long[])property.Value, options),
            FbxPropertyKind.Float32Array => EncodeArrayPayload((float[])property.Value, options),
            FbxPropertyKind.Float64Array => EncodeArrayPayload((double[])property.Value, options),
            _ => throw new NotSupportedException($"Unsupported FBX binary property kind '{property.Kind}' for export."),
        };

        return new EncodedProperty(typeCode, payload);
    }

    private static byte[] EncodeArrayPayload(bool[] values, FbxBinaryExportOptions options)
    {
        byte[] rawPayload = new byte[values.Length];
        for (int index = 0; index < values.Length; index++)
            rawPayload[index] = values[index] ? (byte)1 : (byte)0;
        return EncodeArrayHeaderAndPayload(rawPayload, values.Length, options);
    }

    private static byte[] EncodeArrayPayload(byte[] values, FbxBinaryExportOptions options)
        => EncodeArrayHeaderAndPayload(values, values.Length, options);

    private static byte[] EncodeArrayPayload(int[] values, FbxBinaryExportOptions options)
    {
        byte[] rawPayload = new byte[values.Length * sizeof(int)];
        for (int index = 0; index < values.Length; index++)
        {
            Span<byte> element = rawPayload.AsSpan(index * sizeof(int), sizeof(int));
            if (options.BigEndian)
                BinaryPrimitives.WriteInt32BigEndian(element, values[index]);
            else
                BinaryPrimitives.WriteInt32LittleEndian(element, values[index]);
        }

        return EncodeArrayHeaderAndPayload(rawPayload, values.Length, options);
    }

    private static byte[] EncodeArrayPayload(long[] values, FbxBinaryExportOptions options)
    {
        byte[] rawPayload = new byte[values.Length * sizeof(long)];
        for (int index = 0; index < values.Length; index++)
        {
            Span<byte> element = rawPayload.AsSpan(index * sizeof(long), sizeof(long));
            if (options.BigEndian)
                BinaryPrimitives.WriteInt64BigEndian(element, values[index]);
            else
                BinaryPrimitives.WriteInt64LittleEndian(element, values[index]);
        }

        return EncodeArrayHeaderAndPayload(rawPayload, values.Length, options);
    }

    private static byte[] EncodeArrayPayload(float[] values, FbxBinaryExportOptions options)
    {
        byte[] rawPayload = new byte[values.Length * sizeof(float)];
        for (int index = 0; index < values.Length; index++)
        {
            Span<byte> element = rawPayload.AsSpan(index * sizeof(float), sizeof(float));
            if (options.BigEndian)
                BinaryPrimitives.WriteSingleBigEndian(element, values[index]);
            else
                BinaryPrimitives.WriteSingleLittleEndian(element, values[index]);
        }

        return EncodeArrayHeaderAndPayload(rawPayload, values.Length, options);
    }

    private static byte[] EncodeArrayPayload(double[] values, FbxBinaryExportOptions options)
    {
        byte[] rawPayload = new byte[values.Length * sizeof(double)];
        for (int index = 0; index < values.Length; index++)
        {
            Span<byte> element = rawPayload.AsSpan(index * sizeof(double), sizeof(double));
            if (options.BigEndian)
                BinaryPrimitives.WriteDoubleBigEndian(element, values[index]);
            else
                BinaryPrimitives.WriteDoubleLittleEndian(element, values[index]);
        }

        return EncodeArrayHeaderAndPayload(rawPayload, values.Length, options);
    }

    private static byte[] EncodeArrayHeaderAndPayload(byte[] rawPayload, int arrayLength, FbxBinaryExportOptions options)
    {
        byte[] payload = options.ArrayEncodingMode == FbxBinaryArrayEncodingMode.ZlibCompressed
            ? CompressZlib(rawPayload)
            : rawPayload;
        uint encoding = options.ArrayEncodingMode == FbxBinaryArrayEncodingMode.ZlibCompressed ? 1u : 0u;

        byte[] header = new byte[sizeof(uint) * 3 + payload.Length];
        WriteUInt32(header.AsSpan(0, sizeof(uint)), checked((uint)arrayLength), options.BigEndian);
        WriteUInt32(header.AsSpan(sizeof(uint), sizeof(uint)), encoding, options.BigEndian);
        WriteUInt32(header.AsSpan(sizeof(uint) * 2, sizeof(uint)), checked((uint)payload.Length), options.BigEndian);
        payload.CopyTo(header, sizeof(uint) * 3);
        return header;
    }

    private static byte[] CompressZlib(byte[] rawPayload)
    {
        using MemoryStream output = new();
        using (ZLibStream stream = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
            stream.Write(rawPayload);
        return output.ToArray();
    }

    private static byte[] EncodeBlob(byte[] payload, bool bigEndian)
    {
        byte[] data = new byte[sizeof(uint) + payload.Length];
        WriteUInt32(data.AsSpan(0, sizeof(uint)), checked((uint)payload.Length), bigEndian);
        payload.CopyTo(data, sizeof(uint));
        return data;
    }

    private static byte[] EncodeInt16(short value, bool bigEndian)
    {
        byte[] buffer = new byte[sizeof(short)];
        if (bigEndian)
            BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        else
            BinaryPrimitives.WriteInt16LittleEndian(buffer, value);
        return buffer;
    }

    private static byte[] EncodeInt32(int value, bool bigEndian)
    {
        byte[] buffer = new byte[sizeof(int)];
        if (bigEndian)
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        else
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        return buffer;
    }

    private static byte[] EncodeInt64(long value, bool bigEndian)
    {
        byte[] buffer = new byte[sizeof(long)];
        if (bigEndian)
            BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        else
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        return buffer;
    }

    private static byte[] EncodeFloat32(float value, bool bigEndian)
    {
        byte[] buffer = new byte[sizeof(float)];
        if (bigEndian)
            BinaryPrimitives.WriteSingleBigEndian(buffer, value);
        else
            BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        return buffer;
    }

    private static byte[] EncodeFloat64(double value, bool bigEndian)
    {
        byte[] buffer = new byte[sizeof(double)];
        if (bigEndian)
            BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
        else
            BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
        return buffer;
    }

    private static void WriteHeader(Stream stream, FbxBinaryExportOptions options)
    {
        stream.Write(BinaryHeaderMagic);
        stream.WriteByte(options.BigEndian ? (byte)1 : (byte)0);

        Span<byte> versionBytes = stackalloc byte[sizeof(int)];
        if (options.BigEndian)
            BinaryPrimitives.WriteInt32BigEndian(versionBytes, options.BinaryVersion);
        else
            BinaryPrimitives.WriteInt32LittleEndian(versionBytes, options.BinaryVersion);
        stream.Write(versionBytes);
    }

    private static void WriteNode(Stream stream, EncodedNode node, ref long absoluteOffset, FbxBinaryExportOptions options)
    {
        ulong endOffset = checked((ulong)(absoluteOffset + node.SerializedSize));
        WriteNodeHeader(stream, options, endOffset, checked((ulong)node.Properties.Length), checked((ulong)node.PropertyListLength), (byte)node.NameBytes.Length);
        stream.Write(node.NameBytes);

        foreach (EncodedProperty property in node.Properties)
        {
            stream.WriteByte(property.TypeCode);
            stream.Write(property.Payload);
        }

        long childAbsoluteOffset = absoluteOffset + GetNodeHeaderSize(options.BinaryVersion) + node.NameBytes.Length + node.PropertyListLength;
        foreach (EncodedNode child in node.Children)
            WriteNode(stream, child, ref childAbsoluteOffset, options);

        WriteSentinel(stream, options.BinaryVersion);
        absoluteOffset = checked((long)endOffset);
    }

    private static void WriteNodeHeader(Stream stream, FbxBinaryExportOptions options, ulong endOffset, ulong propertyCount, ulong propertyListLength, byte nameLength)
    {
        if (options.BinaryVersion >= 7500)
        {
            Span<byte> header = stackalloc byte[25];
            WriteUInt64(header[..8], endOffset, options.BigEndian);
            WriteUInt64(header.Slice(8, 8), propertyCount, options.BigEndian);
            WriteUInt64(header.Slice(16, 8), propertyListLength, options.BigEndian);
            header[24] = nameLength;
            stream.Write(header);
            return;
        }

        Span<byte> compactHeader = stackalloc byte[13];
        WriteUInt32(compactHeader[..4], checked((uint)endOffset), options.BigEndian);
        WriteUInt32(compactHeader.Slice(4, 4), checked((uint)propertyCount), options.BigEndian);
        WriteUInt32(compactHeader.Slice(8, 4), checked((uint)propertyListLength), options.BigEndian);
        compactHeader[12] = nameLength;
        stream.Write(compactHeader);
    }

    private static void WriteSentinel(Stream stream, int binaryVersion)
    {
        Span<byte> sentinel = binaryVersion >= 7500 ? stackalloc byte[25] : stackalloc byte[13];
        sentinel.Clear();
        stream.Write(sentinel);
    }

    private static void WriteFooter(Stream stream, FbxBinaryExportOptions options)
    {
        stream.Write(FooterIdMagic);
        Span<byte> reserved = stackalloc byte[sizeof(int)];
        reserved.Clear();
        stream.Write(reserved);

        long alignmentPosition = stream.Position;
        int paddingLength = (int)(16 - (alignmentPosition % 16));
        if (paddingLength == 0)
            paddingLength = 16;

        WriteZeroBytes(stream, paddingLength);

        Span<byte> versionBytes = stackalloc byte[sizeof(int)];
        if (options.BigEndian)
            BinaryPrimitives.WriteInt32BigEndian(versionBytes, options.BinaryVersion);
        else
            BinaryPrimitives.WriteInt32LittleEndian(versionBytes, options.BinaryVersion);
        stream.Write(versionBytes);

        WriteZeroBytes(stream, 120);
        stream.Write(FooterTerminalMagic);
    }

    private static void WriteZeroBytes(Stream stream, int length)
    {
        Span<byte> zeros = stackalloc byte[32];
        zeros.Clear();
        while (length > 0)
        {
            int chunkLength = Math.Min(length, zeros.Length);
            stream.Write(zeros[..chunkLength]);
            length -= chunkLength;
        }
    }

    private static int GetNodeHeaderSize(int binaryVersion)
        => binaryVersion >= 7500 ? 25 : 13;

    private static byte MapBinaryTypeCode(FbxPropertyKind kind)
        => kind switch
        {
            FbxPropertyKind.Int8 => (byte)'Z',
            FbxPropertyKind.Int16 => (byte)'Y',
            FbxPropertyKind.Boolean => (byte)'B',
            FbxPropertyKind.Byte => (byte)'C',
            FbxPropertyKind.Int32 => (byte)'I',
            FbxPropertyKind.Float32 => (byte)'F',
            FbxPropertyKind.Float64 => (byte)'D',
            FbxPropertyKind.Int64 => (byte)'L',
            FbxPropertyKind.String => (byte)'S',
            FbxPropertyKind.Raw => (byte)'R',
            FbxPropertyKind.BooleanArray => (byte)'b',
            FbxPropertyKind.ByteArray => (byte)'c',
            FbxPropertyKind.Int32Array => (byte)'i',
            FbxPropertyKind.Int64Array => (byte)'l',
            FbxPropertyKind.Float32Array => (byte)'f',
            FbxPropertyKind.Float64Array => (byte)'d',
            _ => throw new NotSupportedException($"Unsupported FBX property kind '{kind}' for binary export."),
        };

    private static void WriteUInt32(Span<byte> destination, uint value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(destination, value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
    }

    private static void WriteUInt64(Span<byte> destination, ulong value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt64BigEndian(destination, value);
        else
            BinaryPrimitives.WriteUInt64LittleEndian(destination, value);
    }

    private readonly record struct EncodedProperty(byte TypeCode, byte[] Payload)
    {
        public int SerializedSize => 1 + Payload.Length;
    }

    private sealed record EncodedNode(
        byte[] NameBytes,
        EncodedProperty[] Properties,
        EncodedNode[] Children,
        long SerializedSize,
        int PropertyListLength);
}