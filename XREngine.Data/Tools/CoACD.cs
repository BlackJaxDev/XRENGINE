using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Data.Tools
{
    public static class CoACD
    {
        private const string MaxConcurrentRunsEnvironmentVariable = XREngineEnvironmentVariables.CoacdMaxConcurrentRuns;
        private static readonly int s_maxConcurrentRuns = ResolveMaxConcurrentRuns();
        private static readonly SemaphoreSlim s_nativeRunGate = new(s_maxConcurrentRuns, s_maxConcurrentRuns);
        private static ICoAcdBackend? s_backend;

        public static void InstallBackend(ICoAcdBackend backend)
            => Volatile.Write(ref s_backend, backend ?? throw new ArgumentNullException(nameof(backend)));

        public static int MaxConcurrentRuns => s_maxConcurrentRuns;

        public static async Task<IReadOnlyList<ConvexHullMesh>?> CalculateAsync(
            Vector3[] positions,
            int[] triangleIndices,
            CoACDParameters? parameters = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(positions);
            ArgumentNullException.ThrowIfNull(triangleIndices);

            await s_nativeRunGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Factory.StartNew(
                    () => Calculate(positions, triangleIndices, parameters),
                    cancellationToken,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default).ConfigureAwait(false);
            }
            finally
            {
                s_nativeRunGate.Release();
            }
        }

        private static int ResolveMaxConcurrentRuns()
        {
            string? rawValue = Environment.GetEnvironmentVariable(MaxConcurrentRunsEnvironmentVariable);
            if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int configuredValue))
                return 1;

            return Math.Clamp(configuredValue, 1, Math.Max(1, Environment.ProcessorCount));
        }

        public static IReadOnlyList<ConvexHullMesh>? Calculate(
            Vector3[] positions,
            int[] triangleIndices,
            CoACDParameters? parameters = null)
        {
            ArgumentNullException.ThrowIfNull(positions);
            ArgumentNullException.ThrowIfNull(triangleIndices);
            return RequireBackend().Calculate(positions, triangleIndices, parameters);
        }

        public static void SetLogLevel(CoACDLogLevel level)
            => RequireBackend().SetLogLevel(level);

        private static ICoAcdBackend RequireBackend()
            => Volatile.Read(ref s_backend) ?? throw new InvalidOperationException(
                "CoACD authoring backend is not installed. Register it in the desktop authoring host before generating convex hulls.");
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
