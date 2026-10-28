using System.Runtime.InteropServices;
namespace XREngine.Rendering.Meshlets;

// Vertex and index codecs, vertex filters, point ordering and welding.
internal static partial class NativeMeshOptimizer
{
    private const int MaxVertexCodecSize = 256;

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_encodeVertexBufferBound")]
    private static extern nuint MeshoptEncodeVertexBufferBound(nuint vertexCount, nuint vertexSize);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_encodeVertexBufferLevel")]
    private static extern unsafe nuint MeshoptEncodeVertexBufferLevel(byte* buffer, nuint bufferSize, void* vertices, nuint vertexCount, nuint vertexSize, int level, int version);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_decodeVertexBuffer")]
    private static extern unsafe int MeshoptDecodeVertexBuffer(void* destination, nuint vertexCount, nuint vertexSize, byte* buffer, nuint bufferSize);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_decodeVertexVersion")]
    private static extern unsafe int MeshoptDecodeVertexVersion(byte* buffer, nuint bufferSize);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_encodeIndexBufferBound")]
    private static extern nuint MeshoptEncodeIndexBufferBound(nuint indexCount, nuint vertexCount);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_encodeIndexBuffer")]
    private static extern unsafe nuint MeshoptEncodeIndexBuffer(byte* buffer, nuint bufferSize, uint* indices, nuint indexCount);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_decodeIndexBuffer")]
    private static extern unsafe int MeshoptDecodeIndexBuffer(void* destination, nuint indexCount, nuint indexSize, byte* buffer, nuint bufferSize);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_optimizeVertexCache")]
    private static extern unsafe void MeshoptOptimizeVertexCache(uint* destination, uint* indices, nuint indexCount, nuint vertexCount);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_encodeFilterQuat")]
    private static extern unsafe void MeshoptEncodeFilterQuat(void* destination, nuint count, nuint stride, int bits, float* data);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_encodeFilterExp")]
    private static extern unsafe void MeshoptEncodeFilterExp(void* destination, nuint count, nuint stride, int bits, float* data, int mode);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_encodeFilterColor")]
    private static extern unsafe void MeshoptEncodeFilterColor(void* destination, nuint count, nuint stride, int bits, float* data);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_decodeFilterQuat")]
    private static extern unsafe void MeshoptDecodeFilterQuat(void* buffer, nuint count, nuint stride);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_decodeFilterExp")]
    private static extern unsafe void MeshoptDecodeFilterExp(void* buffer, nuint count, nuint stride);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_decodeFilterColor")]
    private static extern unsafe void MeshoptDecodeFilterColor(void* buffer, nuint count, nuint stride);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_spatialSortRemap")]
    private static extern unsafe void MeshoptSpatialSortRemap(uint* destination, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_spatialClusterPoints")]
    private static extern unsafe void MeshoptSpatialClusterPoints(uint* destination, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride, nuint clusterSize);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_generatePositionRemap")]
    private static extern unsafe void MeshoptGeneratePositionRemap(uint* destination, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_filterIndexBuffer")]
    private static extern unsafe nuint MeshoptFilterIndexBuffer(uint* destination, uint* indices, nuint indexCount, void* vertices, nuint vertexCount, nuint vertexSize, nuint vertexStride);

    // Vertex codec.

    private static void ValidateVertexCodecSize(int vertexCount, int vertexSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        if (vertexSize <= 0 || (vertexSize & 3) != 0 || vertexSize > MaxVertexCodecSize)
            throw new ArgumentException($"Vertex size must be a positive multiple of 4 and at most {MaxVertexCodecSize}. Value: {vertexSize}.", nameof(vertexSize));
    }

    public static int EncodeVertexBufferBound(int vertexCount, int vertexSize)
    {
        ValidateVertexCodecSize(vertexCount, vertexSize);
        return checked((int)MeshoptEncodeVertexBufferBound((nuint)vertexCount, (nuint)vertexSize));
    }

    public static unsafe int EncodeVertexBuffer(Span<byte> destination, ReadOnlySpan<byte> vertices, int vertexCount, int vertexSize, int level, int version)
    {
        ValidateVertexCodecSize(vertexCount, vertexSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 3);
        if (version is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(version), version, "Vertex codec version must be -1, 0 or 1.");
        if ((long)vertices.Length < (long)vertexCount * vertexSize)
            throw new ArgumentException("The vertex data is smaller than vertexCount * vertexSize.", nameof(vertices));
        if (destination.Length < EncodeVertexBufferBound(vertexCount, vertexSize))
            throw new ArgumentException("The destination is smaller than the encoded size bound.", nameof(destination));

        fixed (byte* destinationPtr = destination)
        fixed (byte* verticesPtr = vertices)
        {
            nuint size = MeshoptEncodeVertexBufferLevel(destinationPtr, (nuint)destination.Length, verticesPtr, (nuint)vertexCount, (nuint)vertexSize, level, version);
            if (size == 0 && vertexCount != 0)
                throw new InvalidDataException("The native vertex encoder failed.");
            return (int)size;
        }
    }

    public static unsafe void DecodeVertexBuffer(Span<byte> destination, int vertexCount, int vertexSize, ReadOnlySpan<byte> encoded)
    {
        ValidateVertexCodecSize(vertexCount, vertexSize);
        if ((long)destination.Length < (long)vertexCount * vertexSize)
            throw new ArgumentException("The destination is smaller than vertexCount * vertexSize.", nameof(destination));

        fixed (byte* destinationPtr = destination)
        fixed (byte* encodedPtr = encoded)
        {
            int result = MeshoptDecodeVertexBuffer(destinationPtr, (nuint)vertexCount, (nuint)vertexSize, encodedPtr, (nuint)encoded.Length);
            if (result != 0)
                throw new InvalidDataException($"The vertex buffer data is not valid. Native result: {result}.");
        }
    }

    public static unsafe int DecodeVertexVersion(ReadOnlySpan<byte> encoded)
    {
        fixed (byte* encodedPtr = encoded)
        {
            int version = MeshoptDecodeVertexVersion(encodedPtr, (nuint)encoded.Length);
            if (version < 0)
                throw new InvalidDataException("The vertex buffer header is not valid.");
            return version;
        }
    }

    // Index codec.

    public static int EncodeIndexBufferBound(int indexCount, int vertexCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(indexCount);
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        return checked((int)MeshoptEncodeIndexBufferBound((nuint)indexCount, (nuint)vertexCount));
    }

    public static unsafe int EncodeIndexBuffer(Span<byte> destination, ReadOnlySpan<uint> indices)
    {
        if (indices.Length % 3 != 0)
            throw new ArgumentException("The index count must be a multiple of 3.", nameof(indices));

        uint maxIndex = 0;
        foreach (uint index in indices)
            maxIndex = Math.Max(maxIndex, index);
        int vertexCount = indices.IsEmpty ? 0 : checked((int)Math.Min(maxIndex + 1u, int.MaxValue));
        if (destination.Length < EncodeIndexBufferBound(indices.Length, vertexCount))
            throw new ArgumentException("The destination is smaller than the encoded size bound.", nameof(destination));

        fixed (byte* destinationPtr = destination)
        fixed (uint* indicesPtr = indices)
        {
            nuint size = MeshoptEncodeIndexBuffer(destinationPtr, (nuint)destination.Length, indicesPtr, (nuint)indices.Length);
            if (size == 0 && !indices.IsEmpty)
                throw new InvalidDataException("The native index encoder failed.");
            return (int)size;
        }
    }

    public static unsafe void DecodeIndexBuffer(Span<uint> destination, ReadOnlySpan<byte> encoded)
    {
        // The native decoder writes whole triangles, and a release build has no check of the count, so a count that is
        // not a multiple of 3 writes past the end of the destination.
        if (destination.Length % 3 != 0)
            throw new ArgumentException("The index count must be a multiple of 3.", nameof(destination));

        fixed (uint* destinationPtr = destination)
        fixed (byte* encodedPtr = encoded)
        {
            int result = MeshoptDecodeIndexBuffer(destinationPtr, (nuint)destination.Length, sizeof(uint), encodedPtr, (nuint)encoded.Length);
            if (result != 0)
                throw new InvalidDataException($"The index buffer data is not valid. Native result: {result}.");
        }
    }

    private static void ValidateIndices(ReadOnlySpan<uint> indices, int vertexCount, string paramName)
    {
        if (indices.Length % 3 != 0)
            throw new ArgumentException("The index count must be a multiple of 3.", paramName);
        foreach (uint index in indices)
        {
            if (index >= (uint)vertexCount)
                throw new ArgumentException($"Index {index} is not less than the vertex count {vertexCount}.", paramName);
        }
    }

    public static unsafe void OptimizeVertexCache(Span<uint> destination, ReadOnlySpan<uint> indices, int vertexCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        ValidateIndices(indices, vertexCount, nameof(indices));
        if (destination.Length < indices.Length)
            throw new ArgumentException("The destination is smaller than the index count.", nameof(destination));
        if (indices.IsEmpty)
            return;

        fixed (uint* destinationPtr = destination)
        fixed (uint* indicesPtr = indices)
            MeshoptOptimizeVertexCache(destinationPtr, indicesPtr, (nuint)indices.Length, (nuint)vertexCount);
    }

    // Filters.

    private static void ValidateFilterBuffer(int length, int count, int stride, string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if ((long)length < (long)count * stride)
            throw new ArgumentException("The buffer is smaller than count * stride.", paramName);
    }

    private static void ValidateFilterInput(int length, long required, string paramName)
    {
        if (length < required)
            throw new ArgumentException($"The input holds {length} floats and {required} are required.", paramName);
    }

    private static void ValidateQuatStride(int stride)
    {
        if (stride != 8)
            throw new ArgumentException($"The quaternion filter stride must be 8. Value: {stride}.", nameof(stride));
    }

    private static void ValidateExpStride(int stride)
    {
        if (stride <= 0 || (stride & 3) != 0 || stride > MaxVertexCodecSize)
            throw new ArgumentException($"The exponent filter stride must be a positive multiple of 4 and at most {MaxVertexCodecSize}. Value: {stride}.", nameof(stride));
    }

    private static void ValidateColorStride(int stride)
    {
        if (stride != 4 && stride != 8)
            throw new ArgumentException($"The color filter stride must be 4 or 8. Value: {stride}.", nameof(stride));
    }

    public static unsafe void EncodeFilterQuat(Span<byte> destination, int count, int stride, int bits, ReadOnlySpan<float> quaternionsXyzw)
    {
        ValidateQuatStride(stride);
        if (bits is < 4 or > 16)
            throw new ArgumentOutOfRangeException(nameof(bits), bits, "The quaternion filter needs 4 to 16 bits.");
        ValidateFilterBuffer(destination.Length, count, stride, nameof(destination));
        ValidateFilterInput(quaternionsXyzw.Length, (long)count * 4, nameof(quaternionsXyzw));
        if (count == 0)
            return;

        fixed (byte* destinationPtr = destination)
        fixed (float* dataPtr = quaternionsXyzw)
            MeshoptEncodeFilterQuat(destinationPtr, (nuint)count, (nuint)stride, bits, dataPtr);
    }

    public static unsafe void EncodeFilterExp(Span<byte> destination, int count, int stride, int bits, ReadOnlySpan<float> data, MeshOptimizerExpMode mode)
    {
        ValidateExpStride(stride);
        if (bits is < 1 or > 24)
            throw new ArgumentOutOfRangeException(nameof(bits), bits, "The exponent filter needs 1 to 24 bits.");
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The exponent mode is not known.");
        ValidateFilterBuffer(destination.Length, count, stride, nameof(destination));
        ValidateFilterInput(data.Length, (long)count * (stride / 4), nameof(data));
        if (count == 0)
            return;

        fixed (byte* destinationPtr = destination)
        fixed (float* dataPtr = data)
            MeshoptEncodeFilterExp(destinationPtr, (nuint)count, (nuint)stride, bits, dataPtr, (int)mode);
    }

    public static unsafe void EncodeFilterColor(Span<byte> destination, int count, int stride, int bits, ReadOnlySpan<float> rgba)
    {
        ValidateColorStride(stride);
        if (bits is < 2 or > 16)
            throw new ArgumentOutOfRangeException(nameof(bits), bits, "The color filter needs 2 to 16 bits.");
        if ((bits <= 8) != (stride == 4))
            throw new ArgumentException($"A stride of {stride} does not hold {bits} bits per component.", nameof(bits));
        ValidateFilterBuffer(destination.Length, count, stride, nameof(destination));
        ValidateFilterInput(rgba.Length, (long)count * 4, nameof(rgba));
        if (count == 0)
            return;

        fixed (byte* destinationPtr = destination)
        fixed (float* dataPtr = rgba)
            MeshoptEncodeFilterColor(destinationPtr, (nuint)count, (nuint)stride, bits, dataPtr);
    }

    public static unsafe void DecodeFilterQuat(Span<byte> buffer, int count, int stride)
    {
        ValidateQuatStride(stride);
        ValidateFilterBuffer(buffer.Length, count, stride, nameof(buffer));
        if (count == 0)
            return;

        fixed (byte* bufferPtr = buffer)
            MeshoptDecodeFilterQuat(bufferPtr, (nuint)count, (nuint)stride);
    }

    public static unsafe void DecodeFilterExp(Span<byte> buffer, int count, int stride)
    {
        ValidateExpStride(stride);
        ValidateFilterBuffer(buffer.Length, count, stride, nameof(buffer));
        if (count == 0)
            return;

        fixed (byte* bufferPtr = buffer)
            MeshoptDecodeFilterExp(bufferPtr, (nuint)count, (nuint)stride);
    }

    public static unsafe void DecodeFilterColor(Span<byte> buffer, int count, int stride)
    {
        ValidateColorStride(stride);
        ValidateFilterBuffer(buffer.Length, count, stride, nameof(buffer));
        if (count == 0)
            return;

        fixed (byte* bufferPtr = buffer)
            MeshoptDecodeFilterColor(bufferPtr, (nuint)count, (nuint)stride);
    }

    // Points and welding.

    private static void ValidatePositions(ReadOnlySpan<float> positions, int vertexCount, int positionsStride)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        if (positionsStride < 12 || (positionsStride & 3) != 0)
            throw new ArgumentException($"The position stride must be a multiple of 4 and at least 12. Value: {positionsStride}.", nameof(positionsStride));
        if (vertexCount > 0 && (long)positions.Length < ((long)(vertexCount - 1) * positionsStride) / 4 + 3)
            throw new ArgumentException("The positions are smaller than vertexCount and positionsStride need.", nameof(positions));
    }

    public static unsafe void SpatialSortRemap(Span<uint> destination, ReadOnlySpan<float> positions, int vertexCount, int positionsStride)
    {
        ValidatePositions(positions, vertexCount, positionsStride);
        if (destination.Length < vertexCount)
            throw new ArgumentException("The destination is smaller than the vertex count.", nameof(destination));
        if (vertexCount == 0)
            return;

        fixed (uint* destinationPtr = destination)
        fixed (float* positionsPtr = positions)
            MeshoptSpatialSortRemap(destinationPtr, positionsPtr, (nuint)vertexCount, (nuint)positionsStride);
    }

    public static unsafe void SpatialClusterPoints(Span<uint> destination, ReadOnlySpan<float> positions, int vertexCount, int positionsStride, int clusterSize)
    {
        ValidatePositions(positions, vertexCount, positionsStride);
        ArgumentOutOfRangeException.ThrowIfLessThan(clusterSize, 1);
        if (destination.Length < vertexCount)
            throw new ArgumentException("The destination is smaller than the vertex count.", nameof(destination));
        if (vertexCount == 0)
            return;

        fixed (uint* destinationPtr = destination)
        fixed (float* positionsPtr = positions)
            MeshoptSpatialClusterPoints(destinationPtr, positionsPtr, (nuint)vertexCount, (nuint)positionsStride, (nuint)clusterSize);
    }

    public static unsafe void GeneratePositionRemap(Span<uint> destination, ReadOnlySpan<float> positions, int vertexCount, int positionsStride)
    {
        ValidatePositions(positions, vertexCount, positionsStride);
        if (destination.Length < vertexCount)
            throw new ArgumentException("The destination is smaller than the vertex count.", nameof(destination));
        if (vertexCount == 0)
            return;

        fixed (uint* destinationPtr = destination)
        fixed (float* positionsPtr = positions)
            MeshoptGeneratePositionRemap(destinationPtr, positionsPtr, (nuint)vertexCount, (nuint)positionsStride);
    }

    public static unsafe int FilterIndexBuffer(Span<uint> destination, ReadOnlySpan<uint> indices, ReadOnlySpan<float> vertices, int vertexCount, int vertexSize, int vertexStride)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        if (vertexSize <= 0 || vertexStride < vertexSize)
            throw new ArgumentException("The vertex size must be positive and not larger than the vertex stride.", nameof(vertexSize));
        if ((vertexStride & 3) != 0)
            throw new ArgumentException("The vertex stride must be a multiple of 4.", nameof(vertexStride));
        if (vertexCount > 0 && (long)vertices.Length * sizeof(float) < (long)(vertexCount - 1) * vertexStride + vertexSize)
            throw new ArgumentException("The vertices are smaller than vertexCount and vertexStride need.", nameof(vertices));
        ValidateIndices(indices, vertexCount, nameof(indices));
        if (destination.Length < indices.Length)
            throw new ArgumentException("The destination is smaller than the index count.", nameof(destination));
        if (indices.IsEmpty)
            return 0;

        fixed (uint* destinationPtr = destination)
        fixed (uint* indicesPtr = indices)
        fixed (float* verticesPtr = vertices)
            return (int)MeshoptFilterIndexBuffer(destinationPtr, indicesPtr, (nuint)indices.Length, verticesPtr, (nuint)vertexCount, (nuint)vertexSize, (nuint)vertexStride);
    }
}
