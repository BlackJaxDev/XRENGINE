using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace XREngine.Fbx;

public static class FbxStructuralParser
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

    public static FbxStructuralDocument ParseFile(string path, FbxReaderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FileInfo fileInfo = new(path);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"FBX file '{path}' does not exist.", path);

        FbxSourceBuffer source = fileInfo.Length >= 256L * 1024L * 1024L
            ? new MemoryMappedFbxSourceBuffer(path)
            : new ManagedFbxSourceBuffer(File.ReadAllBytes(path));
        return Parse(source, options);
    }

    public static FbxStructuralDocument Parse(byte[] source, FbxReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Parse(new ManagedFbxSourceBuffer(source), options);
    }

    private static FbxStructuralDocument Parse(FbxSourceBuffer source, FbxReaderOptions? options)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            options ??= FbxReaderOptions.Strict;
            FbxTransportEncoding encoding = DetectEncoding(source.Span);
            return encoding switch
            {
                FbxTransportEncoding.Binary => ParseBinary(source, options),
                FbxTransportEncoding.Ascii => ParseAscii(source, options),
                _ => throw new FbxParseException("Unrecognized FBX transport encoding", 0),
            };
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public static int DecodeArrayPayload(FbxStructuralDocument document, FbxArrayWorkItem workItem, Span<byte> destination)
    {
        if (destination.Length < workItem.ExpectedDecodedLength)
            throw new ArgumentException("Destination span is too small for the decoded FBX array payload.", nameof(destination));

        ReadOnlySpan<byte> payload = document.Source.Slice(workItem.PayloadOffset, workItem.PayloadLength);
        if (workItem.Encoding == 0)
        {
            if (payload.Length != workItem.ExpectedDecodedLength)
                throw new FbxParseException("Raw FBX array payload length does not match the declared element count", workItem.PayloadOffset);

            payload.CopyTo(destination);
            return workItem.ExpectedDecodedLength;
        }

        if (workItem.Encoding != 1)
            throw new FbxParseException("Unsupported FBX array encoding", workItem.PayloadOffset);

        using MemoryStream input = new(document.Source.Slice(workItem.PayloadOffset, workItem.PayloadLength).ToArray(), writable: false);
        using ZLibStream stream = new(input, CompressionMode.Decompress, leaveOpen: false);

        int totalRead = 0;
        while (totalRead < workItem.ExpectedDecodedLength)
        {
            int read = stream.Read(destination[totalRead..workItem.ExpectedDecodedLength]);
            if (read == 0)
                break;

            totalRead += read;
        }

        if (totalRead != workItem.ExpectedDecodedLength)
            throw new FbxParseException("Decoded FBX array payload length does not match the declared element count", workItem.PayloadOffset);

        if (stream.ReadByte() >= 0)
            throw new FbxParseException("Decoded FBX array payload contains trailing data", workItem.PayloadOffset);

        return totalRead;
    }

    public static byte[] DecodeArrayPayload(FbxStructuralDocument document, FbxArrayWorkItem workItem)
    {
        byte[] buffer = GC.AllocateUninitializedArray<byte>(workItem.ExpectedDecodedLength);
        DecodeArrayPayload(document, workItem, buffer);

        if (workItem.ElementSize == 1 && document.Properties[workItem.PropertyIndex].Kind == FbxPropertyKind.BooleanArray)
        {
            for (int index = 0; index < buffer.Length; index++)
                buffer[index] = buffer[index] == 0 ? (byte)0 : (byte)1;
        }

        return buffer;
    }

    private static FbxTransportEncoding DetectEncoding(ReadOnlySpan<byte> source)
    {
        if (source.Length >= BinaryHeaderMagic.Length + 5 && source[..BinaryHeaderMagic.Length].SequenceEqual(BinaryHeaderMagic))
            return FbxTransportEncoding.Binary;

        int position = 0;
        SkipUtf8Bom(source, ref position);
        SkipAsciiWhitespace(source, ref position);
        if (position >= source.Length)
            return FbxTransportEncoding.Unknown;

        if (source[position] == (byte)';')
            return FbxTransportEncoding.Ascii;

        if (!IsAsciiIdentifierStart(source[position]))
            return FbxTransportEncoding.Unknown;

        int scan = position + 1;
        while (scan < source.Length && IsAsciiIdentifierPart(source[scan]))
            scan++;

        return scan < source.Length && source[scan] == (byte)':'
            ? FbxTransportEncoding.Ascii
            : FbxTransportEncoding.Unknown;
    }

    private static FbxStructuralDocument ParseBinary(FbxSourceBuffer source, FbxReaderOptions options)
    {
        BinaryParser parser = new(source, options);
        return parser.Parse();
    }

    private static FbxStructuralDocument ParseAscii(FbxSourceBuffer source, FbxReaderOptions options)
    {
        AsciiParser parser = new(source, options);
        return parser.Parse();
    }

    private static void SkipUtf8Bom(ReadOnlySpan<byte> source, ref int position)
    {
        if (source.Length >= 3 && source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF)
            position = 3;
    }

    private static void SkipAsciiWhitespace(ReadOnlySpan<byte> source, ref int position)
    {
        while (position < source.Length)
        {
            byte value = source[position];
            if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                position++;
                continue;
            }

            break;
        }
    }

    private static bool IsAsciiIdentifierStart(byte value)
        => (value >= (byte)'A' && value <= (byte)'Z')
            || (value >= (byte)'a' && value <= (byte)'z')
            || value == (byte)'_';

    private static bool IsAsciiIdentifierPart(byte value)
        => IsAsciiIdentifierStart(value)
            || (value >= (byte)'0' && value <= (byte)'9')
            || value is (byte)'_' or (byte)'-';

    private enum AsciiTokenKind
    {
        Identifier,
        Name,
        Number,
        String,
        OpenBrace,
        CloseBrace,
        Comma,
        Star,
        EndOfFile,
    }

    private readonly record struct AsciiToken(AsciiTokenKind Kind, int Offset, int Length);

    private sealed class DocumentBuilder
    {
        private readonly List<MutableNode> _nodes = [];
        private readonly List<FbxPropertyRecord> _properties = [];
        private readonly List<FbxArrayWorkItem> _arrayWorkItems = [];

        public DocumentBuilder(FbxSourceBuffer source)
            => Source = source;

        public FbxSourceBuffer Source { get; }
        public FbxHeaderInfo Header { get; set; }
        public FbxFooterInfo? Footer { get; set; }
        public int MaxDepth { get; private set; }
        public IReadOnlyList<string> SkippedNodeNames { get; init; } = Array.Empty<string>();

        public int AddNode(int parentIndex, int depth, int nameOffset, int nameLength, long endOffset, FbxNodeFlags flags)
        {
            MaxDepth = Math.Max(MaxDepth, depth);
            int nodeIndex = _nodes.Count;
            _nodes.Add(new MutableNode
            {
                ParentIndex = parentIndex,
                Depth = depth,
                NameOffset = nameOffset,
                NameLength = nameLength,
                FirstPropertyIndex = _properties.Count,
                EndOffset = endOffset,
                Flags = flags,
            });
            return nodeIndex;
        }

        public int PropertyCount => _properties.Count;
        public int NodeCount => _nodes.Count;

        public void CompleteNode(int nodeIndex)
        {
            MutableNode node = _nodes[nodeIndex];
            node.PropertyCount = _properties.Count - node.FirstPropertyIndex;
            _nodes[nodeIndex] = node;
        }

        public void AddProperty(FbxPropertyRecord property)
            => _properties.Add(property);

        public void AddArrayWorkItem(FbxArrayWorkItem workItem)
            => _arrayWorkItems.Add(workItem);

        public FbxStructuralDocument Build()
        {
            FbxNodeRecord[] nodes = new FbxNodeRecord[_nodes.Count];
            for (int index = 0; index < _nodes.Count; index++)
            {
                MutableNode node = _nodes[index];
                nodes[index] = new FbxNodeRecord(
                    Index: index,
                    ParentIndex: node.ParentIndex,
                    Depth: node.Depth,
                    NameOffset: node.NameOffset,
                    NameLength: node.NameLength,
                    FirstPropertyIndex: node.FirstPropertyIndex,
                    PropertyCount: node.PropertyCount,
                    EndOffset: node.EndOffset,
                    Flags: node.Flags);
            }

            return new FbxStructuralDocument(
                Source,
                Header,
                nodes,
                _properties.ToArray(),
                _arrayWorkItems.ToArray(),
                Footer,
                MaxDepth);
        }

        public bool ShouldSkipNode(ReadOnlySpan<byte> nameBytes)
        {
            foreach (string candidate in SkippedNodeNames)
            {
                if (nameBytes.Length != candidate.Length)
                    continue;

                bool matched = true;
                for (int index = 0; index < candidate.Length; index++)
                {
                    if (nameBytes[index] != candidate[index])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                    return true;
            }

            return false;
        }

        private struct MutableNode
        {
            public int ParentIndex;
            public int Depth;
            public int NameOffset;
            public int NameLength;
            public int FirstPropertyIndex;
            public int PropertyCount;
            public long EndOffset;
            public FbxNodeFlags Flags;
        }
    }

    private ref struct BinarySpanReader
    {
        private readonly ReadOnlySpan<byte> _source;
        private readonly bool _bigEndian;
        private int _position;

        public BinarySpanReader(ReadOnlySpan<byte> source, bool bigEndian, int position)
        {
            _source = source;
            _bigEndian = bigEndian;
            _position = position;
        }

        public int Position
        {
            readonly get => _position;
            set => _position = value;
        }

        public readonly int Length => _source.Length;

        public readonly ReadOnlySpan<byte> Source => _source;

        public readonly byte PeekByte()
            => _position < _source.Length
                ? _source[_position]
                : throw new FbxParseException("Unexpected end of FBX data", _position);

        public byte ReadByte()
        {
            EnsureAvailable(1);
            return _source[_position++];
        }

        public uint ReadUInt32()
        {
            EnsureAvailable(sizeof(uint));
            uint value = _bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(_source.Slice(_position, sizeof(uint)))
                : BinaryPrimitives.ReadUInt32LittleEndian(_source.Slice(_position, sizeof(uint)));
            _position += sizeof(uint);
            return value;
        }

        public ulong ReadUInt64()
        {
            EnsureAvailable(sizeof(ulong));
            ulong value = _bigEndian
                ? BinaryPrimitives.ReadUInt64BigEndian(_source.Slice(_position, sizeof(ulong)))
                : BinaryPrimitives.ReadUInt64LittleEndian(_source.Slice(_position, sizeof(ulong)));
            _position += sizeof(ulong);
            return value;
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            EnsureAvailable(count);
            ReadOnlySpan<byte> slice = _source.Slice(_position, count);
            _position += count;
            return slice;
        }

        public void EnsureAvailable(int count)
        {
            if (_position > _source.Length - count)
                throw new FbxParseException("Unexpected end of FBX data", _position);
        }
    }

    private sealed class BinaryParser
    {
        private readonly FbxSourceBuffer _source;
        private readonly FbxReaderOptions _options;

        public BinaryParser(FbxSourceBuffer source, FbxReaderOptions options)
        {
            _source = source;
            _options = options;
        }

        public FbxStructuralDocument Parse()
        {
            if (_source.Length < 27)
                throw new FbxParseException("Binary FBX header is truncated", 0);

            if (!_source.Span[..BinaryHeaderMagic.Length].SequenceEqual(BinaryHeaderMagic))
                throw new FbxParseException("Binary FBX magic header is invalid", 0);

            byte endianness = _source.Span[22];
            if (endianness is not 0 and not 1)
                throw new FbxParseException("Binary FBX endianness byte is invalid", 22);

            bool bigEndian = endianness == 1;
            int version = bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(_source.Span.Slice(23, sizeof(int)))
                : BinaryPrimitives.ReadInt32LittleEndian(_source.Span.Slice(23, sizeof(int)));

            DocumentBuilder builder = new(_source)
            {
                Header = new FbxHeaderInfo(
                    Encoding: FbxTransportEncoding.Binary,
                    VersionText: version.ToString(),
                    BinaryVersion: version,
                    IsBigEndian: bigEndian,
                    HeaderLength: 27),
                SkippedNodeNames = _options.SkippedNodeNames,
            };

            BinarySpanReader reader = new(_source.Span, bigEndian, 27);
            ParseNodeList(ref reader, builder, parentIndex: -1, depth: 0, expectedEndOffset: null);
            builder.Footer = TryParseFooter(ref reader, version, bigEndian);

            if (_options.Strictness == FbxReaderStrictness.Strict && reader.Position != _source.Length)
                throw new FbxParseException("Binary FBX parser did not consume the full file", reader.Position);

            return builder.Build();
        }

        private void ParseNodeList(ref BinarySpanReader reader, DocumentBuilder builder, int parentIndex, int depth, long? expectedEndOffset)
        {
            while (true)
            {
                int headerOffset = reader.Position;
                BinaryNodeHeader header = ReadNodeHeader(ref reader, builder.Header.BinaryVersion ?? 0);
                if (header.IsSentinel)
                {
                    if (expectedEndOffset.HasValue && reader.Position != expectedEndOffset.Value)
                        throw new FbxParseException("Binary FBX child list did not end at the declared endOffset", reader.Position);

                    return;
                }

                if (header.EndOffset <= (ulong)headerOffset || header.EndOffset > (ulong)reader.Length)
                    throw new FbxParseException("Binary FBX node endOffset is invalid", headerOffset);

                int nameOffset = reader.Position;
                reader.ReadBytes(header.NameLength);
                ReadOnlySpan<byte> nameBytes = reader.Source.Slice(nameOffset, header.NameLength);
                bool skipped = builder.ShouldSkipNode(nameBytes);

                int nodeIndex = builder.AddNode(
                    parentIndex,
                    depth,
                    nameOffset,
                    header.NameLength,
                    checked((long)header.EndOffset),
                    skipped ? FbxNodeFlags.SkippedSubtree : FbxNodeFlags.None);

                if (skipped)
                {
                    reader.Position = checked((int)header.EndOffset);
                    builder.CompleteNode(nodeIndex);
                    continue;
                }

                int propertyBytesStart = reader.Position;
                ParseProperties(ref reader, builder, nodeIndex, header.PropertyCount);
                int propertyBytesConsumed = reader.Position - propertyBytesStart;
                if ((ulong)propertyBytesConsumed != header.PropertyListLength)
                    throw new FbxParseException("Binary FBX property list length does not match the declared propertyListLen", propertyBytesStart);

                ParseNodeList(ref reader, builder, nodeIndex, depth + 1, checked((long)header.EndOffset));
                builder.CompleteNode(nodeIndex);
            }
        }

        private void ParseProperties(ref BinarySpanReader reader, DocumentBuilder builder, int nodeIndex, ulong propertyCount)
        {
            if (propertyCount > int.MaxValue)
                throw new FbxParseException("Binary FBX property count exceeds the supported managed range", reader.Position);

            for (int index = 0; index < (int)propertyCount; index++)
            {
                int typeOffset = reader.Position;
                byte typeCode = reader.ReadByte();
                FbxPropertyKind kind = MapBinaryPropertyKind(typeCode, typeOffset);
                int dataOffset = reader.Position;

                if (kind is FbxPropertyKind.Int8 or FbxPropertyKind.Boolean or FbxPropertyKind.Byte)
                {
                    reader.ReadBytes(1);
                    builder.AddProperty(new FbxPropertyRecord(nodeIndex, kind, dataOffset, 1, 0, 0, 1));
                    continue;
                }

                if (kind == FbxPropertyKind.Int16)
                {
                    reader.ReadBytes(sizeof(short));
                    builder.AddProperty(new FbxPropertyRecord(nodeIndex, kind, dataOffset, sizeof(short), 0, 0, sizeof(short)));
                    continue;
                }

                if (kind is FbxPropertyKind.Int32 or FbxPropertyKind.Float32)
                {
                    reader.ReadBytes(sizeof(int));
                    builder.AddProperty(new FbxPropertyRecord(nodeIndex, kind, dataOffset, sizeof(int), 0, 0, sizeof(int)));
                    continue;
                }

                if (kind is FbxPropertyKind.Int64 or FbxPropertyKind.Float64)
                {
                    reader.ReadBytes(sizeof(long));
                    builder.AddProperty(new FbxPropertyRecord(nodeIndex, kind, dataOffset, sizeof(long), 0, 0, sizeof(long)));
                    continue;
                }

                if (kind is FbxPropertyKind.String or FbxPropertyKind.Raw)
                {
                    uint length = reader.ReadUInt32();
                    int payloadStartOffset = reader.Position;
                    reader.ReadBytes(checked((int)length));
                    builder.AddProperty(new FbxPropertyRecord(nodeIndex, kind, payloadStartOffset, checked((int)length), 0, 0, 1));
                    continue;
                }

                uint arrayLength = reader.ReadUInt32();
                uint encoding = reader.ReadUInt32();
                uint compressedLength = reader.ReadUInt32();
                int payloadOffset = reader.Position;
                int payloadLength = checked((int)compressedLength);
                int elementSize = kind switch
                {
                    FbxPropertyKind.BooleanArray => 1,
                    FbxPropertyKind.ByteArray => 1,
                    FbxPropertyKind.Int32Array => sizeof(int),
                    FbxPropertyKind.Int64Array => sizeof(long),
                    FbxPropertyKind.Float32Array => sizeof(float),
                    FbxPropertyKind.Float64Array => sizeof(double),
                    _ => throw new FbxParseException("Internal FBX array property mapping failure", typeOffset),
                };

                if (encoding is not 0 and not 1)
                    throw new FbxParseException("Binary FBX array encoding must be 0 or 1", typeOffset);

                int expectedLength = checked((int)arrayLength * elementSize);
                if (encoding == 0 && payloadLength != expectedLength)
                    throw new FbxParseException("Binary FBX raw array length does not match the declared element count", payloadOffset);

                reader.ReadBytes(payloadLength);

                int propertyIndex = builder.PropertyCount;
                builder.AddProperty(new FbxPropertyRecord(nodeIndex, kind, payloadOffset, payloadLength, arrayLength, encoding, elementSize));
                builder.AddArrayWorkItem(new FbxArrayWorkItem(propertyIndex, payloadOffset, payloadLength, arrayLength, encoding, elementSize));
            }
        }

        private FbxFooterInfo? TryParseFooter(ref BinarySpanReader reader, int headerVersion, bool bigEndian)
        {
            int remaining = reader.Length - reader.Position;
            if (remaining == 0)
                return null;

            if (remaining < 161)
            {
                if (_options.Strictness == FbxReaderStrictness.Strict)
                    throw new FbxParseException("Binary FBX footer is truncated", reader.Position);

                return null;
            }

            ReadOnlySpan<byte> footer = reader.Source[reader.Position..];
            if (!footer[^FooterTerminalMagic.Length..].SequenceEqual(FooterTerminalMagic))
            {
                if (_options.Strictness == FbxReaderStrictness.Strict)
                    throw new FbxParseException("Binary FBX footer terminal magic is missing or invalid", reader.Position);

                return null;
            }

            if (!footer[..FooterIdMagic.Length].SequenceEqual(FooterIdMagic))
            {
                if (_options.Strictness == FbxReaderStrictness.Strict)
                    throw new FbxParseException("Binary FBX footer ID block is invalid", reader.Position);

                return null;
            }

            ReadOnlySpan<byte> reserved = footer.Slice(FooterIdMagic.Length, sizeof(int));
            for (int index = 0; index < reserved.Length; index++)
            {
                if (reserved[index] != 0)
                {
                    if (_options.Strictness == FbxReaderStrictness.Strict)
                        throw new FbxParseException("Binary FBX footer reserved zero bytes are invalid", reader.Position + FooterIdMagic.Length + index);

                    return null;
                }
            }

            int paddingLength = remaining - 160;
            if (paddingLength is < 1 or > 16)
            {
                if (_options.Strictness == FbxReaderStrictness.Strict)
                    throw new FbxParseException("Binary FBX footer alignment padding is invalid", reader.Position);

                return null;
            }

            ReadOnlySpan<byte> padding = footer.Slice(20, paddingLength);
            for (int index = 0; index < padding.Length; index++)
            {
                if (padding[index] != 0)
                {
                    if (_options.Strictness == FbxReaderStrictness.Strict)
                        throw new FbxParseException("Binary FBX footer alignment padding must be zero-filled", reader.Position + 20 + index);

                    return null;
                }
            }

            int versionOffset = reader.Position + 20 + paddingLength;
            int footerVersion = bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(reader.Source.Slice(versionOffset, sizeof(int)))
                : BinaryPrimitives.ReadInt32LittleEndian(reader.Source.Slice(versionOffset, sizeof(int)));
            bool versionMatchesHeader = footerVersion == headerVersion;
            if (_options.Strictness == FbxReaderStrictness.Strict && !versionMatchesHeader)
                throw new FbxParseException("Binary FBX footer version does not match the header version", versionOffset);

            for (int index = versionOffset + sizeof(int); index < reader.Length - FooterTerminalMagic.Length; index++)
            {
                if (reader.Source[index] != 0)
                {
                    if (_options.Strictness == FbxReaderStrictness.Strict)
                        throw new FbxParseException("Binary FBX footer padding must be zero-filled", index);

                    return null;
                }
            }

            reader.Position = reader.Length;
            return new FbxFooterInfo(reader.Length - remaining, remaining, paddingLength, footerVersion, versionMatchesHeader);
        }

        private static BinaryNodeHeader ReadNodeHeader(ref BinarySpanReader reader, int version)
        {
            if (version >= 7500)
            {
                ulong endOffset = reader.ReadUInt64();
                ulong propertyCount = reader.ReadUInt64();
                ulong propertyListLength = reader.ReadUInt64();
                byte nameLength = reader.ReadByte();
                return new BinaryNodeHeader(endOffset, propertyCount, propertyListLength, nameLength);
            }

            uint endOffset32 = reader.ReadUInt32();
            uint propertyCount32 = reader.ReadUInt32();
            uint propertyListLength32 = reader.ReadUInt32();
            byte nameLength32 = reader.ReadByte();
            return new BinaryNodeHeader(endOffset32, propertyCount32, propertyListLength32, nameLength32);
        }

        private static FbxPropertyKind MapBinaryPropertyKind(byte typeCode, int offset)
            => typeCode switch
            {
                (byte)'Z' => FbxPropertyKind.Int8,
                (byte)'Y' => FbxPropertyKind.Int16,
                (byte)'B' => FbxPropertyKind.Boolean,
                (byte)'C' => FbxPropertyKind.Byte,
                (byte)'I' => FbxPropertyKind.Int32,
                (byte)'F' => FbxPropertyKind.Float32,
                (byte)'D' => FbxPropertyKind.Float64,
                (byte)'L' => FbxPropertyKind.Int64,
                (byte)'S' => FbxPropertyKind.String,
                (byte)'R' => FbxPropertyKind.Raw,
                (byte)'b' => FbxPropertyKind.BooleanArray,
                (byte)'c' => FbxPropertyKind.ByteArray,
                (byte)'i' => FbxPropertyKind.Int32Array,
                (byte)'l' => FbxPropertyKind.Int64Array,
                (byte)'f' => FbxPropertyKind.Float32Array,
                (byte)'d' => FbxPropertyKind.Float64Array,
                _ => throw new FbxParseException($"Unsupported FBX property code '{(char)typeCode}'", offset),
            };

        private readonly record struct BinaryNodeHeader(ulong EndOffset, ulong PropertyCount, ulong PropertyListLength, byte NameLength)
        {
            public bool IsSentinel => EndOffset == 0 && PropertyCount == 0 && PropertyListLength == 0 && NameLength == 0;
        }
    }

    private sealed class AsciiParser
    {
        private readonly FbxSourceBuffer _source;
        private readonly FbxReaderOptions _options;
        private int _position;

        public AsciiParser(FbxSourceBuffer source, FbxReaderOptions options)
        {
            _source = source;
            _options = options;
        }

        public FbxStructuralDocument Parse()
        {
            DocumentBuilder builder = new(_source)
            {
                Header = new FbxHeaderInfo(
                    Encoding: FbxTransportEncoding.Ascii,
                    VersionText: TryReadAsciiVersion(),
                    BinaryVersion: null,
                    IsBigEndian: false,
                    HeaderLength: 0),
                SkippedNodeNames = _options.SkippedNodeNames,
            };

            SkipTrivia();
            ParseNodeList(builder, parentIndex: -1, depth: 0, stopAtCloseBrace: false);
            SkipTrivia();
            if (builder.NodeCount == 0)
                throw new FbxParseException("ASCII FBX file does not contain any nodes after the header/comments", _position);
            if (_position != _source.Length)
                throw new FbxParseException("ASCII FBX parser encountered trailing non-whitespace content", _position);

            return builder.Build();
        }

        private void ParseNodeList(DocumentBuilder builder, int parentIndex, int depth, bool stopAtCloseBrace)
        {
            while (true)
            {
                SkipTrivia();
                if (_position >= _source.Length)
                {
                    if (stopAtCloseBrace)
                        throw new FbxParseException("ASCII FBX block is missing a closing brace", _position);

                    return;
                }

                if (_source.Span[_position] == (byte)'}')
                {
                    if (!stopAtCloseBrace)
                        throw new FbxParseException("ASCII FBX encountered an unexpected closing brace", _position);

                    _position++;
                    return;
                }

                ParseNode(builder, parentIndex, depth);
            }
        }

        private void ParseNode(DocumentBuilder builder, int parentIndex, int depth)
        {
            AsciiToken nameToken = ReadNameToken();
            bool skipped = builder.ShouldSkipNode(_source.Slice(nameToken.Offset, nameToken.Length));
            int nodeIndex = builder.AddNode(
                parentIndex,
                depth,
                nameToken.Offset,
                nameToken.Length,
                endOffset: -1,
                skipped ? FbxNodeFlags.SkippedSubtree : FbxNodeFlags.None);

            if (skipped)
            {
                SkipSkippedAsciiNode();
                builder.CompleteNode(nodeIndex);
                return;
            }

            bool propertySeen = false;
            while (true)
            {
                SkipTrivia();
                if (_position >= _source.Length)
                    break;

                byte next = _source.Span[_position];
                if (next == (byte)'{')
                {
                    _position++;
                    ParseNodeList(builder, nodeIndex, depth + 1, stopAtCloseBrace: true);
                    break;
                }

                if (next == (byte)'}')
                    break;

                if (next == (byte)',')
                {
                    _position++;
                    continue;
                }

                if (propertySeen && NextTokenStartsNode())
                    break;

                AddAsciiProperty(builder, nodeIndex);
                propertySeen = true;
            }

            builder.CompleteNode(nodeIndex);
        }

        private void AddAsciiProperty(DocumentBuilder builder, int nodeIndex)
        {
            SkipTrivia();
            int propertyOffset = _position;

            if (_position >= _source.Length)
                throw new FbxParseException("ASCII FBX property was truncated", _position);

            if (_source.Span[_position] == (byte)'*')
            {
                _position++;
                SkipTrivia();
                AsciiToken countToken = ReadToken();
                if (countToken.Kind != AsciiTokenKind.Number || !uint.TryParse(Encoding.ASCII.GetString(_source.Slice(countToken.Offset, countToken.Length)), out uint arrayLength))
                    throw new FbxParseException("ASCII FBX array count is invalid", countToken.Offset);

                SkipTrivia();
                ExpectByte((byte)'{', "ASCII FBX array block is missing '{'");
                SkipTrivia();
                ReadNameToken();

                while (true)
                {
                    SkipTrivia();
                    if (_position >= _source.Length)
                        throw new FbxParseException("ASCII FBX array block is missing a closing brace", _position);

                    if (_source.Span[_position] == (byte)'}')
                    {
                        _position++;
                        break;
                    }

                    if (_source.Span[_position] == (byte)',')
                    {
                        _position++;
                        continue;
                    }

                    AsciiToken valueToken = ReadToken();
                    if (valueToken.Kind is not (AsciiTokenKind.Number or AsciiTokenKind.String or AsciiTokenKind.Identifier))
                        throw new FbxParseException("ASCII FBX array block contains an invalid value token", valueToken.Offset);
                }

                builder.AddProperty(new FbxPropertyRecord(
                    nodeIndex,
                    FbxPropertyKind.AsciiArray,
                    propertyOffset,
                    _position - propertyOffset,
                    arrayLength,
                    0,
                    0));
                return;
            }

            AsciiToken token = ReadToken();
            if (token.Kind is not (AsciiTokenKind.Number or AsciiTokenKind.String or AsciiTokenKind.Identifier))
                throw new FbxParseException("ASCII FBX property token is invalid", token.Offset);

            builder.AddProperty(new FbxPropertyRecord(
                nodeIndex,
                FbxPropertyKind.AsciiScalar,
                token.Offset,
                token.Length,
                0,
                0,
                0));
        }

        private void SkipSkippedAsciiNode()
        {
            int braceDepth = 0;
            while (_position < _source.Length)
            {
                byte value = _source.Span[_position];
                if (value == (byte)'"')
                {
                    ReadStringToken();
                    continue;
                }

                if (value == (byte)'{')
                {
                    braceDepth++;
                    _position++;
                    continue;
                }

                if (value == (byte)'}')
                {
                    _position++;
                    if (braceDepth == 0)
                        return;

                    braceDepth--;
                    if (braceDepth == 0)
                        return;

                    continue;
                }

                if (value == (byte)'\n' && braceDepth == 0)
                {
                    _position++;
                    return;
                }

                _position++;
            }
        }

        private bool NextTokenStartsNode()
        {
            int probe = _position;
            SkipTrivia(ref probe);
            if (probe >= _source.Length || !IsAsciiIdentifierStart(_source.Span[probe]))
                return false;

            probe++;
            while (probe < _source.Length && IsAsciiIdentifierPart(_source.Span[probe]))
                probe++;

            return probe < _source.Length && _source.Span[probe] == (byte)':';
        }

        private string? TryReadAsciiVersion()
        {
            int probe = 0;
            SkipUtf8Bom(_source.Span, ref probe);
            SkipAsciiWhitespace(_source.Span, ref probe);
            if (probe >= _source.Length || _source.Span[probe] != (byte)';')
                return null;

            int lineStart = probe;
            while (probe < _source.Length && _source.Span[probe] is not (byte)'\r' and not (byte)'\n')
                probe++;

            string line = Encoding.ASCII.GetString(_source.Span.Slice(lineStart, probe - lineStart));
            const string prefix = "; FBX ";
            const string suffix = " project file";
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith(suffix, StringComparison.Ordinal))
                return null;

            return line[prefix.Length..^suffix.Length].Trim();
        }

        private void SkipTrivia()
            => SkipTrivia(ref _position);

        private void SkipTrivia(ref int position)
        {
            while (position < _source.Length)
            {
                byte value = _source.Span[position];
                if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    position++;
                    continue;
                }

                if (value == (byte)';')
                {
                    position++;
                    while (position < _source.Length && _source.Span[position] is not (byte)'\r' and not (byte)'\n')
                        position++;
                    continue;
                }

                break;
            }
        }

        private AsciiToken ReadNameToken()
        {
            SkipTrivia();
                if (_position >= _source.Length || !IsAsciiIdentifierStart(_source.Span[_position]))
                throw new FbxParseException("ASCII FBX expected a Name: token", _position);

            int start = _position;
            _position++;
            while (_position < _source.Length && IsAsciiIdentifierPart(_source.Span[_position]))
                _position++;

            int length = _position - start;
            if (_position >= _source.Length || _source.Span[_position] != (byte)':')
                throw new FbxParseException("ASCII FBX expected a Name: token", start);

            _position++;
            return new AsciiToken(AsciiTokenKind.Name, start, length);
        }

        private AsciiToken ReadToken()
        {
            SkipTrivia();
            if (_position >= _source.Length)
                return new AsciiToken(AsciiTokenKind.EndOfFile, _position, 0);

            byte value = _source.Span[_position];
            if (value == (byte)'{')
            {
                _position++;
                return new AsciiToken(AsciiTokenKind.OpenBrace, _position - 1, 1);
            }

            if (value == (byte)'}')
            {
                _position++;
                return new AsciiToken(AsciiTokenKind.CloseBrace, _position - 1, 1);
            }

            if (value == (byte)',')
            {
                _position++;
                return new AsciiToken(AsciiTokenKind.Comma, _position - 1, 1);
            }

            if (value == (byte)'*')
            {
                _position++;
                return new AsciiToken(AsciiTokenKind.Star, _position - 1, 1);
            }

            if (value == (byte)'"')
                return ReadStringToken();

            if (value is (byte)'+' or (byte)'-' or >= (byte)'0' and <= (byte)'9')
                return ReadNumberToken();

            if (!IsAsciiIdentifierStart(value))
                throw new FbxParseException("ASCII FBX encountered an invalid token", _position);

            int start = _position;
            _position++;
            while (_position < _source.Length && IsAsciiIdentifierPart(_source.Span[_position]))
                _position++;

            return new AsciiToken(AsciiTokenKind.Identifier, start, _position - start);
        }

        private AsciiToken ReadStringToken()
        {
            int start = _position;
            _position++;
            while (_position < _source.Length)
            {
                byte value = _source.Span[_position++];
                if (value == (byte)'"')
                    return new AsciiToken(AsciiTokenKind.String, start + 1, _position - start - 2);

                if (value == (byte)'\\' && _position < _source.Length)
                    _position++;
            }

            throw new FbxParseException("ASCII FBX string literal is unterminated", start);
        }

        private AsciiToken ReadNumberToken()
        {
            int start = _position;
            if (_source.Span[_position] is (byte)'+' or (byte)'-')
                _position++;

            bool digitSeen = false;
            while (_position < _source.Length)
            {
                byte value = _source.Span[_position];
                if (value >= (byte)'0' && value <= (byte)'9')
                {
                    digitSeen = true;
                    _position++;
                    continue;
                }

                if (value is (byte)'.' or (byte)'e' or (byte)'E' or (byte)'+' or (byte)'-')
                {
                    _position++;
                    continue;
                }

                break;
            }

            if (!digitSeen)
                throw new FbxParseException("ASCII FBX numeric literal is invalid", start);

            return new AsciiToken(AsciiTokenKind.Number, start, _position - start);
        }

        private void ExpectByte(byte expected, string message)
        {
            if (_position >= _source.Length || _source.Span[_position] != expected)
                throw new FbxParseException(message, _position);

            _position++;
        }
    }
}