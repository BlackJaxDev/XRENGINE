using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene.Prefabs;

namespace XREngine.RenderBench;

/// <summary>Runs explicit, repeatable runtime measurements without an editor window.</summary>
internal static partial class RuntimeDataLayoutScenario
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly int[] Populations = [1, 8, 32];

    internal static int Run(RenderBenchOptions options)
    {
        RuntimeDataLayoutReport report = new();
        try
        {
            using IDisposable services = RuntimeRenderingBootstrap.InstallEngineHostServices(new RuntimeApplicationProfile(
                "RuntimeMeasurements", RuntimeAdapterProfile.All, AllowsWindows: false, AllowsVr: false, RegisterRendererBackends: false));
            RuntimeHelpers.RunModuleConstructor(typeof(MonkeyBallVR.MonkeyBallWorldAsset).Module.ModuleHandle);
            using RenderBenchWorkSchedulerScope scheduler = RenderBenchWorkSchedulerScope.EnsureInstalled();
            RuntimeMeasurementManifest? manifest = null;
            string manifestRoot = string.Empty;
            if (options.RuntimeManifest is { } manifestPath)
            {
                string fullPath = Path.GetFullPath(manifestPath);
                manifestRoot = Path.GetDirectoryName(fullPath)!;
                byte[] bytes = File.ReadAllBytes(fullPath);
                report.ManifestSha256 = Convert.ToHexString(SHA256.HashData(bytes));
                manifest = JsonSerializer.Deserialize<RuntimeMeasurementManifest>(bytes, JsonOptions)
                    ?? throw new InvalidDataException("Runtime manifest is empty.");
                if (manifest.Version != 1)
                    throw new InvalidDataException($"Unsupported runtime manifest version {manifest.Version}.");
            }

            if (options.RuntimeLane is "all" or "assets")
            {
                MeasureAssets(options, manifest?.MonkeyBall ?? throw new InvalidDataException("Missing MonkeyBall fixture."), manifestRoot, 1, report);
                MeasureAssets(options, manifest?.Avatar ?? throw new InvalidDataException("Missing avatar fixture."), manifestRoot, 20, report);
            }
            if (options.RuntimeLane is "all" or "networking")
                MeasureNetworking(options, report);
            if (options.RuntimeLane is "all" or "transforms")
                MeasureTransforms(options, manifest?.Avatar ?? throw new InvalidDataException("Missing avatar fixture."), manifestRoot, report);
        }
        catch (Exception exception)
        {
            report.Failure = exception.ToString();
            Console.Error.WriteLine(exception.Message);
        }
        finally
        {
            PublishedArchiveRegistry.CloseAll();
            string reportPath = Path.Combine(options.OutputDirectory, "runtime-data-layout.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine(reportPath);
        }
        return report.Failure is null ? 0 : 1;
    }

    private static XRAsset Load(RuntimeMeasurementAsset fixture, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixture.Identity);
        bool hasSource = !string.IsNullOrWhiteSpace(fixture.Source);
        bool hasArchive = !string.IsNullOrWhiteSpace(fixture.Archive);
        if (hasSource == hasArchive)
            throw new InvalidDataException($"Fixture '{fixture.Identity}' requires exactly one source or archive.");
        if (hasSource)
            return Engine.Assets.Load<XRPrefabSource>(Path.GetFullPath(fixture.Source!, root), bypassJobThread: true)
                ?? throw new InvalidDataException($"Imported avatar '{fixture.Identity}' could not be loaded.");
        ArgumentException.ThrowIfNullOrWhiteSpace(fixture.Entry);
        PublishedArchiveHandle archive = PublishedArchiveRegistry.GetOrOpen(Path.GetFullPath(fixture.Archive!, root));
        using CookedPayloadLease payload = archive.ReadAsset(fixture.Entry);
        return PublishedCookedAssetReader.LoadAsset(payload.Span) as XRAsset
            ?? throw new InvalidDataException($"Fixture '{fixture.Identity}' did not contain a runtime asset.");
    }

    private static string HashFixture(RuntimeMeasurementAsset fixture, string root)
    {
        string path = Path.GetFullPath(fixture.Source ?? fixture.Archive ?? throw new InvalidDataException("Missing fixture path."), root);
        using FileStream file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static void Unload(XRAsset asset)
    {
        // A benchmark fixture exclusively owns its imported prefab and embedded assets.
        // Destroy the template graph too; destroying only the asset leaves global scene-object roots alive.
        if (asset is XRPrefabSource prefab)
            prefab.RootNode?.Destroy(now: true);
        for (int i = asset.EmbeddedAssets.Count - 1; i >= 0; i--)
        {
            XRAsset embedded = asset.EmbeddedAssets[i];
            if (!ReferenceEquals(asset, embedded) && ReferenceEquals(embedded.SourceAsset, asset))
                embedded.Destroy(now: true);
        }
        asset.Destroy(now: true);
        XRObjectBase.ProcessPendingDestructions();
    }

    private static (long Bytes, double Milliseconds) Measure(Action action, int warmup, int iterations)
    {
        for (int i = 0; i < warmup; i++)
            action();
        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
            action();
        long stopped = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        return (allocated, Stopwatch.GetElapsedTime(started, stopped).TotalMilliseconds);
    }

    private static double Percentile(double[] samples, double percentile)
    {
        Array.Sort(samples);
        return samples[Math.Clamp((int)Math.Ceiling(samples.Length * percentile) - 1, 0, samples.Length - 1)];
    }
}
