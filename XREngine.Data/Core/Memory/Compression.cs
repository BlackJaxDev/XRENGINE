using System.Buffers;
using System.Numerics;
using System.Text;
using K4os.Compression.LZ4;
using Silk.NET.Core.Native;
using Silk.NET.DirectStorage;
using XREngine.Data.Transforms.Rotations;
using ZstdSharp;

namespace XREngine.Data
{
    public static class Compression
    {
        private sealed class GDeflateCodecContext(DStorage api, ComPtr<IDStorageCompressionCodec> codec) : IDisposable
        {
            public DStorage Api { get; } = api;
            public ComPtr<IDStorageCompressionCodec> Codec { get; } = codec;
            public object SyncRoot { get; } = new();

            public void Dispose()
            {
                Codec.Dispose();
                Api.Dispose();
            }
        }

        private static readonly Lazy<GDeflateCodecContext?> GDeflateCodec = new(CreateGDeflateCodec, LazyThreadSafetyMode.ExecutionAndPublication);

        private static unsafe GDeflateCodecContext? CreateGDeflateCodec()
        {
            if (!OperatingSystem.IsWindows())
                return null;

            try
            {
                DStorage api = DStorage.GetApi();
                ComPtr<IDStorageCompressionCodec> codec = api.CreateCompressionCodec<IDStorageCompressionCodec>((CompressionFormat)1, 0);
                if (codec.Handle is null)
                {
                    api.Dispose();
                    return null;
                }

                return new GDeflateCodecContext(api, codec);
            }
            catch
            {
                return null;
            }
        }

        public static unsafe bool TryCompressGDeflate(ReadOnlySpan<byte> source, out byte[] encoded)
        {
            encoded = [];
            if (source.IsEmpty)
                return true;

            GDeflateCodecContext? context = GDeflateCodec.Value;
            if (context is null)
                return false;

            lock (context.SyncRoot)
            {
                try
                {
                    nuint bound = context.Codec.CompressBufferBound((nuint)source.Length);
                    if (bound == 0 || bound > int.MaxValue)
                        return false;

                    encoded = new byte[(int)bound];
                    nuint compressedSize = 0;

                    int hr;
                    fixed (byte* sourcePtr = source)
                    fixed (byte* encodedPtr = encoded)
                    {
                        hr = context.Codec.CompressBuffer(
                            sourcePtr,
                            (nuint)source.Length,
                            (Silk.NET.DirectStorage.Compression)1,
                            encodedPtr,
                            (nuint)encoded.Length,
                            &compressedSize);
                    }

                    if (hr < 0 || compressedSize == 0 || compressedSize > (nuint)encoded.Length)
                    {
                        encoded = [];
                        return false;
                    }

                    if (compressedSize != (nuint)encoded.Length)
                        Array.Resize(ref encoded, (int)compressedSize);

                    return true;
                }
                catch
                {
                    encoded = [];
                    return false;
                }
            }
        }

        public static unsafe bool TryDecompressGDeflate(ReadOnlySpan<byte> encodedSource, int expectedDecodedLength, out byte[] decoded)
        {
            decoded = [];
            if (expectedDecodedLength < 0)
                return false;

            if (expectedDecodedLength == 0)
                return encodedSource.IsEmpty;

            if (encodedSource.IsEmpty)
                return false;

            GDeflateCodecContext? context = GDeflateCodec.Value;
            if (context is null)
                return false;

            lock (context.SyncRoot)
            {
                try
                {
                    decoded = new byte[expectedDecodedLength];
                    nuint actualDecodedSize = 0;

                    int hr;
                    fixed (byte* encodedPtr = encodedSource)
                    fixed (byte* decodedPtr = decoded)
                    {
                        hr = context.Codec.DecompressBuffer(
                            encodedPtr,
                            (nuint)encodedSource.Length,
                            decodedPtr,
                            (nuint)decoded.Length,
                            &actualDecodedSize);
                    }

                    if (hr < 0 || actualDecodedSize != (nuint)expectedDecodedLength)
                    {
                        decoded = [];
                        return false;
                    }

                    return true;
                }
                catch
                {
                    decoded = [];
                    return false;
                }
            }
        }

