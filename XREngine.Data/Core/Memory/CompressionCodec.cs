namespace XREngine.Data
{
    /// <summary>
    /// Identifies which compression algorithm was used to encode an asset's payload.
    /// Stored per-entry in the archive TOC so different assets can use different codecs.
    /// </summary>
    public enum CompressionCodec : byte
    {
        /// <summary>
        /// SevenZip LZMA — the original codec.  High ratio, slow compress, moderate decompress.
        /// Existing archives use this exclusively.
        /// </summary>
        Lzma = 0,

        /// <summary>
        /// LZ4 (K4os.Compression.LZ4) — very fast decompress, moderate ratio.
        /// Ideal for assets loaded at runtime where latency matters.
        /// Span-native: no intermediate <c>byte[]</c> copies needed.
        /// </summary>
        Lz4 = 1,

        /// <summary>
        /// Zstandard (ZstdSharp) — excellent ratio close to LZMA with much faster decompress.
        /// Good default for most assets.
        /// Span-native: no intermediate <c>byte[]</c> copies needed.
        /// </summary>
        Zstd = 2,

        /// <summary>
        /// GDeflate via DirectStorage — GPU-friendly block compression.
        /// Uses the DirectStorage compression codec when available, CPU fallback otherwise.
        /// </summary>
        GDeflate = 3,

        /// <summary>
        /// NVIDIA nvCOMP — GPU-accelerated compression/decompression via CUDA.
        /// Supports LZ4/Snappy/Deflate on SM; hardware Decompression Engine on Blackwell+.
        /// Requires <c>nvcomp.dll</c> + CUDA runtime.  Falls back to <see cref="Lz4"/> on CPU
        /// if the GPU path is unavailable.
        /// </summary>
        NvComp = 4,
    }
}
