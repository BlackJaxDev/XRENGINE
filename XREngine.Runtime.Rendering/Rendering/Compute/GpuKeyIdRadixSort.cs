using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Sorts (key, id) pairs of 32-bit unsigned integers on the GPU in ascending key order.
/// The sort is stable: pairs that have equal keys keep their input order.
/// </summary>
/// <remarks>
/// <para>
/// The sort runs <see cref="PassCount"/> least-significant-digit passes of <see cref="BitsPerPass"/> bits.
/// Each pass dispatches the histogram, prefix, and scatter shaders that the GPU BVH Morton sort also uses.
/// The passes alternate between the pairs buffer and the scratch buffer, so the sorted pairs end in the
/// pairs buffer.
/// </para>
/// <para>
/// The prefix pass runs in one workgroup of <see cref="LocalSize"/> invocations. Each invocation adds the
/// counts of one digit across all blocks, so its time increases linearly with the pair count.
/// </para>
/// <para>Call the methods on the render thread.</para>
/// </remarks>
public sealed class GpuKeyIdRadixSort : IDisposable
{
    /// <summary>Block histogram shader, relative to the engine shader folder.</summary>
    public const string HistogramShaderPath = "Scene3D/RenderPipeline/OctreeGeneration/radix_morton_histogram.comp";
    /// <summary>Digit and block prefix shader, relative to the engine shader folder.</summary>
    public const string PrefixShaderPath = "Scene3D/RenderPipeline/OctreeGeneration/radix_morton_prefix.comp";
    /// <summary>Stable scatter shader, relative to the engine shader folder.</summary>
    public const string ScatterShaderPath = "Scene3D/RenderPipeline/OctreeGeneration/radix_morton_scatter.comp";

    /// <summary>Storage binding of the pairs that a pass reads.</summary>
    public const uint InputBinding = 1u;
    /// <summary>Storage binding of the pairs that a pass writes.</summary>
    public const uint OutputBinding = 12u;
    /// <summary>Storage binding of the per-block digit offsets.</summary>
    public const uint OffsetsBinding = 13u;

    /// <summary>Invocations in one workgroup. Each block of this many pairs has its own digit counts.</summary>
    public const uint LocalSize = 256u;
    /// <summary>Bits of the key that one pass sorts.</summary>
    public const uint BitsPerPass = 8u;
    /// <summary>Number of different digits in one pass.</summary>
    public const uint DigitCount = 1u << (int)BitsPerPass;
    /// <summary>Number of passes. Together they sort all 32 bits of the key.</summary>
    public const int PassCount = 4;
    /// <summary>Unsigned integers in one pair: the key, then the id.</summary>
    public const uint UIntsPerPair = 2u;

    internal const string CountUniform = "numObjects";
    internal const string ShiftUniform = "radixShift";
    internal const string BlockCountUniform = "blockCount";

    private const string OffsetsBufferName = "GpuKeyIdRadixSortOffsets";

    private XRRenderProgram? _histogramProgram;
    private XRRenderProgram? _prefixProgram;
    private XRRenderProgram? _scatterProgram;
    private XRDataBuffer? _offsets;
    private bool _disposed;

    /// <summary>Number of blocks (workgroups) for <paramref name="count"/> pairs. The minimum is 1.</summary>
    public static uint GetBlockCount(uint count)
        => Math.Max(1u, (count + LocalSize - 1u) / LocalSize);

    /// <summary>Number of unsigned integers in the offsets buffer for <paramref name="count"/> pairs.</summary>
    public static uint GetOffsetCount(uint count)
        => GetBlockCount(count) * DigitCount;

    /// <summary>Right shift of the key digit that pass <paramref name="pass"/> sorts.</summary>
    public static uint GetPassShift(int pass)
        => (uint)pass * BitsPerPass;

    /// <summary>
    /// True when pass <paramref name="pass"/> reads the pairs buffer and writes the scratch buffer.
    /// False when it reads the scratch buffer and writes the pairs buffer.
    /// </summary>
    public static bool PassReadsPairs(int pass)
        => (pass & 1) == 0;