        public static byte[] DecompressFromString(uint? length, string byteStr)
        {
            if (string.IsNullOrEmpty(byteStr))
                return [];

            // YAML scalars may include newlines/whitespace (folded or literal blocks) for very long strings.
            // Our compressor emits a contiguous hex string, so strip whitespace before validating/decoding.
            // Also accept an optional "0x" prefix for debugging convenience.
            if (byteStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                byteStr = byteStr[2..];

            bool hasWhitespace = false;
            for (int i = 0; i < byteStr.Length; i++)
            {
                if (char.IsWhiteSpace(byteStr[i]))
                {
                    hasWhitespace = true;
                    break;
                }
            }

            if (hasWhitespace)
            {
                var sb = new StringBuilder(byteStr.Length);
                foreach (char c in byteStr)
                {
                    if (!char.IsWhiteSpace(c))
                        sb.Append(c);
                }
                byteStr = sb.ToString();
            }

            static bool TryDecodeAndDecompress(uint? expectedLength, string hex, out byte[] decompressed)
            {
                decompressed = [];
                byte[] compressed;
                try
                {
                    compressed = Convert.FromHexString(hex);
                }
                catch
                {
                    return false;
                }

                try
                {
                    decompressed = Decompress(compressed, false);
                    return expectedLength is null || decompressed.Length == expectedLength.Value;
                }
                catch
                {
                    decompressed = [];
                    return false;
                }
            }

            if ((byteStr.Length & 1) != 0)
            {
                // Best-effort recovery: if a single hex nibble was lost (common when huge YAML scalars
                // are edited/wrapped), try restoring by padding either the front or end.
                // If the caller provided an expected decompressed length, validate against it.
                if (TryDecodeAndDecompress(length, "0" + byteStr, out var recoveredA))
                    return recoveredA;
                if (TryDecodeAndDecompress(length, byteStr + "0", out var recoveredB))
                    return recoveredB;

                // If the string was truncated by exactly 1 character, another plausible repair is to
                // drop a nibble from either end to re-align to whole bytes.
                if (byteStr.Length > 1)
                {
                    if (TryDecodeAndDecompress(length, byteStr[..^1], out var recoveredC))
                        return recoveredC;
                    if (TryDecodeAndDecompress(length, byteStr[1..], out var recoveredD))
                        return recoveredD;
                }

                // If no expected length was provided, fall back to accepting the first successful decode+decompress.
                if (length is null)
                {
                    if (TryDecodeAndDecompress(null, "0" + byteStr, out recoveredA))
                        return recoveredA;
                    if (TryDecodeAndDecompress(null, byteStr + "0", out recoveredB))
                        return recoveredB;

                    if (byteStr.Length > 1)
                    {
                        if (TryDecodeAndDecompress(null, byteStr[..^1], out var recoveredC))
                            return recoveredC;
                        if (TryDecodeAndDecompress(null, byteStr[1..], out var recoveredD))
                            return recoveredD;
                    }
                }

                // Provide a more actionable error: this is almost certainly a corrupted/truncated payload.
                // Include a small preview to help spot obvious formatting issues.
                string head = byteStr.Length <= 32 ? byteStr : byteStr[..32];
                string tail = byteStr.Length <= 32 ? string.Empty : byteStr[^32..];
                throw new FormatException(
                    $"Compressed byte string must contain an even number of hex characters (after trimming). Got {byteStr.Length}. " +
                    $"Tried repairs (pad front/back, drop first/last nibble) but none produced a valid LZMA payload" +
                    (length is null ? "." : $" matching expected length {length.Value}.") +
                    $" Head='{head}' Tail='{tail}'.");
            }

            byte[] compressed;
            try
            {
                compressed = Convert.FromHexString(byteStr);
            }
            catch (FormatException ex)
            {
                throw new FormatException("Compressed byte string contains invalid hexadecimal characters.", ex);
            }
            return Decompress(compressed, false);
        }

        public static byte[] Compress(byte[] arr, bool longSize = false, SevenZip.ICodeProgress? progress = null)
        {
            SevenZip.Compression.LZMA.Encoder encoder = new();
            using MemoryStream inStream = new(arr);
            using MemoryStream outStream = new();
            encoder.WriteCoderProperties(outStream);
            if (longSize)
            {
                outStream.Write(BitConverter.GetBytes(arr.LongLength), 0, 8);
                encoder.Code(inStream, outStream, arr.LongLength, -1, progress);
            }
            else
            {
                outStream.Write(BitConverter.GetBytes(arr.Length), 0, 4);
                encoder.Code(inStream, outStream, arr.Length, -1, progress);
            }
            return outStream.ToArray();
        }
        /// <summary>
        /// Compresses a byte array using the LZMA algorithm, without allocating new objects.
        /// </summary>
        /// <param name="arr"></param>
        /// <param name="encoder"></param>
        /// <param name="inStreamObject"></param>
        /// <param name="outStreamObject"></param>
        /// <returns></returns>
        public static unsafe byte[] Compress(
            byte[] arr,
            ref SevenZip.Compression.LZMA.Encoder encoder,
            ref MemoryStream inStreamObject,
            ref MemoryStream outStreamObject)
        {
            encoder ??= new();
            inStreamObject ??= new();
            outStreamObject ??= new();

            if (inStreamObject.Length < arr.Length)
                inStreamObject.SetLength(arr.Length);
            inStreamObject.Seek(0, SeekOrigin.Begin);
            inStreamObject.Write(arr, 0, arr.Length);
            inStreamObject.Seek(0, SeekOrigin.Begin);

            if (outStreamObject.Length < arr.Length)
                outStreamObject.SetLength(arr.Length); // Set the length of the output stream to the size of the input stream, will be smaller after compression
            outStreamObject.Seek(0, SeekOrigin.Begin);

            encoder.WriteCoderProperties(outStreamObject);
            outStreamObject.Write(BitConverter.GetBytes(arr.Length), 0, 4);
            encoder.Code(inStreamObject, outStreamObject, arr.Length, -1, null);
            return outStreamObject.ToArray();
        }
        public static int Decompress(byte[] inBuf, int inOffset, int inLength, byte[] outBuf, int outOffset)
        {
            SevenZip.Compression.LZMA.Decoder decoder = new();
            using MemoryStream inStream = new(inBuf, inOffset, inLength);
            using MemoryStream outStream = new(outBuf, outOffset, outBuf.Length - outOffset);

            inStream.Seek(0, SeekOrigin.Begin);
            byte[] properties = new byte[5];
            inStream.Read(properties, 0, 5);
            byte[] lengthBytes = new byte[4];
            inStream.Read(lengthBytes, 0, 4);

            decoder.SetDecoderProperties(properties);
            int len = BitConverter.ToInt32(lengthBytes, 0);

            decoder.Code(inStream, outStream, inStream.Length - 9, len, null);

            return len;
        }
        public static int Decompress(
            byte[] inBuf,
            int inOffset,
            int inLength,
            byte[] outBuf,
            int outOffset,
            ref SevenZip.Compression.LZMA.Decoder decoder,
            ref MemoryStream inStreamObject,
            ref MemoryStream outStreamObject)
        {
            decoder ??= new();
            inStreamObject ??= new();
            outStreamObject ??= new();

            if (inStreamObject.Length < inLength)
                inStreamObject.SetLength(inLength);
            inStreamObject.Seek(0, SeekOrigin.Begin);
            inStreamObject.Write(inBuf, inOffset, inLength);
            inStreamObject.Seek(0, SeekOrigin.Begin);

            //if (outStreamObject.Length < outBuf.Length - outOffset)
            //outStreamObject.SetLength(outBuf.Length - outOffset);
            outStreamObject.SetLength(0);
            outStreamObject.Seek(0, SeekOrigin.Begin);

            byte[] properties = new byte[5];
            inStreamObject.Read(properties, 0, 5);
            byte[] lengthBytes = new byte[4];
            inStreamObject.Read(lengthBytes, 0, 4);

            decoder.SetDecoderProperties(properties);
            int len = BitConverter.ToInt32(lengthBytes, 0);

            decoder.Code(inStreamObject, outStreamObject, inStreamObject.Length - 9, len, null);

            outStreamObject.Seek(0, SeekOrigin.Begin);
            outStreamObject.Read(outBuf, outOffset, len);

            return len;
        }
        public static byte[] Decompress(byte[] bytes, bool longLength = false)
        {
            // Check for chunked-parallel format first.
            if (bytes.Length > 0 && bytes[0] == ChunkedMagic)
                return DecompressChunked(bytes);

            SevenZip.Compression.LZMA.Decoder decoder = new();
            using MemoryStream inStream = new(bytes);
            using MemoryStream outStream = new();

            inStream.Seek(0, SeekOrigin.Begin);
            byte[] properties = new byte[5];
            inStream.Read(properties, 0, 5);

            int sizeByteCount = longLength ? 8 : 4;
            byte[] lengthBytes = new byte[sizeByteCount];
            inStream.Read(lengthBytes, 0, sizeByteCount);

            decoder.SetDecoderProperties(properties);
            long len = longLength
                ? BitConverter.ToInt64(lengthBytes, 0)
                : BitConverter.ToInt32(lengthBytes, 0);

            decoder.Code(inStream, outStream, inStream.Length - 5 - sizeByteCount, len, null);

            return outStream.ToArray();
        }

        /// <summary>
        /// Decompresses LZMA data from a <see cref="ReadOnlySpan{T}"/> without first
        /// copying it into a managed <c>byte[]</c>.  This is the zero-copy-friendly
        /// overload used when reading directly from memory-mapped archive data.
        /// </summary>
        /// <remarks>
        /// The LZMA <see cref="SevenZip.Compression.LZMA.Decoder"/> is stream-based, so
        /// we wrap the span in an <see cref="UnmanagedMemoryStream"/> (no copy).  The
        /// decompressed output is written into a pooled buffer from
        /// <see cref="System.Buffers.ArrayPool{T}"/> and then right-sized.
        /// </remarks>
        public static unsafe byte[] Decompress(ReadOnlySpan<byte> span, bool longLength = false)
        {
            if (span.IsEmpty)
                return [];

            // Chunked-parallel format check.
            if (span[0] == ChunkedMagic)
            {
                // Chunked decompress currently requires byte[]; copy once.
                return DecompressChunked(span.ToArray());
            }

            fixed (byte* ptr = span)
            {
                using var inStream = new UnmanagedMemoryStream(ptr, span.Length);

                byte[] properties = new byte[5];
                inStream.Read(properties, 0, 5);

                int sizeByteCount = longLength ? 8 : 4;
                Span<byte> lengthBuf = stackalloc byte[8];
                inStream.Read(lengthBuf[..sizeByteCount]);

                long len = longLength
                    ? BitConverter.ToInt64(lengthBuf)
                    : BitConverter.ToInt32(lengthBuf);

                SevenZip.Compression.LZMA.Decoder decoder = new();
                decoder.SetDecoderProperties(properties);

                // Pool the output buffer to avoid a large GC allocation per asset load.
                byte[] pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(checked((int)len));
                try
                {
                    using var outStream = new MemoryStream(pooled, 0, (int)len, writable: true);
                    decoder.Code(inStream, outStream, inStream.Length - 5 - sizeByteCount, len, null);

                    // Right-size: the caller owns this array, so copy out of the pooled buffer.
                    byte[] result = new byte[len];
                    Buffer.BlockCopy(pooled, 0, result, 0, (int)len);
                    return result;
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(pooled);
                }
            }
        }

        // ═══════════════════════ Chunked parallel LZMA ═══════════════════════
        //
        // Large files are split into chunks and each chunk is LZMA-compressed in
        // parallel.  The packed blob format:
        //
        //   byte   ChunkedMagic (0xCB)          – distinguishes from raw LZMA
        //   long   originalTotalSize             – uncompressed size
        //   int    chunkCount
        //   int[]  chunkCompressedSizes          – one entry per chunk
        //   int[]  chunkOriginalSizes            – one entry per chunk
        //   byte[] ...compressed chunk data...   – each is a standalone LZMA stream
        //
        // Each LZMA stream uses longSize=false (4-byte length prefix) since
        // individual chunks are small enough to fit in an int.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Magic byte that identifies a chunked-parallel LZMA blob.</summary>
        internal const byte ChunkedMagic = 0xCB;

        /// <summary>Default chunk size for parallel LZMA compression (4 MB).</summary>
        public const int DefaultChunkSize = 4 * 1024 * 1024;

        /// <summary>
        /// Compresses <paramref name="data"/> by splitting it into
        /// <paramref name="chunkSize"/>-byte pieces, LZMA-compressing each piece in
        /// parallel, and concatenating the results with a small index header.
        /// </summary>
        /// <param name="data">Uncompressed source data.</param>
        /// <param name="chunkSize">
        /// Size of each chunk in bytes.  The last chunk may be smaller.
        /// Pass 0 or negative for <see cref="DefaultChunkSize"/>.
        /// </param>
        /// <param name="progress">
        /// Optional callback invoked after each chunk finishes compressing.
        /// The parameter is the cumulative number of source bytes compressed so far.
        /// Thread-safe; may be called from multiple threads concurrently.
        /// </param>
        /// <returns>A byte array in the chunked-parallel format.</returns>
        public static byte[] CompressChunked(byte[] data, int chunkSize = 0, Action<long>? progress = null)
        {
            if (chunkSize <= 0)
                chunkSize = DefaultChunkSize;

            int chunkCount = (data.Length + chunkSize - 1) / chunkSize;
            if (chunkCount <= 0)
                chunkCount = 1;

            byte[][] compressedChunks = new byte[chunkCount][];
            int[] chunkOriginalSizes = new int[chunkCount];
            long compressedSoFar = 0;

            Parallel.For(0, chunkCount, i =>
            {
                int offset = i * chunkSize;
                int length = Math.Min(chunkSize, data.Length - offset);
                chunkOriginalSizes[i] = length;

                // Extract chunk and compress independently.
                byte[] chunk = new byte[length];
                Buffer.BlockCopy(data, offset, chunk, 0, length);
                compressedChunks[i] = Compress(chunk, longSize: false);

                // Report cumulative progress.
                if (progress is not null)
                {
                    long cumulative = Interlocked.Add(ref compressedSoFar, length);
                    progress(cumulative);
                }
            });

            // --- Assemble the blob --------------------------------------------------
            // Header: magic(1) + originalSize(8) + chunkCount(4) + sizes(4*N*2)
            int headerSize = 1 + 8 + 4 + chunkCount * 4 * 2;
            int totalCompressed = 0;
            for (int i = 0; i < chunkCount; i++)
                totalCompressed += compressedChunks[i].Length;

            byte[] result = new byte[headerSize + totalCompressed];
            int pos = 0;

            result[pos++] = ChunkedMagic;
            BitConverter.TryWriteBytes(result.AsSpan(pos), (long)data.Length);
            pos += 8;
            BitConverter.TryWriteBytes(result.AsSpan(pos), chunkCount);
            pos += 4;

            for (int i = 0; i < chunkCount; i++)
            {
                BitConverter.TryWriteBytes(result.AsSpan(pos), compressedChunks[i].Length);
                pos += 4;
            }

            for (int i = 0; i < chunkCount; i++)
            {
                BitConverter.TryWriteBytes(result.AsSpan(pos), chunkOriginalSizes[i]);
                pos += 4;
            }

            for (int i = 0; i < chunkCount; i++)
            {
                Buffer.BlockCopy(compressedChunks[i], 0, result, pos, compressedChunks[i].Length);
                pos += compressedChunks[i].Length;
            }

            return result;
        }

        /// <summary>
        /// Decompresses a blob produced by <see cref="CompressChunked"/>.
        /// </summary>
        internal static byte[] DecompressChunked(byte[] bytes)
        {
            int pos = 0;

            if (bytes[pos++] != ChunkedMagic)
                throw new InvalidOperationException("Not a chunked LZMA blob.");

            long originalSize = BitConverter.ToInt64(bytes, pos);
            pos += 8;

            int chunkCount = BitConverter.ToInt32(bytes, pos);
            pos += 4;

            int[] compressedSizes = new int[chunkCount];
            for (int i = 0; i < chunkCount; i++)
            {
                compressedSizes[i] = BitConverter.ToInt32(bytes, pos);
                pos += 4;
            }

            int[] originalSizes = new int[chunkCount];
            for (int i = 0; i < chunkCount; i++)
            {
                originalSizes[i] = BitConverter.ToInt32(bytes, pos);
                pos += 4;
            }

            byte[] result = new byte[originalSize];
            int destOffset = 0;

            // Decompress chunks in order (could be parallelized later if desired).
            for (int i = 0; i < chunkCount; i++)
            {
                byte[] chunkCompressed = new byte[compressedSizes[i]];
                Buffer.BlockCopy(bytes, pos, chunkCompressed, 0, compressedSizes[i]);
                pos += compressedSizes[i];

                byte[] decompressed = Decompress(chunkCompressed, longLength: false);
                Buffer.BlockCopy(decompressed, 0, result, destOffset, decompressed.Length);
                destOffset += decompressed.Length;
            }

            return result;
        }


        // ═══════════════════════════ LZ4 (span-native) ══════════════════════════
        //
        // Uses K4os.Compression.LZ4 which provides ReadOnlySpan<byte> APIs.
        // Blob format: [int32 uncompressedSize][LZ4 compressed block]
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Compresses <paramref name="source"/> using LZ4 (high-speed block mode).
        /// Returns <c>[int32 originalSize][compressed block]</c>.
        /// </summary>
        public static byte[] CompressLz4(ReadOnlySpan<byte> source, LZ4Level level = LZ4Level.L00_FAST)
        {
            // LZ4Codec.Encode returns 0 for empty input — handle as special case.
            if (source.Length == 0)
            {
                byte[] header = new byte[sizeof(int)];
                BitConverter.TryWriteBytes(header, 0);
                return header;
            }

            int maxEncoded = LZ4Codec.MaximumOutputSize(source.Length);
            byte[] output = new byte[sizeof(int) + maxEncoded];
            BitConverter.TryWriteBytes(output.AsSpan(0, sizeof(int)), source.Length);
            int encoded = LZ4Codec.Encode(source, output.AsSpan(sizeof(int)), level);
            if (encoded <= 0)
                throw new InvalidOperationException("LZ4 compression failed.");
            Array.Resize(ref output, sizeof(int) + encoded);
            return output;
        }

        /// <summary>
        /// Decompresses an LZ4 blob produced by <see cref="CompressLz4"/>.
        /// Zero-copy: reads directly from the span, output is pooled then right-sized.
        /// </summary>
        public static byte[] DecompressLz4(ReadOnlySpan<byte> compressed)
        {
            if (compressed.Length < sizeof(int))
                throw new InvalidOperationException("LZ4 blob too short.");

            int originalSize = BitConverter.ToInt32(compressed);
            if (originalSize == 0)
                return [];

            ReadOnlySpan<byte> payload = compressed[sizeof(int)..];

            byte[] pooled = ArrayPool<byte>.Shared.Rent(originalSize);
            try
            {
                int decoded = LZ4Codec.Decode(payload, pooled.AsSpan(0, originalSize));
                if (decoded != originalSize)
                    throw new InvalidOperationException($"LZ4 decode size mismatch: expected {originalSize}, got {decoded}.");
                byte[] result = new byte[originalSize];
                Buffer.BlockCopy(pooled, 0, result, 0, originalSize);
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pooled);
            }
        }

