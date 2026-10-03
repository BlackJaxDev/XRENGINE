using K4os.Compression.LZ4;
using ZstdSharp;

namespace XREngine.Data
{
    public static partial class Compression
    {
        /// <summary>
        /// Decompresses into caller-owned storage and returns the number of bytes written. Every codec
        /// writes directly to <paramref name="destination"/>; codecs supplied by a backend that lacks
        /// a span destination copy once from the backend's array through their interface default.
        /// </summary>
        public static int Decompress(ReadOnlySpan<byte> compressed, CompressionCodec codec, Span<byte> destination)
        {
            return codec switch
            {
                CompressionCodec.Stored => CopyStored(compressed, destination),
                CompressionCodec.Lzma => DecompressLzmaInto(compressed, destination),
                CompressionCodec.Lz4 => DecompressLz4Into(compressed, destination),
                CompressionCodec.Zstd => DecompressZstdInto(compressed, destination),
                CompressionCodec.GDeflate => DecompressGDeflateInto(compressed, destination),
                CompressionCodec.NvComp => DecompressNvCompInto(compressed, destination),
                _ => throw new NotSupportedException($"Unknown compression codec: {codec}"),
            };
        }

        private static int CopyStored(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            if (source.Length > destination.Length)
                throw new InvalidDataException($"Stored payload of {source.Length} bytes exceeds the destination of {destination.Length} bytes.");
            source.CopyTo(destination);
            return source.Length;
        }

        /// <summary>Decompresses an LZMA or chunked-LZMA blob produced by <see cref="Compress(byte[], bool, SevenZip.ICodeProgress?)"/> into <paramref name="destination"/>.</summary>
        public static unsafe int DecompressLzmaInto(ReadOnlySpan<byte> compressed, Span<byte> destination)
        {
            if (compressed.IsEmpty)
                return 0;

            if (compressed[0] == ChunkedMagic)
                return DecompressChunkedInto(compressed, destination);

            return DecompressLzmaStreamInto(compressed, destination, longLength: true);
        }

        private static unsafe int DecompressLzmaStreamInto(ReadOnlySpan<byte> compressed, Span<byte> destination, bool longLength)
        {
            int sizeByteCount = longLength ? 8 : 4;
            if (compressed.Length < 5 + sizeByteCount)
                throw new InvalidDataException("LZMA payload is shorter than its header.");

            long length = longLength
                ? BitConverter.ToInt64(compressed.Slice(5, 8))
                : BitConverter.ToInt32(compressed.Slice(5, 4));
            if (length < 0 || length > destination.Length)
                throw new InvalidDataException($"Decoded LZMA length {length} exceeds the destination of {destination.Length} bytes.");

            SevenZip.Compression.LZMA.Decoder decoder = new();
            byte[] properties = new byte[5];
            compressed[..5].CopyTo(properties);
            decoder.SetDecoderProperties(properties);

            fixed (byte* inPtr = compressed)
            fixed (byte* outPtr = destination)
            {
                using UnmanagedMemoryStream inStream = new(inPtr + 5 + sizeByteCount, compressed.Length - 5 - sizeByteCount);
                using UnmanagedMemoryStream outStream = new(outPtr, length, length, FileAccess.Write);
                decoder.Code(inStream, outStream, inStream.Length, length, null);
            }

            return checked((int)length);
        }

        private static int DecompressChunkedInto(ReadOnlySpan<byte> bytes, Span<byte> destination)
        {
            int pos = 0;
            if (bytes[pos++] != ChunkedMagic)
                throw new InvalidOperationException("Not a chunked LZMA blob.");

            long originalSize = BitConverter.ToInt64(bytes.Slice(pos, 8));
            pos += 8;
            if (originalSize < 0 || originalSize > destination.Length)
                throw new InvalidDataException($"Chunked LZMA payload declares {originalSize} bytes but the destination holds {destination.Length}.");

            int chunkCount = BitConverter.ToInt32(bytes.Slice(pos, 4));
            pos += 4;
            if (chunkCount < 0 || pos + chunkCount * 8L > bytes.Length)
                throw new InvalidDataException("Chunked LZMA header is truncated.");

            int sizesStart = pos;
            int originalsStart = pos + chunkCount * 4;
            pos = originalsStart + chunkCount * 4;

            int destOffset = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                int compressedSize = BitConverter.ToInt32(bytes.Slice(sizesStart + i * 4, 4));
                int originalChunkSize = BitConverter.ToInt32(bytes.Slice(originalsStart + i * 4, 4));
                if (compressedSize < 0 || pos + compressedSize > bytes.Length || originalChunkSize < 0 || destOffset + originalChunkSize > originalSize)
                    throw new InvalidDataException("Chunked LZMA chunk table is inconsistent.");

                int written = DecompressLzmaStreamInto(bytes.Slice(pos, compressedSize), destination.Slice(destOffset, originalChunkSize), longLength: false);
                if (written != originalChunkSize)
                    throw new InvalidDataException($"Chunked LZMA chunk {i} decoded {written} bytes but declares {originalChunkSize}.");

                pos += compressedSize;
                destOffset += originalChunkSize;
            }

            return destOffset;
        }

        /// <summary>Decompresses an LZ4 blob produced by <see cref="CompressLz4"/> into <paramref name="destination"/>.</summary>
        public static int DecompressLz4Into(ReadOnlySpan<byte> compressed, Span<byte> destination)
        {
            if (compressed.Length < sizeof(int))
                throw new InvalidOperationException("LZ4 blob too short.");

            int originalSize = BitConverter.ToInt32(compressed);
            if (originalSize == 0)
                return 0;
            if (originalSize < 0 || originalSize > destination.Length)
                throw new InvalidDataException($"LZ4 payload declares {originalSize} bytes but the destination holds {destination.Length}.");

            int decoded = LZ4Codec.Decode(compressed[sizeof(int)..], destination[..originalSize]);
            if (decoded != originalSize)
                throw new InvalidOperationException($"LZ4 decode size mismatch: expected {originalSize}, got {decoded}.");
            return decoded;
        }

        /// <summary>Decompresses a Zstandard frame produced by <see cref="CompressZstd"/> into <paramref name="destination"/>.</summary>
        public static int DecompressZstdInto(ReadOnlySpan<byte> compressed, Span<byte> destination)
        {
            using Decompressor decompressor = new();
            return decompressor.Unwrap(compressed, destination);
        }

        private static int DecompressGDeflateInto(ReadOnlySpan<byte> compressed, Span<byte> destination)
        {
            IGDeflateCodec backend = GDeflateBackend
                ?? throw new InvalidOperationException("GDeflate decompression is not available (DirectStorage codec not loaded).");
            if (destination.IsEmpty)
                return compressed.IsEmpty ? 0 : throw new InvalidDataException("GDeflate payload has no destination.");
            if (!backend.TryDecompress(compressed, destination, out int written))
                throw new InvalidOperationException("GDeflate decompression failed or is not available.");
            return written;
        }

        private static int DecompressNvCompInto(ReadOnlySpan<byte> compressed, Span<byte> destination)
        {
            IHardwareLz4Codec? backend = NvCompBackend;
            if (backend?.IsAvailable != true)
                throw new NotSupportedException("nvCOMP decompression requires an installed and available CUDA backend.");
            return backend.Decompress(compressed, destination);
        }
    }
}
