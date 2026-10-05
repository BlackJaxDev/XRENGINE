using System.Diagnostics;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine.RenderBench;

internal static partial class RuntimeDataLayoutScenario
{
    private static void MeasureAssets(RenderBenchOptions options, RuntimeMeasurementAsset fixture, string root, int cycles, RuntimeDataLayoutReport report)
    {
        string identityHash = HashFixture(fixture, root);
        for (int repeat = 0; repeat < options.ScenarioRepeats; repeat++)
        {
            // A repeat starts with a fresh archive; churn cycles retain the same mapping.
            PublishedArchiveRegistry.CloseAll();
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                GCMemoryInfo before = GC.GetGCMemoryInfo();
                long opensBefore = PublishedArchiveRegistry.TotalOpenCount;
                int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                long bytes = GC.GetTotalAllocatedBytes(precise: true);
                long started = Stopwatch.GetTimestamp();
                XRAsset loaded = Load(fixture, root);
                long stopped = Stopwatch.GetTimestamp();
                long allocated = GC.GetTotalAllocatedBytes(precise: true) - bytes;
                int gen0Delta = GC.CollectionCount(0) - gen0, gen1Delta = GC.CollectionCount(1) - gen1, gen2Delta = GC.CollectionCount(2) - gen2;
                long unloadStarted = Stopwatch.GetTimestamp();
                Unload(loaded);
                double unloadTime = Stopwatch.GetElapsedTime(unloadStarted).TotalMilliseconds;
                GCMemoryInfo after = GC.GetGCMemoryInfo();
                CookedPayloadPoolStatistics pool = CookedPayloadBufferPool.Statistics;
                report.Assets.Add(new(fixture.Identity, identityHash,
                    fixture.Source is null ? "published-archive" : "authoring-import", repeat, cycle,
                    allocated, gen0Delta, gen1Delta, gen2Delta,
                    Stopwatch.GetElapsedTime(started, stopped).TotalMilliseconds, unloadTime,
                    PublishedArchiveRegistry.TotalOpenCount - opensBefore,
                    LohSize(before), LohSize(after), after.HeapSizeBytes, pool.RetainedBytes, pool.ActiveNativeBytes));
            }
        }
    }

    private static long LohSize(GCMemoryInfo info)
        => info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0;
}