        // ═══════════════════════════ Zstd (span-native) ═════════════════════════
        //
        // Uses ZstdSharp (pure managed .NET) which provides ReadOnlySpan<byte> APIs.
        // Blob format: native Zstd frame (self-describing, includes uncompressed size).
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>Default Zstd compression level.  3 is a good balance of speed and ratio.</summary>
        public const int DefaultZstdLevel = 3;

        /// <summary>
        /// Compresses <paramref name="source"/> using Zstandard.
        /// The output is a standard Zstd frame which is self-describing.
        /// </summary>
        public static byte[] CompressZstd(ReadOnlySpan<byte> source, int level = DefaultZstdLevel)
        {
            using var compressor = new Compressor(level);
            // Wrap includes content size in frame header for decompression.
            return compressor.Wrap(source).ToArray();
        }

        /// <summary>
        /// Decompresses a Zstd frame produced by <see cref="CompressZstd"/>.
        /// Zero-copy on input; output is pooled then right-sized.
        /// </summary>
        public static byte[] DecompressZstd(ReadOnlySpan<byte> compressed)
        {
            using var decompressor = new Decompressor();

            // Try to read uncompressed size from the Zstd frame header.
            ulong contentSize = Decompressor.GetDecompressedSize(compressed);
            if (contentSize > 0 && contentSize <= int.MaxValue)
            {
                int size = (int)contentSize;
                byte[] pooled = ArrayPool<byte>.Shared.Rent(size);
                try
                {
                    int written = decompressor.Unwrap(compressed, pooled.AsSpan(0, size));
                    byte[] result = new byte[written];
                    Buffer.BlockCopy(pooled, 0, result, 0, written);
                    return result;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pooled);
                }
            }

