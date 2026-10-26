using System.Runtime.InteropServices;
namespace XREngine.Rendering.Meshlets;

internal static partial class NativeMeshOptimizer
{
    private const string NativeLibraryName = "meshoptimizer";
    private const string VersionFileName = "meshoptimizer.version";
    private const int InteropVersion = 3;
    private const uint SimplifyPermissiveWithSeamsMask = (uint)MeshOptimizerSimplifyOptions.Permissive;
    private static readonly Lazy<nint> s_nativeLibraryHandle = new(LoadNativeLibraryHandle, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<string> s_versionText = new(ReadVersionText, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<MeshoptOptimizeMeshletLevelDelegate?> s_optimizeMeshletLevel = new(() => TryLoadExport<MeshoptOptimizeMeshletLevelDelegate>("meshopt_optimizeMeshletLevel"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<MeshoptOptimizeMeshletDelegate?> s_optimizeMeshlet = new(() => TryLoadExport<MeshoptOptimizeMeshletDelegate>("meshopt_optimizeMeshlet"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static int s_loggedLegacyOptimizeLevelFallback;
    private static int s_loggedMissingOptimizeMeshletExport;

    static NativeMeshOptimizer()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeMeshOptimizer).Assembly, ResolveNativeLibrary);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static nint ResolveNativeLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        => libraryName == NativeLibraryName ? s_nativeLibraryHandle.Value : IntPtr.Zero;

    /// <summary>
    /// Finds the native library file. Order: the assembly folder, the application base folder,
    /// then runtimes/&lt;rid&gt;/native under each of them.
    /// </summary>
    internal static string? FindNativeLibraryFile(string? assemblyDirectory, string baseDirectory, string rid, string fileName)
    {
        foreach (string directory in EnumerateProbeDirectories(assemblyDirectory, baseDirectory, rid))
        {
            string path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateProbeDirectories(string? assemblyDirectory, string baseDirectory, string rid)
    {
        if (!string.IsNullOrEmpty(assemblyDirectory))
            yield return assemblyDirectory;

        yield return baseDirectory;

        if (!string.IsNullOrEmpty(assemblyDirectory))
            yield return Path.Combine(assemblyDirectory, "runtimes", rid, "native");

        yield return Path.Combine(baseDirectory, "runtimes", rid, "native");
    }

    private static string CurrentRid => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    private static string NativeFileName => OperatingSystem.IsWindows() ? "meshoptimizer.dll" : "libmeshoptimizer.so";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void MeshoptOptimizeMeshletLevelDelegate(uint* meshletVertices, nuint vertexCount, byte* meshletTriangles, nuint triangleCount, int level);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void MeshoptOptimizeMeshletDelegate(uint* meshletVertices, byte* meshletTriangles, nuint triangleCount, nuint vertexCount);





    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_buildMeshletsBound")]
    private static extern nuint MeshoptBuildMeshletsBound(nuint indexCount, nuint maxVertices, nuint maxTriangles);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_buildMeshlets")]
    private static extern unsafe nuint MeshoptBuildMeshlets(
        MeshOptimizerMeshlet* meshlets,
        uint* meshletVertices,
        byte* meshletTriangles,
        uint* indices,
        nuint indexCount,
        float* vertexPositions,
        nuint vertexCount,
        nuint vertexPositionsStride,
        nuint maxVertices,
        nuint maxTriangles,
        float coneWeight);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_buildMeshletsScan")]
    private static extern unsafe nuint MeshoptBuildMeshletsScan(
        MeshOptimizerMeshlet* meshlets,
        uint* meshletVertices,
        byte* meshletTriangles,
        uint* indices,
        nuint indexCount,
        nuint vertexCount,
        nuint maxVertices,
        nuint maxTriangles);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_buildMeshletsFlex")]
    private static extern unsafe nuint MeshoptBuildMeshletsFlex(
        MeshOptimizerMeshlet* meshlets,
        uint* meshletVertices,
        byte* meshletTriangles,
        uint* indices,
        nuint indexCount,
        float* vertexPositions,
        nuint vertexCount,
        nuint vertexPositionsStride,
        nuint maxVertices,
        nuint minTriangles,
        nuint maxTriangles,
        float coneWeight,
        float splitFactor);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_buildMeshletsSpatial")]
    private static extern unsafe nuint MeshoptBuildMeshletsSpatial(
        MeshOptimizerMeshlet* meshlets,
        uint* meshletVertices,
        byte* meshletTriangles,
        uint* indices,
        nuint indexCount,
        float* vertexPositions,
        nuint vertexCount,
        nuint vertexPositionsStride,
        nuint maxVertices,
        nuint minTriangles,
        nuint maxTriangles,
        float fillWeight);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_computeMeshletBounds")]
    private static extern unsafe MeshOptimizerBounds MeshoptComputeMeshletBounds(uint* meshletVertices, byte* meshletTriangles, nuint triangleCount, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_encodeMeshletBound")]
    private static extern nuint MeshoptEncodeMeshletBound(nuint maxVertices, nuint maxTriangles);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_encodeMeshlet")]
    private static extern unsafe nuint MeshoptEncodeMeshlet(byte* buffer, nuint bufferSize, uint* vertices, nuint vertexCount, byte* triangles, nuint triangleCount);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_simplify")]
    private static extern unsafe nuint MeshoptSimplify(uint* destination, uint* indices, nuint indexCount, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride, nuint targetIndexCount, float targetError, uint options, float* resultError);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_simplifyWithAttributes")]
    private static extern unsafe nuint MeshoptSimplifyWithAttributes(uint* destination, uint* indices, nuint indexCount, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride, float* vertexAttributes, nuint vertexAttributesStride, float* attributeWeights, nuint attributeCount, byte* vertexLock, nuint targetIndexCount, float targetError, uint options, float* resultError);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_simplifyWithUpdate")]
    private static extern unsafe nuint MeshoptSimplifyWithUpdate(uint* indices, nuint indexCount, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride, float* vertexAttributes, nuint vertexAttributesStride, float* attributeWeights, nuint attributeCount, byte* vertexLock, nuint targetIndexCount, float targetError, uint options, float* resultError);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_simplifySloppy")]
    private static extern unsafe nuint MeshoptSimplifySloppy(uint* destination, uint* indices, nuint indexCount, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride, byte* vertexLock, nuint targetIndexCount, float targetError, float* resultError);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_simplifyScale")]
    private static extern unsafe float MeshoptSimplifyScale(float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride);

    public static string VersionKey
        => $"{s_versionText.Value};interop:{InteropVersion}";

    private static string ReadVersionText()
    {
        foreach (string directory in EnumerateNativeLibraryDirectories())
        {
            string versionPath = Path.Combine(directory, VersionFileName);
            if (!File.Exists(versionPath))
                continue;

            try
            {
                string text = File.ReadAllText(versionPath).Trim();
                if (text.Length > 0)
                    return text;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return "unknown";
    }

    private static IEnumerable<string> EnumerateNativeLibraryDirectories()
    {
        nint handle = s_nativeLibraryHandle.Value;
        if (handle != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            char[] buffer = new char[1024];
            uint length = GetModuleFileNameW(handle, buffer, (uint)buffer.Length);
            if (length > 0 && length < buffer.Length && Path.GetDirectoryName(new string(buffer, 0, (int)length)) is { Length: > 0 } loadedDirectory)
                yield return loadedDirectory;
        }

        foreach (string directory in EnumerateProbeDirectories(Path.GetDirectoryName(typeof(NativeMeshOptimizer).Assembly.Location), AppContext.BaseDirectory, CurrentRid))
            yield return directory;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetModuleFileNameW(nint module, [Out] char[] fileName, uint size);

    public static nuint BuildMeshletsBound(nuint indexCount, uint maxVertices, uint maxTriangles)
        => MeshoptBuildMeshletsBound(indexCount, (nuint)maxVertices, (nuint)maxTriangles);

    public static unsafe nuint BuildNativeMeshletClusters(MeshletBuildMode buildMode, MeshOptimizerMeshlet[] meshlets, uint[] meshletVertices, byte[] meshletTriangles, uint[] indices, float[] vertexPositions, nuint vertexCount, uint maxVertices, uint minTriangles, uint maxTriangles, float coneWeight, float splitFactor, float fillWeight)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(vertexPositions);
        if ((indices.Length != 0 && vertexCount == 0) ||
            vertexCount > (nuint)(int.MaxValue / 3) || vertexPositions.Length != (int)vertexCount * 3)
            throw new ArgumentException("Meshlet positions must contain exactly three floats for every native vertex.", nameof(vertexPositions));

        fixed (MeshOptimizerMeshlet* meshletPtr = meshlets)
        fixed (uint* meshletVerticesPtr = meshletVertices)
        fixed (byte* meshletTrianglesPtr = meshletTriangles)
        fixed (uint* indicesPtr = indices)
        fixed (float* positionsPtr = vertexPositions)
        {
            return buildMode switch
            {
                MeshletBuildMode.Scan => MeshoptBuildMeshletsScan(meshletPtr, meshletVerticesPtr, meshletTrianglesPtr, indicesPtr, (nuint)indices.Length, vertexCount, (nuint)maxVertices, (nuint)maxTriangles),
                MeshletBuildMode.Flex => MeshoptBuildMeshletsFlex(meshletPtr, meshletVerticesPtr, meshletTrianglesPtr, indicesPtr, (nuint)indices.Length, positionsPtr, vertexCount, sizeof(float) * 3u, (nuint)maxVertices, (nuint)minTriangles, (nuint)maxTriangles, coneWeight, splitFactor),
                MeshletBuildMode.Spatial => MeshoptBuildMeshletsSpatial(meshletPtr, meshletVerticesPtr, meshletTrianglesPtr, indicesPtr, (nuint)indices.Length, positionsPtr, vertexCount, sizeof(float) * 3u, (nuint)maxVertices, (nuint)minTriangles, (nuint)maxTriangles, fillWeight),
                _ => MeshoptBuildMeshlets(meshletPtr, meshletVerticesPtr, meshletTrianglesPtr, indicesPtr, (nuint)indices.Length, positionsPtr, vertexCount, sizeof(float) * 3u, (nuint)maxVertices, (nuint)maxTriangles, coneWeight),
            };
        }
    }

    public static unsafe void OptimizeMeshletLevel(Span<uint> meshletVertices, Span<byte> meshletTriangles, int level)
    {
        if (meshletVertices.IsEmpty || meshletTriangles.IsEmpty)
            return;

        fixed (uint* verticesPtr = meshletVertices)
        fixed (byte* trianglesPtr = meshletTriangles)
        {
            nuint triangleCount = (nuint)(meshletTriangles.Length / 3);
            if (triangleCount == 0)
                return;

            if (s_optimizeMeshletLevel.Value is { } optimizeMeshletLevel)
            {
                optimizeMeshletLevel(verticesPtr, (nuint)meshletVertices.Length, trianglesPtr, triangleCount, level);
                return;
            }

            if (s_optimizeMeshlet.Value is { } optimizeMeshlet)
            {
                optimizeMeshlet(verticesPtr, trianglesPtr, triangleCount, (nuint)meshletVertices.Length);

                if (level != 0 && Interlocked.Exchange(ref s_loggedLegacyOptimizeLevelFallback, 1) == 0)
                {
                    Debug.LogWarning($"meshoptimizer native DLL does not expose 'meshopt_optimizeMeshletLevel'; falling back to legacy 'meshopt_optimizeMeshlet'. OptimizeLevel={level} will be ignored.");
                }

                return;
            }

            if (Interlocked.Exchange(ref s_loggedMissingOptimizeMeshletExport, 1) == 0)
            {
                Debug.LogWarning("meshoptimizer native DLL does not expose meshlet optimization exports. Meshlets will be built without the optional post-build optimization pass.");
            }
        }
    }

    private static nint LoadNativeLibraryHandle()
    {
        try
        {
            string? assemblyDirectory = Path.GetDirectoryName(typeof(NativeMeshOptimizer).Assembly.Location);
            if (FindNativeLibraryFile(assemblyDirectory, AppContext.BaseDirectory, CurrentRid, NativeFileName) is { } path
                && NativeLibrary.TryLoad(path, out IntPtr explicitHandle)
                && explicitHandle != IntPtr.Zero)
            {
                return explicitHandle;
            }
        }
        catch
        {
        }

        try
        {
            if (NativeLibrary.TryLoad(NativeLibraryName, typeof(NativeMeshOptimizer).Assembly, null, out IntPtr assemblyHandle)
                && assemblyHandle != IntPtr.Zero)
            {
                return assemblyHandle;
            }
        }
        catch
        {
        }

        try
        {
            if (NativeLibrary.TryLoad(NativeLibraryName, out IntPtr defaultHandle) && defaultHandle != IntPtr.Zero)
                return defaultHandle;
        }
        catch
        {
        }

        return IntPtr.Zero;
    }

    private static T? TryLoadExport<T>(string exportName) where T : Delegate
    {
        IntPtr libraryHandle = s_nativeLibraryHandle.Value;
        if (libraryHandle == IntPtr.Zero
            || !NativeLibrary.TryGetExport(libraryHandle, exportName, out IntPtr exportPtr)
            || exportPtr == IntPtr.Zero)
        {
            return null;
        }

        return Marshal.GetDelegateForFunctionPointer<T>(exportPtr);
    }

    public static unsafe MeshOptimizerBounds ComputeMeshletBounds(ReadOnlySpan<uint> meshletVertices, ReadOnlySpan<byte> meshletTriangles, float[] vertexPositions, int vertexCount)
    {
        fixed (uint* verticesPtr = meshletVertices)
        fixed (byte* trianglesPtr = meshletTriangles)
        fixed (float* positionsPtr = vertexPositions)
            return MeshoptComputeMeshletBounds(verticesPtr, trianglesPtr, (nuint)(meshletTriangles.Length / 3), positionsPtr, (nuint)vertexCount, sizeof(float) * 3u);
    }

    public static unsafe int EncodeMeshlet(uint[]? vertices, byte[] triangles, int maxVertices, int maxTriangles)
    {
        int bufferSize = (int)MeshoptEncodeMeshletBound((nuint)maxVertices, (nuint)maxTriangles);
        if (bufferSize == 0)
            return 0;

        byte[] buffer = new byte[bufferSize];
        fixed (byte* bufferPtr = buffer)
        fixed (byte* trianglesPtr = triangles)
        {
            if (vertices is null || vertices.Length == 0)
                return (int)MeshoptEncodeMeshlet(bufferPtr, (nuint)buffer.Length, null, 0, trianglesPtr, (nuint)(triangles.Length / 3));

            fixed (uint* verticesPtr = vertices)
                return (int)MeshoptEncodeMeshlet(bufferPtr, (nuint)buffer.Length, verticesPtr, (nuint)vertices.Length, trianglesPtr, (nuint)(triangles.Length / 3));
        }
    }

    public static unsafe int Simplify(uint[] indices, float[] positions, int vertexCount, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, out float resultError)
    {
        fixed (uint* destinationPtr = indices)
        fixed (uint* indicesPtr = indices)
        fixed (float* positionsPtr = positions)
        fixed (float* errorPtr = &resultError)
            return (int)MeshoptSimplify(destinationPtr, indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, (nuint)targetIndexCount, targetError, (uint)options, errorPtr);
    }

    public static unsafe int SimplifyWithAttributes(uint[] indices, float[] positions, int vertexCount, float[] attributeBuffer, int attributeStride, float[] attributeWeights, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, byte[]? vertexLock, out float resultError)
    {
        fixed (uint* destinationPtr = indices)
        fixed (uint* indicesPtr = indices)
        fixed (float* positionsPtr = positions)
        fixed (float* attributesPtr = attributeBuffer)
        fixed (float* weightsPtr = attributeWeights)
        fixed (float* errorPtr = &resultError)
        {
            if (vertexLock is null)
                return (int)MeshoptSimplifyWithAttributes(destinationPtr, indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, attributesPtr, (nuint)(attributeStride * sizeof(float)), weightsPtr, (nuint)attributeWeights.Length, null, (nuint)targetIndexCount, targetError, (uint)options, errorPtr);

            fixed (byte* lockPtr = vertexLock)
                return (int)MeshoptSimplifyWithAttributes(destinationPtr, indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, attributesPtr, (nuint)(attributeStride * sizeof(float)), weightsPtr, (nuint)attributeWeights.Length, lockPtr, (nuint)targetIndexCount, targetError, (uint)options, errorPtr);
        }
    }

    public static unsafe int SimplifyWithUpdate(uint[] indices, float[] positions, int vertexCount, float[] attributeBuffer, int attributeStride, float[] attributeWeights, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, byte[]? vertexLock, out float resultError)
    {
        fixed (uint* indicesPtr = indices)
        fixed (float* positionsPtr = positions)
        fixed (float* attributesPtr = attributeBuffer)
        fixed (float* weightsPtr = attributeWeights)
        fixed (float* errorPtr = &resultError)
        {
            if (vertexLock is null)
                return (int)MeshoptSimplifyWithUpdate(indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, attributesPtr, (nuint)(attributeStride * sizeof(float)), weightsPtr, (nuint)attributeWeights.Length, null, (nuint)targetIndexCount, targetError, (uint)options, errorPtr);

            fixed (byte* lockPtr = vertexLock)
                return (int)MeshoptSimplifyWithUpdate(indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, attributesPtr, (nuint)(attributeStride * sizeof(float)), weightsPtr, (nuint)attributeWeights.Length, lockPtr, (nuint)targetIndexCount, targetError, (uint)options, errorPtr);
        }
    }

    public static unsafe int SimplifySloppy(uint[] indices, float[] positions, int vertexCount, int targetIndexCount, float targetError, byte[]? vertexLock, out float resultError)
    {
        fixed (uint* destinationPtr = indices)
        fixed (uint* indicesPtr = indices)
        fixed (float* positionsPtr = positions)
        fixed (float* errorPtr = &resultError)
        {
            if (vertexLock is null)
                return (int)MeshoptSimplifySloppy(destinationPtr, indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, null, (nuint)targetIndexCount, targetError, errorPtr);

            fixed (byte* lockPtr = vertexLock)
                return (int)MeshoptSimplifySloppy(destinationPtr, indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, sizeof(float) * 3u, lockPtr, (nuint)targetIndexCount, targetError, errorPtr);
        }
    }

    public static unsafe float SimplifyScale(float[] positions, int vertexCount)
    {
        fixed (float* positionsPtr = positions)
            return MeshoptSimplifyScale(positionsPtr, (nuint)vertexCount, sizeof(float) * 3u);
    }
}
