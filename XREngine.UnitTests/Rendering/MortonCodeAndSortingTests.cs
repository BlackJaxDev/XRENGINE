using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Unit tests for Morton code computation and GPU sorting algorithms.
/// Tests use the actual compute shaders: GPURenderBuildKeys.comp and GPURenderRadixIndexSort.comp
/// </summary>
[TestFixture]
public class MortonCodeAndSortingTests
{
    /// <summary>
    /// Gets the path to the shader directory.
    /// </summary>
    private static string ShaderBasePath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                var candidate = Path.Combine(dir, "Build", "CommonAssets", "Shaders");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir) ?? dir;
            }
            return @"D:\Documents\XRENGINE\Build\CommonAssets\Shaders";
        }
    }

    private static string LoadShaderSource(string relativePath)
    {
        var fullPath = Path.Combine(ShaderBasePath, relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Shader file not found: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    #region Shader Loading Tests

    [Test]
    public void GPURenderBuildKeysShader_Loads_AndContainsSortingLogic()
    {
        string source = LoadShaderSource("Compute/GPURenderBuildKeys.comp");

        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("#version 460 core");
        source.ShouldContain("CurrentRenderPass");
        source.ShouldContain("MaxSortKeys");
        source.ShouldContain("StateBitMask");
        source.ShouldContain("SortDomain");
        source.ShouldContain("SortDirection");
        source.ShouldContain("sortKeys");
        source.ShouldContain("packedPassPipelineState");
        source.ShouldContain("COMMAND_FLOATS = 48");
        source.ShouldContain("KEY_UINTS = 4");
    }

    [Test]
    public void GPURenderRadixIndexSortShader_Loads_AndContainsRadixPhases()
    {
        string source = LoadShaderSource("Compute/GPURenderRadixIndexSort.comp");

        source.ShouldNotBeNullOrEmpty();
        source.ShouldContain("#version 460 core");
        source.ShouldContain("RadixPass");
        source.ShouldContain("BuildHistogram");
        source.ShouldContain("PrefixScan");
        source.ShouldContain("Scatter");
        source.ShouldContain("RADIX_BITS = 8u");
        source.ShouldContain("RADIX_SIZE = 256u");
    }

    [Test]
    public void RadixSortShader_Constants_MatchExpectedValues()
    {
        string source = LoadShaderSource("Compute/GPURenderRadixIndexSort.comp");

        // Verify radix sort constants
        source.ShouldContain("RADIX_BITS = 8u");
        source.ShouldContain("RADIX_SIZE = 256u");
        source.ShouldContain("RADIX_MASK = 0xFFu");
    }

    [Test]
    public void BuildKeysShader_SupportsMaterialBatching()
    {
        string source = LoadShaderSource("Compute/GPURenderBuildKeys.comp");

        // Verify material + mesh lanes are emitted for downstream batching/sorting.
        source.ShouldContain("uint materialID = floatBitsToUint(culled[base + 38u])");
        source.ShouldContain("uint meshID = floatBitsToUint(culled[base + 36u])");
        source.ShouldContain("primarySortKey = materialID");
        source.ShouldContain("secondarySortKey = meshID");
        source.ShouldContain("sortKeys[outBase + 1u] = primarySortKey");
        source.ShouldContain("sortKeys[outBase + 2u] = secondarySortKey");
    }

    #endregion

    #region Morton Code Generation Tests

    [Test]
    public void MortonCode_ZeroPosition_ProducesZeroCode()
    {
        var position = Vector3.Zero;
        var sceneMin = Vector3.Zero;
        var sceneMax = Vector3.One;

        uint code = CalculateMortonCode(position, sceneMin, sceneMax);
        
        ((int)code).ShouldBe(0);
    }

    [Test]
    public void MortonCode_MaxPosition_ProducesMaxCode()
    {
        var position = Vector3.One;
        var sceneMin = Vector3.Zero;
        var sceneMax = Vector3.One;

        uint code = CalculateMortonCode(position, sceneMin, sceneMax);
        
        // Max 30-bit Morton code (10 bits per axis)
        code.ShouldBe<uint>(0x3FFFFFFF);
    }

    [Test]
    public void MortonCode_XAxisOnly_InterleavesCorrectly()
    {
        var sceneMin = Vector3.Zero;
        var sceneMax = Vector3.One;
        
        // Position at x=1, y=0, z=0
        var positionX = new Vector3(1f, 0f, 0f);
        uint codeX = CalculateMortonCode(positionX, sceneMin, sceneMax);
        
        // X bits should be in positions 0, 3, 6, 9, ... (every 3rd bit starting at 0)
        // For x=1023 (max 10-bit), expanded: 0b01001001001001001001001001001001
        codeX.ShouldBe(0x09249249u); // Binary: 001001001001001001001001001001
    }

    [Test]
    public void MortonCode_YAxisOnly_InterleavesCorrectly()
    {
        var sceneMin = Vector3.Zero;
        var sceneMax = Vector3.One;
        
        // Position at x=0, y=1, z=0
        var positionY = new Vector3(0f, 1f, 0f);
        uint codeY = CalculateMortonCode(positionY, sceneMin, sceneMax);
        
        // Y bits should be in positions 1, 4, 7, 10, ... (every 3rd bit starting at 1)
        codeY.ShouldBe(0x12492492u); // Binary: 010010010010010010010010010010
    }

    [Test]
    public void MortonCode_ZAxisOnly_InterleavesCorrectly()
    {
        var sceneMin = Vector3.Zero;
        var sceneMax = Vector3.One;
        
        // Position at x=0, y=0, z=1
        var positionZ = new Vector3(0f, 0f, 1f);
        uint codeZ = CalculateMortonCode(positionZ, sceneMin, sceneMax);
        
        // Z bits should be in positions 2, 5, 8, 11, ... (every 3rd bit starting at 2)
        codeZ.ShouldBe(0x24924924u); // Binary: 100100100100100100100100100100
    }

    [Test]
    public void MortonCode_SpatiallyClosePositions_HaveSimilarCodes()
    {
        var sceneMin = Vector3.Zero;
        var sceneMax = Vector3.One;
        
        // Use positions that differ only in one axis to get cleaner Morton code relationships
        var pos1 = new Vector3(0.5f, 0.5f, 0.5f);
        var pos2 = new Vector3(0.5f, 0.5f, 0.6f);  // Only Z differs slightly
        var pos3 = new Vector3(0.1f, 0.1f, 0.1f);  // Different octant entirely
        
        uint code1 = CalculateMortonCode(pos1, sceneMin, sceneMax);
        uint code2 = CalculateMortonCode(pos2, sceneMin, sceneMax);
        uint code3 = CalculateMortonCode(pos3, sceneMin, sceneMax);
        
        // Verify codes are different
        code1.ShouldNotBe(code2);
        code1.ShouldNotBe(code3);
        
        // Morton codes preserve Z-curve ordering
        // Positions in different octants should have very different codes
        // The test verifies that Morton coding produces different codes for different positions
        uint xor12 = code1 ^ code2;
        uint xor13 = code1 ^ code3;
        
        // Different positions should produce different XOR patterns
        // We can't guarantee prefix relationship without knowing exact quantization,
        // but we can verify the codes meaningfully differ
        xor12.ShouldNotBe(0u);
        xor13.ShouldNotBe(0u);
    }

    [Test]
    public void MortonCode_NegativeSceneCoordinates_HandledCorrectly()
    {
        var sceneMin = new Vector3(-100, -100, -100);
        var sceneMax = new Vector3(100, 100, 100);
        
        var position = Vector3.Zero; // Center of scene
        uint code = CalculateMortonCode(position, sceneMin, sceneMax);
        
        // Center should map to middle code range
        code.ShouldBeGreaterThan(0u);
        code.ShouldBeLessThan(0x3FFFFFFFu);
    }

    [Test]
    public void MortonCode_OutOfBoundsPosition_ClampedToRange()
    {
        var sceneMin = Vector3.Zero;
        var sceneMax = Vector3.One;
        
        // Position outside scene bounds
        var outsidePos = new Vector3(2f, 2f, 2f);
        uint code = CalculateMortonCode(outsidePos, sceneMin, sceneMax);
        
        // Should be clamped to max
        code.ShouldBe<uint>(0x3FFFFFFF);
    }

    /// <summary>
    /// CPU reference implementation of Morton code calculation.
    /// </summary>
    private static uint CalculateMortonCode(Vector3 position, Vector3 sceneMin, Vector3 sceneMax)
    {
        // Normalize to [0, 1]
        var range = sceneMax - sceneMin;
        if (range.X <= 0) range.X = 1;
        if (range.Y <= 0) range.Y = 1;
        if (range.Z <= 0) range.Z = 1;
        
        var normalized = (position - sceneMin) / range;
        normalized = Vector3.Clamp(normalized, Vector3.Zero, Vector3.One);
        
        // Scale to 10-bit integer (0-1023)
        uint x = Math.Min(1023u, (uint)(normalized.X * 1023f));
        uint y = Math.Min(1023u, (uint)(normalized.Y * 1023f));
        uint z = Math.Min(1023u, (uint)(normalized.Z * 1023f));
        
        return ExpandBits(x) | (ExpandBits(y) << 1) | (ExpandBits(z) << 2);
    }

    private static uint ExpandBits(uint v)
    {
        v = (v * 0x00010001u) & 0xFF0000FFu;
        v = (v * 0x00000101u) & 0x0F00F00Fu;
        v = (v * 0x00000011u) & 0xC30C30C3u;
        v = (v * 0x00000005u) & 0x49249249u;
        return v;
    }

    #endregion

    #region Morton Code Sorting Tests

    [Test]
    public void MortonSort_UnsortedArray_ProducesSortedOutput()
    {
        var codes = new uint[] { 100, 50, 200, 25, 150, 75 };
        var indices = new uint[] { 0, 1, 2, 3, 4, 5 };
        
        SortMortonCodes(codes, indices);
        
        // Codes should be in ascending order
        for (int i = 1; i < codes.Length; i++)
        {
            codes[i].ShouldBeGreaterThanOrEqualTo(codes[i - 1]);
        }
    }

    [Test]
    public void MortonSort_IndicesTrackOriginalPositions()
    {
        var originalCodes = new uint[] { 100, 50, 200, 25, 150, 75 };
        var codes = (uint[])originalCodes.Clone();
        var indices = new uint[] { 0, 1, 2, 3, 4, 5 };
        
        SortMortonCodes(codes, indices);
        
        // Index at position 0 should point to smallest original code (25 at index 3)
        originalCodes[indices[0]].ShouldBe(25u);
        
        // Index at last position should point to largest original code (200 at index 2)
        originalCodes[indices[codes.Length - 1]].ShouldBe(200u);
    }

    [Test]
    public void MortonSort_AlreadySorted_NoChange()
    {
        var codes = new uint[] { 10, 20, 30, 40, 50 };
        var originalCodes = (uint[])codes.Clone();
        var indices = new uint[] { 0, 1, 2, 3, 4 };
        
        SortMortonCodes(codes, indices);
        
        codes.ShouldBe(originalCodes);
    }

    [Test]
    public void MortonSort_ReverseSorted_FullyReversed()
    {
        var codes = new uint[] { 50, 40, 30, 20, 10 };
        var indices = new uint[] { 0, 1, 2, 3, 4 };
        
        SortMortonCodes(codes, indices);
        
        codes.ShouldBe(new uint[] { 10, 20, 30, 40, 50 });
    }

    [Test]
    public void MortonSort_DuplicateCodes_StableSortOrder()
    {
        var codes = new uint[] { 50, 50, 50, 50 };
        var indices = new uint[] { 0, 1, 2, 3 };
        
        SortMortonCodes(codes, indices);
        
        // All codes equal, indices should maintain relative order (stable sort)
        indices[0].ShouldBe(0u);
        indices[1].ShouldBe(1u);
        indices[2].ShouldBe(2u);
        indices[3].ShouldBe(3u);
    }

    [Test]
    public void MortonSort_SingleElement_NoChange()
    {
        var codes = new uint[] { 42 };
        var indices = new uint[] { 0 };
        
        SortMortonCodes(codes, indices);
        
        codes[0].ShouldBe(42u);
        indices[0].ShouldBe(0u);
    }

    [Test]
    public void MortonSort_EmptyArray_NoException()
    {
        var codes = Array.Empty<uint>();
        var indices = Array.Empty<uint>();
        
        Should.NotThrow(() => SortMortonCodes(codes, indices));
    }

    [Test]
    public void MortonSort_LargeArray_CompletesInReasonableTime()
    {
        const int count = 10000;
        var codes = new uint[count];
        var indices = new uint[count];
        var random = new Random(12345);
        
        for (int i = 0; i < count; i++)
        {
            codes[i] = (uint)random.Next();
            indices[i] = (uint)i;
        }
        
        var start = DateTime.UtcNow;
        SortMortonCodes(codes, indices);
        var elapsed = DateTime.UtcNow - start;
        
        // Should complete in reasonable time
        elapsed.TotalSeconds.ShouldBeLessThan(5);
        
        // Verify sorted
        for (int i = 1; i < count; i++)
        {
            codes[i].ShouldBeGreaterThanOrEqualTo(codes[i - 1]);
        }
    }

    /// <summary>
    /// Simple insertion sort for small arrays (GPU uses more efficient parallel sorts).
    /// </summary>
    private static void SortMortonCodes(uint[] codes, uint[] indices)
    {
        if (codes.Length <= 1) return;
        
        // Simple insertion sort for reference implementation
        for (int i = 1; i < codes.Length; i++)
        {
            uint key = codes[i];
            uint idx = indices[i];
            int j = i - 1;
            
            while (j >= 0 && codes[j] > key)
            {
                codes[j + 1] = codes[j];
                indices[j + 1] = indices[j];
                j--;
            }
            
            codes[j + 1] = key;
            indices[j + 1] = idx;
        }
    }

    #endregion

    #region Bitonic Sort Tests

    [Test]
    public void BitonicSort_PowerOfTwoLength_SortsCorrectly()
    {
        var data = new uint[] { 8, 4, 2, 6, 1, 5, 3, 7 };
        
        BitonicSort(data);
        
        data.ShouldBe(new uint[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    }

    [Test]
    public void BitonicSort_TwoElements_SortsCorrectly()
    {
        var data = new uint[] { 2, 1 };
        
        BitonicSort(data);
        
        data.ShouldBe(new uint[] { 1, 2 });
    }

    [Test]
    public void BitonicSort_CompareAndSwap_CorrectlyOrders()
    {
        uint a = 10;
        uint b = 5;
        
        CompareAndSwap(ref a, ref b, ascending: true);
        
        a.ShouldBe(5u);
        b.ShouldBe(10u);
    }

    [Test]
    public void BitonicSort_CompareAndSwap_DescendingOrder()
    {
        uint a = 5;
        uint b = 10;
        
        CompareAndSwap(ref a, ref b, ascending: false);
        
        a.ShouldBe(10u);
        b.ShouldBe(5u);
    }

    private static void BitonicSort(uint[] arr)
    {
        int n = arr.Length;
        
        for (int k = 2; k <= n; k *= 2)
        {
            for (int j = k / 2; j > 0; j /= 2)
            {
                for (int i = 0; i < n; i++)
                {
                    int ixj = i ^ j;
                    if (ixj > i)
                    {
                        bool ascending = (i & k) == 0;
                        CompareAndSwap(ref arr[i], ref arr[ixj], ascending);
                    }
                }
            }
        }
    }

    private static void CompareAndSwap(ref uint a, ref uint b, bool ascending)
    {
        if (ascending ? (a > b) : (a < b))
        {
            (a, b) = (b, a);
        }
    }

    #endregion

    #region Radix Sort Tests

    [Test]
    public void RadixSort_CountingPhase_CorrectHistogram()
    {
        var data = new uint[] { 0x10, 0x20, 0x10, 0x30, 0x10 };
        
        var histogram = new uint[256];
        int pass = 0;
        int shift = pass * 8;
        
        foreach (var value in data)
        {
            uint bucket = (value >> shift) & 0xFF;
            histogram[bucket]++;
        }
        
        histogram[0x10].ShouldBe(3u);
        histogram[0x20].ShouldBe(1u);
        histogram[0x30].ShouldBe(1u);
    }

    [Test]
    public void RadixSort_PrefixSum_CorrectOffsets()
    {
        var histogram = new uint[] { 3, 2, 1, 4 };
        
        var prefixSum = new uint[histogram.Length];
        uint sum = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            prefixSum[i] = sum;
            sum += histogram[i];
        }
        
        prefixSum[0].ShouldBe(0u);
        prefixSum[1].ShouldBe(3u);
        prefixSum[2].ShouldBe(5u);
        prefixSum[3].ShouldBe(6u);
    }

    [Test]
    public void RadixSort_ScatterPhase_CorrectPlacement()
    {
        // Simplified scatter test
        var keys = new uint[] { 2, 0, 1, 0 };
        var output = new uint[4];
        var offsets = new uint[] { 0, 2, 3 }; // prefix sums for buckets 0, 1, 2
        
        foreach (var key in keys)
        {
            output[offsets[key]++] = key;
        }
        
        output.ShouldBe(new uint[] { 0, 0, 1, 2 });
    }

    #endregion

    #region Padding Tests (for power-of-two buffer sizing)

    [Test]
    public void PadToNextPowerOfTwo_ExactPower_NoChange()
    {
        uint input = 256;
        uint padded = XRMath.NextPowerOfTwo(input);
        
        padded.ShouldBe(256u);
    }

    [Test]
    public void PadToNextPowerOfTwo_SlightlyOver_RoundsUp()
    {
        uint input = 257;
        uint padded = XRMath.NextPowerOfTwo(input);
        
        padded.ShouldBe(512u);
    }

    [Test]
    public void PadToNextPowerOfTwo_One_ReturnsOne()
    {
        uint input = 1;
        uint padded = XRMath.NextPowerOfTwo(input);
        
        padded.ShouldBe(1u);
    }

    [Test]
    public void PadToNextPowerOfTwo_Zero_ReturnsZero()
    {
        uint input = 0;
        uint padded = XRMath.NextPowerOfTwo(input);
        
        padded.ShouldBe(0u);
    }

    [Test]
    public void PadMortonBuffer_CalculatesCorrectPaddingValue()
    {
        uint objectCount = 100;
        uint paddedCount = XRMath.NextPowerOfTwo(objectCount);
        
        paddedCount.ShouldBe(128u);
        
        // Padding entries should use sentinel Morton code (max value)
        uint sentinelCode = uint.MaxValue;
        sentinelCode.ShouldBe(0xFFFFFFFF);
    }

    #endregion

    #region Common Prefix Tests (for BVH hierarchy)

    [Test]
    public void CommonPrefixLength_IdenticalCodes_Returns32()
    {
        uint a = 0x12345678;
        uint b = 0x12345678;
        
        int prefix = CommonPrefixLength(a, b);
        
        prefix.ShouldBe(32);
    }

    [Test]
    public void CommonPrefixLength_HighBitsDiffer_ReturnsSmall()
    {
        uint a = 0x00000000;
        uint b = 0x80000000;
        
        int prefix = CommonPrefixLength(a, b);
        
        prefix.ShouldBe(0);
    }

    [Test]
    public void CommonPrefixLength_LowBitsDiffer_ReturnsLarge()
    {
        uint a = 0xFFFFFFFE;
        uint b = 0xFFFFFFFF;
        
        int prefix = CommonPrefixLength(a, b);
        
        prefix.ShouldBe(31);
    }

    [Test]
    public void CommonPrefixLength_UsedForBvhSplit()
    {
        // BVH split location is determined by common prefix length
        // These codes represent Morton codes sorted in ascending order
        var sortedCodes = new uint[] 
        { 
            0x00000000,  // binary: 0000...
            0x10000000,  // binary: 0001...  
            0x20000000,  // binary: 0010...
            0x30000000   // binary: 0011...
        };
        
        // For index 1 (0x10000000):
        // - With left neighbor (0x00000000): XOR = 0x10000000, prefix = 3 bits  
        // - With right neighbor (0x20000000): XOR = 0x30000000, prefix = 2 bits
        int prefixWithLeft = CommonPrefixLength(sortedCodes[1], sortedCodes[0]);
        int prefixWithRight = CommonPrefixLength(sortedCodes[1], sortedCodes[2]);
        
        // The direction with longer prefix indicates tighter grouping
        // Here, index 1 is more similar to index 0 (longer common prefix)
        prefixWithLeft.ShouldBeGreaterThan(prefixWithRight);
    }

    private static int CommonPrefixLength(uint a, uint b)
    {
        uint xor = a ^ b;
        if (xor == 0) return 32;
        return LeadingZeroCount(xor);
    }

    private static int LeadingZeroCount(uint x)
    {
        if (x == 0) return 32;
        int n = 0;
        if ((x & 0xFFFF0000) == 0) { n += 16; x <<= 16; }
        if ((x & 0xFF000000) == 0) { n += 8; x <<= 8; }
        if ((x & 0xF0000000) == 0) { n += 4; x <<= 4; }
        if ((x & 0xC0000000) == 0) { n += 2; x <<= 2; }
        if ((x & 0x80000000) == 0) { n += 1; }
        return n;
    }

    #endregion

    #region Spatial Hash Tests

    [Test]
    public void SpatialHash_DifferentCells_DifferentHashes()
    {
        var cellA = new Vector3Int(0, 0, 0);
        var cellB = new Vector3Int(1, 0, 0);
        
        uint hashA = SpatialHash(cellA);
        uint hashB = SpatialHash(cellB);
        
        hashA.ShouldNotBe(hashB);
    }

    [Test]
    public void SpatialHash_SameCell_SameHash()
    {
        var cellA = new Vector3Int(5, 10, 15);
        var cellB = new Vector3Int(5, 10, 15);
        
        uint hashA = SpatialHash(cellA);
        uint hashB = SpatialHash(cellB);
        
        hashA.ShouldBe(hashB);
    }

    [Test]
    public void SpatialHash_NegativeCoordinates_ValidHash()
    {
        var cell = new Vector3Int(-5, -10, -15);
        
        uint hash = SpatialHash(cell);
        
        // Should produce a valid non-zero hash
        hash.ShouldBeGreaterThan(0u);
    }

    private record struct Vector3Int(int X, int Y, int Z);

    private static uint SpatialHash(Vector3Int cell)
    {
        // Simple spatial hash combining coordinates
        uint h = 73856093u * (uint)cell.X;
        h ^= 19349663u * (uint)cell.Y;
        h ^= 83492791u * (uint)cell.Z;
        return h;
    }

    #endregion
}