            // Fallback: unknown size — let ZstdSharp allocate internally.
            return decompressor.Unwrap(compressed).ToArray();
        }

        // ════════════════════════ GDeflate (archive codec) ══════════════════════
        //
        // Wraps the existing TryCompressGDeflate / TryDecompressGDeflate for use
        // via the unified codec dispatch.  These require the expected decompressed
        // length to be stored in the archive TOC (UncompressedSize field).
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Compresses <paramref name="source"/> using GDeflate via DirectStorage.
        /// Throws if the GDeflate codec is not available on this system.
        /// </summary>
        public static byte[] CompressGDeflateOrThrow(ReadOnlySpan<byte> source)
        {
            if (!TryCompressGDeflate(source, out byte[] encoded))
                throw new InvalidOperationException("GDeflate compression is not available (DirectStorage codec not loaded).");
            return encoded;
        }

        /// <summary>
        /// Decompresses a GDeflate blob.  Requires the known uncompressed size
        /// from the archive TOC.
        /// </summary>
        public static byte[] DecompressGDeflateOrThrow(ReadOnlySpan<byte> compressed, int uncompressedSize)
        {
            if (!TryDecompressGDeflate(compressed, uncompressedSize, out byte[] decoded))
                throw new InvalidOperationException("GDeflate decompression failed or is not available.");
            return decoded;
        }

