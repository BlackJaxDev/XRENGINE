using System.Runtime.InteropServices;

namespace XREngine.Data
{
    /// <summary>
    /// P/Invoke interop for NVIDIA nvCOMP GPU-accelerated compression/decompression.
    /// 
    /// nvCOMP supports LZ4, Snappy, Deflate, GDeflate, Zstd, and more on CUDA GPUs.
    /// On Blackwell+ (B200/B300/GB200/GB300), the hardware Decompression Engine (DE)
    /// handles LZ4/Snappy/Deflate with zero SM usage.  On older GPUs, SM-based
    /// fallback is used transparently.
    /// 
    /// <para><b>Requirements:</b></para>
    /// <list type="bullet">
    ///   <item><c>nvcomp.dll</c> / <c>libnvcomp.so</c> — download from
    ///         <see href="https://developer.nvidia.com/nvcomp-downloads"/>.</item>
    ///   <item>CUDA runtime (<c>cudart64_*.dll</c>).</item>
    /// </list>
    /// 
    /// <para><b>Wire format:</b> The compressed output is a CPU-LZ4-compatible blob
    /// (4-byte little-endian uncompressed size prefix followed by raw LZ4 block data),
    /// identical to <see cref="Compression.CompressLz4"/>. This means archives written
    /// with the GPU path can always be read back on the CPU path and vice-versa.</para>
    /// </summary>
    public static class NvCompInterop
    {
        // ────────────────────── Native library detection ──────────────────────

        private const string NvCompLib = "nvcomp";
        private const string CudaRtLib = "cudart64_12"; // CUDA 12.x runtime