    /// <summary>
    /// Creates the sort programs on the first call and polls their link state.
    /// Returns true when all three programs are linked.
    /// </summary>
    public bool EnsureProgramsReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _histogramProgram ??= CreateProgram(HistogramShaderPath);
        _prefixProgram ??= CreateProgram(PrefixShaderPath);
        _scatterProgram ??= CreateProgram(ScatterShaderPath);
        return IsReady(_histogramProgram) && IsReady(_prefixProgram) && IsReady(_scatterProgram);
    }

    /// <summary>
    /// Sorts the first <paramref name="count"/> pairs of <paramref name="pairs"/> in ascending key order.
    /// The sorted pairs are in <paramref name="pairs"/> when the method returns; the contents of
    /// <paramref name="scratch"/> are not defined.
    /// </summary>
    /// <param name="pairs">Storage buffer of (key, id) unsigned integer pairs.</param>
    /// <param name="scratch">Storage buffer with space for at least <paramref name="count"/> pairs.</param>
    /// <param name="count">Number of pairs to sort.</param>
    /// <exception cref="ArgumentException">A buffer is too small for <paramref name="count"/> pairs.</exception>
    /// <exception cref="InvalidOperationException">The programs are not linked. Call <see cref="EnsureProgramsReady"/> first.</exception>
    public void Sort(XRDataBuffer pairs, XRDataBuffer scratch, uint count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(scratch);
        ulong requiredBytes = (ulong)count * UIntsPerPair * sizeof(uint);
        if (pairs.Length < requiredBytes)
            throw new ArgumentException($"The pairs buffer has {pairs.Length} bytes; {count} pairs need {requiredBytes} bytes.", nameof(pairs));
        if (scratch.Length < requiredBytes)
            throw new ArgumentException($"The scratch buffer has {scratch.Length} bytes; {count} pairs need {requiredBytes} bytes.", nameof(scratch));
        if (count <= 1u)
            return;
        if (!EnsureProgramsReady())
            throw new InvalidOperationException("The radix sort programs are not linked. Call EnsureProgramsReady before Sort.");

        XRRenderProgram histogram = _histogramProgram!;
        XRRenderProgram prefix = _prefixProgram!;
        XRRenderProgram scatter = _scatterProgram!;
        XRDataBuffer offsets = EnsureOffsets(GetOffsetCount(count));
        uint blockCount = GetBlockCount(count);

        for (int pass = 0; pass < PassCount; pass++)
        {
            XRDataBuffer source = PassReadsPairs(pass) ? pairs : scratch;
            XRDataBuffer destination = PassReadsPairs(pass) ? scratch : pairs;
            uint shift = GetPassShift(pass);

            histogram.BindBuffer(source, InputBinding);
            histogram.BindBuffer(offsets, OffsetsBinding);
            histogram.Uniform(CountUniform, count);
            histogram.Uniform(ShiftUniform, shift);
            histogram.DispatchCompute(blockCount, 1u, 1u, EMemoryBarrierMask.ShaderStorage);

            prefix.BindBuffer(offsets, OffsetsBinding);
            prefix.Uniform(BlockCountUniform, blockCount);
            prefix.DispatchCompute(1u, 1u, 1u, EMemoryBarrierMask.ShaderStorage);

            scatter.BindBuffer(source, InputBinding);
            scatter.BindBuffer(destination, OutputBinding);
            scatter.BindBuffer(offsets, OffsetsBinding);
            scatter.Uniform(CountUniform, count);
            scatter.Uniform(ShiftUniform, shift);
            scatter.DispatchCompute(blockCount, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        }
    }

    /// <summary>Releases the programs and the offsets buffer.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _histogramProgram?.Destroy();
        _prefixProgram?.Destroy();
        _scatterProgram?.Destroy();
        _offsets?.Dispose();
        _histogramProgram = null;
        _prefixProgram = null;
        _scatterProgram = null;
        _offsets = null;
    }

    /// <summary>Creates the offsets buffer, or makes it larger. The buffer never becomes smaller.</summary>
    private XRDataBuffer EnsureOffsets(uint offsetCount)
    {
        if (_offsets is null)
        {
            _offsets = new XRDataBuffer(OffsetsBufferName, EBufferTarget.ShaderStorageBuffer, offsetCount, EComponentType.UInt, 1u, false, true, allocateClientSideSource: false)
            {
                // The histogram pass writes every offset that the prefix and scatter passes read.
                GpuProduced = true,
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                PadEndingToVec4 = true,
            };
        }
        else if (_offsets.ElementCount < offsetCount)
        {
            _offsets.Resize(offsetCount, copyData: false);
        }

        return _offsets;
    }

    private static XRRenderProgram CreateProgram(string path)
        => new(true, false, ShaderHelper.LoadEngineShader(path, EShaderType.Compute))
        {
            Name = $"GpuKeyIdRadixSort.{System.IO.Path.GetFileNameWithoutExtension(path)}",
        };

    private static bool IsReady(XRRenderProgram program)
    {
        if (program.IsLinked)
            return true;
        program.Link();
        return program.IsLinked;
    }
}