        // ═══════════════════ nvCOMP stub (GPU-accelerated) ══════════════════════
        //
        // NVIDIA nvCOMP provides GPU-accelerated compression/decompression via CUDA.
        // On Blackwell+, the hardware Decompression Engine handles LZ4/Snappy/Deflate
        // with zero SM usage.  On older CUDA GPUs, SM-based fallback is used.
        //
        // Integration requires:
        //  1. nvcomp.dll (not on NuGet — separate NVIDIA download)
        //  2. CUDA runtime (cudart)
        //  3. P/Invoke bindings to nvcompBatchedLZ4DecompressAsync, etc.
        //  4. GPU buffer management (cudaMalloc / cudaFree / cudaMemcpy)
        //
        // The stub below provides the API surface.  When nvcomp.dll is present,
        // it will be used; otherwise it falls back to CPU LZ4.
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>Whether the nvCOMP native library is loaded and usable.</summary>
        public static bool IsNvCompAvailable => NvCompInterop.IsAvailable;

        /// <summary>
        /// Compresses using nvCOMP (GPU LZ4).  Falls back to CPU <see cref="CompressLz4"/>
        /// if the GPU path is unavailable.
        /// </summary>
        public static byte[] CompressNvComp(ReadOnlySpan<byte> source)
        {
            if (NvCompInterop.IsAvailable)
            {
                try
                {
                    return NvCompInterop.Compress(source);
                }
                catch
                {
                    // Fall back to CPU path if native interop is present but runtime
                    // invocation fails (ABI drift, driver/runtime mismatch, etc.).
                }
            }

            // Fallback: CPU LZ4
            return CompressLz4(source);
        }