        private static readonly Lazy<bool> _isAvailable = new(ProbeNativeLibrary, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// Returns <c>true</c> if both <c>nvcomp</c> and the CUDA runtime are loadable.
        /// </summary>
        public static bool IsAvailable => _isAvailable.Value;

        private static bool ProbeNativeLibrary()
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
                return false;

            try
            {
                if (!NativeLibrary.TryLoad(NvCompLib, out nint nvcompHandle))
                    return false;

                if (!NativeLibrary.TryLoad(CudaRtLib, out nint cudartHandle))
                {
                    NativeLibrary.Free(nvcompHandle);
                    return false;
                }

                // Verify CUDA is functional by attempting to query device count.
                int deviceCount = 0;
                unsafe
                {
                    int err = cudaGetDeviceCount(&deviceCount);
                    if (err != 0 || deviceCount <= 0)
                    {
                        NativeLibrary.Free(nvcompHandle);
                        NativeLibrary.Free(cudartHandle);
                        return false;
                    }
                }

                // Keep both libraries loaded for the process lifetime.
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ──────────────────── CUDA Runtime P/Invoke ──────────────────────────

        private const int cudaSuccess = 0;
        private const int cudaMemcpyHostToDevice = 1;
        private const int cudaMemcpyDeviceToHost = 2;

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int cudaGetDeviceCount(int* count);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int cudaMalloc(out nint devPtr, nuint size);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int cudaFree(nint devPtr);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int cudaMemcpy(nint dst, nint src, nuint count, int kind);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int cudaStreamCreate(out nint stream);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int cudaStreamSynchronize(nint stream);

        [DllImport(CudaRtLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int cudaStreamDestroy(nint stream);

        // ──────────────────── nvCOMP Batched LZ4 P/Invoke ────────────────────

        // nvcompBatchedLZ4Opts_t is a struct { nvcompType_t data_type; }
        // where data_type = NVCOMP_TYPE_CHAR (0) for raw byte data.
        [StructLayout(LayoutKind.Sequential)]
        private struct nvcompBatchedLZ4Opts_t
        {
            public int data_type; // NVCOMP_TYPE_CHAR = 0
        }

        // nvcompStatus_t = int enum, 0 = nvcompSuccess.
        private const int nvcompSuccess = 0;

        [DllImport(NvCompLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int nvcompBatchedLZ4CompressGetTempSize(
            nuint batchSize,
            nuint maxUncompressedChunkBytes,
            nvcompBatchedLZ4Opts_t formatOpts,
            nuint* tempBytes);

        [DllImport(NvCompLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int nvcompBatchedLZ4CompressGetMaxOutputChunkSize(
            nuint maxUncompressedChunkBytes,
            nvcompBatchedLZ4Opts_t formatOpts,
            nuint* maxCompressedChunkBytes);

        [DllImport(NvCompLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int nvcompBatchedLZ4CompressAsync(
            nint* deviceUncompressedPtrs,
            nuint* deviceUncompressedBytes,
            nuint maxUncompressedChunkBytes,
            nuint batchSize,
            nint deviceTempPtr,
            nuint tempBytes,
            nint* deviceCompressedPtrs,
            nuint* deviceCompressedBytes,
            nvcompBatchedLZ4Opts_t formatOpts,
            nint stream);

        [DllImport(NvCompLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int nvcompBatchedLZ4DecompressGetTempSize(
            nuint batchSize,
            nuint maxUncompressedChunkBytes,
            nuint* tempBytes);

        [DllImport(NvCompLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int nvcompBatchedLZ4DecompressAsync(
            nint* deviceCompressedPtrs,
            nuint* deviceCompressedBytes,
            nuint* deviceUncompressedBytes,
            nuint* deviceActualUncompressedBytes,
            nuint batchSize,
            nint deviceTempPtr,
            nuint tempBytes,
            nint* deviceUncompressedPtrs,
            int* deviceStatuses,
            nint stream);

        // ──────────────────── Helpers ────────────────────────────────────────

        /// <summary>Throws <see cref="InvalidOperationException"/> on non-zero CUDA error.</summary>
        private static void CudaCheck(int err, string context)
        {
            if (err != cudaSuccess)
                throw new InvalidOperationException($"CUDA error {err} during {context}.");
        }

        /// <summary>Throws <see cref="InvalidOperationException"/> on non-zero nvCOMP status.</summary>
        private static void NvcompCheck(int status, string context)
        {
            if (status != nvcompSuccess)
                throw new InvalidOperationException($"nvCOMP error {status} during {context}.");
        }

        // ──────────────────── Public API ─────────────────────────────────────

        /// <summary>
        /// GPU-compresses <paramref name="source"/> using nvCOMP batched LZ4.
        /// The output format is a 4-byte LE uncompressed-size prefix followed by raw LZ4
        /// block data — identical to <see cref="Compression.CompressLz4"/> — so the
        /// result is always decompressible on the CPU path.
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// Thrown if nvCOMP is not available.  Callers should check
        /// <see cref="IsAvailable"/> or use <see cref="Compression.CompressNvComp"/>
        /// which handles fallback automatically.
        /// </exception>
        public static unsafe byte[] Compress(ReadOnlySpan<byte> source)
        {
            if (!IsAvailable)
                throw new NotSupportedException("nvCOMP native library is not available.");

            nuint srcLen = (nuint)source.Length;
            if (srcLen == 0)
                return [0, 0, 0, 0]; // 4-byte header with size=0

            // ── 1. Query required sizes ──────────────────────────────────
            var opts = new nvcompBatchedLZ4Opts_t { data_type = 0 };

            nuint tempBytes;
            NvcompCheck(
                nvcompBatchedLZ4CompressGetTempSize(1, srcLen, opts, &tempBytes),
                "CompressGetTempSize");

            nuint maxOutputChunkBytes;
            NvcompCheck(
                nvcompBatchedLZ4CompressGetMaxOutputChunkSize(srcLen, opts, &maxOutputChunkBytes),
                "CompressGetMaxOutputChunkSize");

            // ── 2. Allocate device buffers ───────────────────────────────
            nint dSrc, dDst, dTemp;
            CudaCheck(cudaMalloc(out dSrc, srcLen), "cudaMalloc(src)");

            try
            {
                CudaCheck(cudaMalloc(out dDst, maxOutputChunkBytes), "cudaMalloc(dst)");
                try
                {
                    CudaCheck(cudaMalloc(out dTemp, tempBytes), "cudaMalloc(temp)");
                    try
                    {
                        // ── 3. Create stream + copy host→device ──────────────
                        nint stream;
                        CudaCheck(cudaStreamCreate(out stream), "cudaStreamCreate");
                        try
                        {
                            fixed (byte* pSrc = source)
                            {
                                CudaCheck(
                                    cudaMemcpy(dSrc, (nint)pSrc, srcLen, cudaMemcpyHostToDevice),
                                    "cudaMemcpy H→D");
                            }

                            // ── 4. Launch batched compress ───────────────────
                            // Batched API uses arrays of pointers/sizes (batch_size=1 here).
                            nint srcPtr = dSrc;
                            nint dstPtr = dDst;
                            nuint srcSize = srcLen;
                            nuint compressedSize = 0;

                            NvcompCheck(
                                nvcompBatchedLZ4CompressAsync(
                                    &srcPtr,
                                    &srcSize,
                                    srcLen,
                                    1, // batch_size
                                    dTemp,
                                    tempBytes,
                                    &dstPtr,
                                    &compressedSize,
                                    opts,
                                    stream),
                                "BatchedLZ4CompressAsync");

                            CudaCheck(cudaStreamSynchronize(stream), "cudaStreamSynchronize");

                            // ── 5. Copy compressed data device→host ──────────
                            // Read back the actual compressed size (written by the kernel).
                            byte[] result = new byte[4 + (int)compressedSize];

                            // Write 4-byte LE uncompressed size prefix.
                            int uncompLen = source.Length;
                            result[0] = (byte)(uncompLen);
                            result[1] = (byte)(uncompLen >> 8);
                            result[2] = (byte)(uncompLen >> 16);
                            result[3] = (byte)(uncompLen >> 24);

                            fixed (byte* pResult = &result[4])
                            {
                                CudaCheck(
                                    cudaMemcpy((nint)pResult, dDst, compressedSize, cudaMemcpyDeviceToHost),
                                    "cudaMemcpy D→H");
                            }

                            return result;
                        }
                        finally
                        {
                            cudaStreamDestroy(stream);
                        }
                    }
                    finally
                    {
                        cudaFree(dTemp);
                    }
                }
                finally
                {
                    cudaFree(dDst);
                }
            }
            finally
            {
                cudaFree(dSrc);
            }
        }

        /// <summary>
        /// GPU-decompresses <paramref name="compressed"/> using nvCOMP batched LZ4.
        /// Expects the same wire format as <see cref="Compression.CompressLz4"/>:
        /// 4-byte LE uncompressed size prefix followed by raw LZ4 block data.
        /// On Blackwell+, the hardware Decompression Engine handles this with zero
        /// SM usage if device memory was allocated via the HW-decompress memory pool.
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// Thrown if nvCOMP is not available.
        /// </exception>
        public static unsafe byte[] Decompress(ReadOnlySpan<byte> compressed)
        {
            if (!IsAvailable)
                throw new NotSupportedException("nvCOMP native library is not available.");

            if (compressed.Length < 4)
                throw new ArgumentException("Compressed data too short — missing size header.", nameof(compressed));

            // ── 1. Read uncompressed size from 4-byte LE prefix ──────────
            int uncompressedSize = BitConverter.ToInt32(compressed[..4]);
            if (uncompressedSize <= 0)
                return [];

            ReadOnlySpan<byte> payload = compressed[4..];
            nuint compLen = (nuint)payload.Length;
            nuint uncompLen = (nuint)uncompressedSize;

            // ── 2. Query temp buffer size ────────────────────────────────
            nuint tempBytes;
            NvcompCheck(
                nvcompBatchedLZ4DecompressGetTempSize(1, uncompLen, &tempBytes),
                "DecompressGetTempSize");

            // ── 3. Allocate device buffers ───────────────────────────────
            nint dComp, dDecomp, dTemp;
            CudaCheck(cudaMalloc(out dComp, compLen), "cudaMalloc(comp)");

            try
            {
                CudaCheck(cudaMalloc(out dDecomp, uncompLen), "cudaMalloc(decomp)");
                try
                {
                    CudaCheck(cudaMalloc(out dTemp, tempBytes), "cudaMalloc(temp)");
                    try
                    {
                        // ── 4. Create stream + copy compressed host→device ───
                        nint stream;
                        CudaCheck(cudaStreamCreate(out stream), "cudaStreamCreate");
                        try
                        {
                            fixed (byte* pComp = payload)
                            {
                                CudaCheck(
                                    cudaMemcpy(dComp, (nint)pComp, compLen, cudaMemcpyHostToDevice),
                                    "cudaMemcpy H→D");
                            }

                            // ── 5. Launch batched decompress ─────────────────
                            nint compPtr = dComp;
                            nint decompPtr = dDecomp;
                            nuint compSize = compLen;
                            nuint uncompSize = uncompLen;
                            nuint actualUncompSize = 0;
                            int status = 0;

                            NvcompCheck(
                                nvcompBatchedLZ4DecompressAsync(
                                    &compPtr,
                                    &compSize,
                                    &uncompSize,
                                    &actualUncompSize,
                                    1, // batch_size
                                    dTemp,
                                    tempBytes,
                                    &decompPtr,
                                    &status,
                                    stream),
                                "BatchedLZ4DecompressAsync");

                            CudaCheck(cudaStreamSynchronize(stream), "cudaStreamSynchronize");

                            // ── 6. Copy decompressed data device→host ────────
                            byte[] result = new byte[uncompressedSize];
                            fixed (byte* pResult = result)
                            {
                                CudaCheck(
                                    cudaMemcpy((nint)pResult, dDecomp, uncompLen, cudaMemcpyDeviceToHost),
                                    "cudaMemcpy D→H");
                            }

                            return result;
                        }
                        finally
                        {
                            cudaStreamDestroy(stream);
                        }
                    }
                    finally
                    {
                        cudaFree(dTemp);
                    }
                }
                finally
                {
                    cudaFree(dDecomp);
                }
            }
            finally
            {
                cudaFree(dComp);
            }
        }
    }
}
