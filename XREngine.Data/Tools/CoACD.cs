using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Data.Tools
{
    public static class CoACD
    {
        private const string NativeLibraryName = "lib_coacd";

        public static Task<IReadOnlyList<ConvexHullMesh>?> CalculateAsync(
            Vector3[] positions,
            int[] triangleIndices,
            CoACDParameters? parameters = null,
            CancellationToken cancellationToken = default)
            => Task.Run(() => Calculate(positions, triangleIndices, parameters), cancellationToken);

        public static IReadOnlyList<ConvexHullMesh>? Calculate(
            Vector3[] positions,
            int[] triangleIndices,
            CoACDParameters? parameters = null)
        {
            ArgumentNullException.ThrowIfNull(positions);
            ArgumentNullException.ThrowIfNull(triangleIndices);

            if (positions.Length == 0 || triangleIndices.Length == 0)
                return [];

            if (triangleIndices.Length % 3 != 0)
                throw new ArgumentException("Triangle index buffer length must be divisible by 3.", nameof(triangleIndices));

            var config = parameters ?? CoACDParameters.Default;
            double[] vertexBuffer = new double[checked(positions.Length * 3)];
            for (int i = 0; i < positions.Length; i++)
            {
                int offset = i * 3;
                Vector3 v = positions[i];
                vertexBuffer[offset] = v.X;
                vertexBuffer[offset + 1] = v.Y;
                vertexBuffer[offset + 2] = v.Z;
            }

            GCHandle vertexHandle = GCHandle.Alloc(vertexBuffer, GCHandleType.Pinned);
            GCHandle indexHandle = GCHandle.Alloc(triangleIndices, GCHandleType.Pinned);

            try
            {
                NativeMesh nativeInput = new()
                {
                    VerticesPtr = vertexHandle.AddrOfPinnedObject(),
                    VerticesCount = (ulong)positions.Length,
                    TrianglesPtr = indexHandle.AddrOfPinnedObject(),
                    TrianglesCount = (ulong)(triangleIndices.Length / 3)
                };

                NativeMeshArray nativeArray = Run(
                    ref nativeInput,
                    config.Threshold,
                    config.MaxConvexHulls,
                    (int)config.PreprocessMode,
                    config.PreprocessResolution,
                    config.SampleResolution,
                    config.MctsNodes,
                    config.MctsIterations,
                    config.MctsMaxDepth,
                    config.EnablePca,
                    config.EnableMerge,
                    config.EnableDecimation,
                    config.MaxConvexHullVertices,
                    config.EnableExtrusion,
                    config.ExtrusionMargin,
                    (int)config.ApproximationMode,
                    config.Seed);

                try
                {
                    return ExtractMeshes(nativeArray);
                }
                finally
                {
                    FreeMeshArray(nativeArray);
                }
            }
            finally
            {
                if (vertexHandle.IsAllocated)
                    vertexHandle.Free();
                if (indexHandle.IsAllocated)
                    indexHandle.Free();
            }
        }

        public static void SetLogLevel(CoACDLogLevel level)
            => SetLogLevelNative(level switch
            {
                CoACDLogLevel.Off => "off",
                CoACDLogLevel.Debug => "debug",
                CoACDLogLevel.Info => "info",
                CoACDLogLevel.Warning => "warning",
                CoACDLogLevel.Error => "error",
                CoACDLogLevel.Critical => "critical",
                _ => "info"
            });

        private static IReadOnlyList<ConvexHullMesh> ExtractMeshes(NativeMeshArray nativeArray)
        {
            if (nativeArray.MeshesPtr == IntPtr.Zero || nativeArray.MeshesCount == 0)
                return [];

            int meshCount = checked((int)nativeArray.MeshesCount);
            var meshes = new List<ConvexHullMesh>(meshCount);
            int meshSize = Marshal.SizeOf<NativeMesh>();

            for (int i = 0; i < meshCount; i++)
            {
                IntPtr meshPtr = IntPtr.Add(nativeArray.MeshesPtr, i * meshSize);
                NativeMesh mesh = Marshal.PtrToStructure<NativeMesh>(meshPtr)!;
                int vertexCount = checked((int)mesh.VerticesCount);
                if (vertexCount == 0 || mesh.VerticesPtr == IntPtr.Zero)
                    continue;

                double[] vertexData = new double[vertexCount * 3];
                Marshal.Copy(mesh.VerticesPtr, vertexData, 0, vertexData.Length);
                Vector3[] vertices = new Vector3[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                {
                    int offset = v * 3;
                    vertices[v] = new Vector3(
                        (float)vertexData[offset],
                        (float)vertexData[offset + 1],
                        (float)vertexData[offset + 2]);
                }

                int triangleCount = checked((int)mesh.TrianglesCount);
                int[] indices = triangleCount > 0 ? new int[triangleCount * 3] : Array.Empty<int>();
                if (triangleCount > 0 && mesh.TrianglesPtr != IntPtr.Zero)
                    Marshal.Copy(mesh.TrianglesPtr, indices, 0, indices.Length);

                meshes.Add(new ConvexHullMesh(vertices, indices));
            }

            return meshes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMesh
        {
            public IntPtr VerticesPtr;
            public ulong VerticesCount;
            public IntPtr TrianglesPtr;
            public ulong TrianglesCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMeshArray
        {
            public IntPtr MeshesPtr;
            public ulong MeshesCount;
        }

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "CoACD_run")]
        private static extern NativeMeshArray Run(
            ref NativeMesh mesh,
            double threshold,
            int maxConvexHull,
            int preprocessMode,
            int prepResolution,
            int sampleResolution,
            int mctsNodes,
            int mctsIterations,
            int mctsMaxDepth,
            [MarshalAs(UnmanagedType.I1)] bool pca,
            [MarshalAs(UnmanagedType.I1)] bool merge,
            [MarshalAs(UnmanagedType.I1)] bool decimate,
            int maxChVertex,
            [MarshalAs(UnmanagedType.I1)] bool extrude,
            double extrudeMargin,
            int approximationMode,
            uint seed);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "CoACD_freeMeshArray")]
        private static extern void FreeMeshArray(NativeMeshArray array);

        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "CoACD_setLogLevel")]
        private static extern void SetLogLevelNative(string level);

        public sealed record ConvexHullMesh(Vector3[] Vertices, int[] Indices);

        public enum CoACDPreprocessMode
        {
            Auto = 0,
            On = 1,
            Off = 2
        }

        public enum CoACDApproximationMode
        {
            ConvexHull = 0,
            Box = 1
        }

        public enum CoACDLogLevel
        {
            Off,
            Debug,
            Info,
            Warning,
            Error,
            Critical
        }

        public sealed record CoACDParameters
        {
            public static CoACDParameters Default => new();

            public double Threshold { get; init; } = 0.05;
            public int MaxConvexHulls { get; init; } = -1;
            public CoACDPreprocessMode PreprocessMode { get; init; } = CoACDPreprocessMode.Auto;
            public int PreprocessResolution { get; init; } = 50;
            public int SampleResolution { get; init; } = 2000;
            public int MctsNodes { get; init; } = 20;
            public int MctsIterations { get; init; } = 150;
            public int MctsMaxDepth { get; init; } = 3;
            public bool EnablePca { get; init; } = false;
            public bool EnableMerge { get; init; } = true;
            public bool EnableDecimation { get; init; } = false;
            public int MaxConvexHullVertices { get; init; } = 256;
            public bool EnableExtrusion { get; init; } = false;
            public double ExtrusionMargin { get; init; } = 0.01;
            public CoACDApproximationMode ApproximationMode { get; init; } = CoACDApproximationMode.ConvexHull;
            public uint Seed { get; init; } = 0;
        }
    }
}