        /// <summary>
        /// Decompresses using nvCOMP (GPU LZ4).  Falls back to CPU <see cref="DecompressLz4"/>
        /// if the GPU path is unavailable.
        /// </summary>
        public static byte[] DecompressNvComp(ReadOnlySpan<byte> compressed)
        {
            if (NvCompInterop.IsAvailable)
            {
                try
                {
                    return NvCompInterop.Decompress(compressed);
                }
                catch
                {
                    // Fall back to CPU path if native interop is present but runtime
                    // invocation fails (ABI drift, driver/runtime mismatch, etc.).
                }
            }

            // Fallback: CPU LZ4
            return DecompressLz4(compressed);
        }

        // ══════════════════════ Unified codec dispatch ══════════════════════════

        /// <summary>
        /// Compresses <paramref name="source"/> using the specified <paramref name="codec"/>.
        /// </summary>
        public static byte[] Compress(ReadOnlySpan<byte> source, CompressionCodec codec)
        {
            return codec switch
            {
                CompressionCodec.Lzma => Compress(source.ToArray(), longSize: true),
                CompressionCodec.Lz4 => CompressLz4(source),
                CompressionCodec.Zstd => CompressZstd(source),
                CompressionCodec.GDeflate => CompressGDeflateOrThrow(source),
                CompressionCodec.NvComp => CompressNvComp(source),
                _ => throw new NotSupportedException($"Unknown compression codec: {codec}"),
            };
        }

        /// <summary>
        /// Decompresses <paramref name="compressed"/> that was encoded with
        /// <paramref name="codec"/>.  For codecs that need the uncompressed size
        /// (GDeflate), pass it via <paramref name="uncompressedSize"/>.
        /// </summary>
        public static byte[] Decompress(ReadOnlySpan<byte> compressed, CompressionCodec codec, int uncompressedSize = 0)
        {
            return codec switch
            {
                CompressionCodec.Lzma => Decompress(compressed, longLength: true),
                CompressionCodec.Lz4 => DecompressLz4(compressed),
                CompressionCodec.Zstd => DecompressZstd(compressed),
                CompressionCodec.GDeflate => DecompressGDeflateOrThrow(compressed, uncompressedSize),
                CompressionCodec.NvComp => DecompressNvComp(compressed),
                _ => throw new NotSupportedException($"Unknown compression codec: {codec}"),
            };
        }


        public static unsafe string CompressToString(DataSource source)
        {
            if (source.Length == 0)
                return string.Empty;

            int length = checked((int)source.Length);
            ReadOnlySpan<byte> bytes = new((void*)source.Address, length);
            return CompressToString(bytes.ToArray());
        }

        public static unsafe string CompressToString(byte[] arr)
        {
            byte[] compressed = Compress(arr, false);
            return Convert.ToHexString(compressed);
        }

        /// <summary>
        /// Compresses a unit quaternion q = [w, x, y, z] into a compressed form.
        /// N is the number of bits per component for the quantized components.
        /// </summary>
        public static (int index, int signBit, int[] quantizedComponents) CompressQuaternion(Quaternion q, int bitsPerComponent = 8)
        {
            // Find the index of the largest component
            float[] absQ = Array.ConvertAll([q.X, q.Y, q.Z, q.W], MathF.Abs);
            int i = Array.IndexOf(absQ, MaxValue(absQ));

            // Get the sign of q_i
            int signBit = q[i] >= 0 ? 0 : 1;

            // Remove q_i to get the remaining components
            var qRest = new float[3];
            int restIndex = 0;
            for (int idx = 0; idx < 4; idx++)
                if (idx != i)
                    qRest[restIndex++] = q[idx];

            // Compute the scaling factor
            float scale = MathF.Sqrt(1 - q[i] * q[i]);
            float[] scaledComponents = scale > 0 ? Array.ConvertAll(qRest, qi => qi / scale) : ([0.0f, 0.0f, 0.0f]);

            // Quantize the scaled components
            int[] quantizedComponents = new int[3];
            int maxInt = (1 << bitsPerComponent) - 1; // 2^N - 1
            for (int j = 0; j < 3; j++)
            {
                // Map from [-1, 1] to [0, maxInt]
                int qc = (int)MathF.Round((scaledComponents[j] + 1) * (maxInt / 2.0f));
                qc = Math.Max(0, Math.Min(maxInt, qc)); // Clamp to [0, maxInt]
                quantizedComponents[j] = qc;
            }

            // Pack the data
            return (i, signBit, quantizedComponents);
        }

        public static Quaternion DecompressQuaternion((int index, int signBit, int[] quantizedComponents) compressedData, int bitsPerComponent = 8)
        {
            int i = compressedData.index;
            int signBit = compressedData.signBit;
            int[] quantizedComponents = compressedData.quantizedComponents;
            int maxInt = (1 << bitsPerComponent) - 1; // 2^N - 1

            // Dequantize the components
            float[] scaledComponents = new float[3];
            for (int j = 0; j < 3; j++)
            {
                float sc = (quantizedComponents[j] / (maxInt / 2.0f)) - 1;
                sc = Math.Max(-1.0f, Math.Min(1.0f, sc)); // Clamp to [-1, 1]
                scaledComponents[j] = sc;
            }

            // Compute q_i
            float sumOfSquares = scaledComponents.Sum(x => x * x);
            float q_i = MathF.Sqrt(Math.Max(0.0f, 1.0f - sumOfSquares));
            if (signBit == 1)
                q_i = -q_i;
            
            // Rescale the components
            float scale = MathF.Sqrt(1.0f - q_i * q_i);
            float[] qRest = scale > 0 ? Array.ConvertAll(scaledComponents, sc => sc * scale) : ([0.0f, 0.0f, 0.0f]);

            // Reconstruct the quaternion
            Quaternion q = new();
            int restIndex = 0;
            for (int idx = 0; idx < 4; idx++)
                q[idx] = idx == i ? q_i : qRest[restIndex++];

            return q;
        }

        /// <summary>
        /// Utility method to find the maximum value in a double array.
        /// </summary>
        private static float MaxValue(float[] array)
        {
            float max = array[0];
            foreach (var val in array)
                if (val > max)
                    max = val;
            return max;
        }

        /// <summary>
        /// Compresses a unit quaternion q = [w, x, y, z] into a byte array.
        /// N is the number of bits per component for the quantized components.
        /// </summary>
        public static byte[] CompressQuaternionToBytes(Quaternion q, int bitsPerComponent = 8)
        {
            // Compress the quaternion to get the index, sign bit, and quantized components
            var (index, signBit, quantizedComponents) = CompressQuaternion(q, bitsPerComponent);

            // Calculate the total number of bits
            int totalBits = 2 + 1 + 3 * bitsPerComponent; // index (2 bits) + sign bit (1 bit) + 3 components (N bits each)

            // Calculate the number of bytes needed
            int totalBytes = (totalBits + 7) / 8; // Round up to the nearest whole byte

            byte[] byteArray = new byte[totalBytes];

            // Pack the bits into a single integer or long
            ulong packedData = 0;

            // Start packing bits from the most significant bit
            int bitPosition = totalBits;

            // Pack the index (2 bits)
            bitPosition -= 2;
            packedData |= ((ulong)index & 0x3) << bitPosition;

            // Pack the sign bit (1 bit)
            bitPosition -= 1;
            packedData |= ((ulong)signBit & 0x1) << bitPosition;

            // Pack the quantized components (3 * N bits)
            for (int j = 0; j < 3; j++)
            {
                bitPosition -= bitsPerComponent;
                packedData |= ((ulong)quantizedComponents[j] & ((1UL << bitsPerComponent) - 1)) << bitPosition;
            }

            // Now, write the packedData into the byte array
            for (int i = 0; i < totalBytes; i++)
            {
                // Extract the byte at position (from most significant byte)
                int shiftAmount = (totalBytes - 1 - i) * 8;
                byteArray[i] = (byte)((packedData >> shiftAmount) & 0xFF);
            }

            return byteArray;
        }

        /// <summary>
        /// Decompresses the quaternion from a byte array back to the quaternion q = [w, x, y, z].
        /// </summary>
        public static Quaternion DecompressQuaternion(byte[] byteArray, int offset = 0, int bitsPerComponent = 8)
        {
            // Calculate the total number of bits
            int totalBits = 2 + 1 + 3 * bitsPerComponent; // index (2 bits) + sign bit (1 bit) + 3 components (N bits each)
            int totalBytes = (totalBits + 7) / 8;

            // Reconstruct the packed data from the byte array
            ulong packedData = 0;

            for (int i = 0; i < totalBytes; i++)
            {
                int shiftAmount = (totalBytes - 1 - i) * 8;
                packedData |= ((ulong)byteArray[offset + i]) << shiftAmount;
            }

            // Now, unpack the data
            int bitPosition = totalBits;

            // Unpack the index (2 bits)
            bitPosition -= 2;
            int index = (int)((packedData >> bitPosition) & 0x3);

            // Unpack the sign bit (1 bit)
            bitPosition -= 1;
            int signBit = (int)((packedData >> bitPosition) & 0x1);

            // Unpack the quantized components (3 * N bits)
            int[] quantizedComponents = new int[3];
            for (int j = 0; j < 3; j++)
            {
                bitPosition -= bitsPerComponent;
                quantizedComponents[j] = (int)((packedData >> bitPosition) & ((1ul << bitsPerComponent) - 1));
            }

            // Now, use the decompressed data to reconstruct the quaternion
            return DecompressQuaternion((index, signBit, quantizedComponents), bitsPerComponent);
        }

        public static byte[] Compress(Vector3 value, out int bits)
            => FloatQuantizer.QuantizeToByteArray([value.X, value.Y, value.Z], out bits);

        public static byte[] Compress(Rotator value, out int bits)
            => FloatQuantizer.QuantizeToByteArray([value.Pitch, value.Yaw, value.Roll], out bits);

        public static byte[] Compress(float value, out int bits)
            => FloatQuantizer.QuantizeToByteArray([value], out bits);
        
        public static byte[] Compress(int value, out int bits)
        {
            bool sign = value < 0;
            value = Math.Abs(value);
            bits = 1;
            //Calculate the number of bits required to store the integer
            while (value > 0)
            {
                value >>= 1;
                bits++;
            }
            byte[] bytes = new byte[(bits + 7) / 8];
            for (int j = 0; j < bytes.Length; j++)
            {
                bytes[j] = (byte)(value & 0xFF);
                value >>= 8;
            }
            bytes[^1] |= (byte)(sign ? 0x80 : 0);
            return bytes;
        }
    }
}