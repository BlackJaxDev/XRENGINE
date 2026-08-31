using System.Threading;

namespace XREngine.Rendering.Occlusion;

public static partial class OcclusionTelemetry
{
    private static long _hiZSourcePixels, _hiZTestCapacity, _hiZBuildCpuTicks, _hiZTestCpuTicks;
    private static int _hiZBuildDispatches, _hiZTestDispatches;

    public static long LastFrameHiZSourcePixels { get; private set; }
    public static long LastFrameHiZTestCapacity { get; private set; }
    public static int LastFrameHiZBuildDispatches { get; private set; }
    public static int LastFrameHiZTestDispatches { get; private set; }
    /// <summary>CPU command-recording cost, not elapsed GPU execution time.</summary>
    public static double LastFrameHiZBuildCpuMs { get; private set; }
    /// <summary>CPU command-recording cost, not elapsed GPU execution time.</summary>
    public static double LastFrameHiZTestCpuMs { get; private set; }
    /// <summary>Delayed GPU elapsed time for the Hi-Z pyramid build; never CPU recording cost.</summary>
    public static double HiZBuildGpuMs { get; private set; }
    /// <summary>Delayed GPU elapsed time for Hi-Z tests; never CPU recording cost.</summary>
    public static double HiZTestGpuMs { get; private set; }
    public static ulong HiZBuildGpuSourceFrame { get; private set; }
    public static ulong HiZTestGpuSourceFrame { get; private set; }
    public static ulong HiZBuildGpuAgeFrames { get; private set; }
    public static ulong HiZTestGpuAgeFrames { get; private set; }
    public static ulong HiZBuildGpuSequence { get; private set; }
    public static ulong HiZTestGpuSequence { get; private set; }
    /// <summary>Live ring diagnostic; elapsed/source fields retain the last completed measurement.</summary>
    public static EOcclusionGpuElapsedAvailability HiZBuildGpuAvailability { get; private set; }
    /// <summary>Live ring diagnostic; elapsed/source fields retain the last completed measurement.</summary>
    public static EOcclusionGpuElapsedAvailability HiZTestGpuAvailability { get; private set; }

    public static void RecordHiZBuild(uint width, uint height, double cpuMilliseconds)
    {
        Interlocked.Increment(ref _hiZBuildDispatches);
        Interlocked.Add(ref _hiZSourcePixels, (long)width * height);
        Interlocked.Add(ref _hiZBuildCpuTicks, (long)(cpuMilliseconds * 10000.0));
    }

    public static void RecordHiZTest(uint capacity, double cpuMilliseconds)
    {
        Interlocked.Increment(ref _hiZTestDispatches);
        Interlocked.Add(ref _hiZTestCapacity, capacity);
        Interlocked.Add(ref _hiZTestCpuTicks, (long)(cpuMilliseconds * 10000.0));
    }

    private static void SnapshotHiZWork()
    {
        OcclusionGpuElapsedSample buildTiming;
        OcclusionGpuElapsedSample testTiming;
        if (OcclusionGpuElapsedTiming.Instance.IsRequested)
        {
            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            buildTiming = OcclusionGpuElapsedTiming.Instance.GetSample(EOcclusionGpuElapsedStage.Build, frameId) with
            {
                Availability = OcclusionGpuElapsedTiming.Instance.GetDiagnosticAvailability(EOcclusionGpuElapsedStage.Build),
            };
            testTiming = OcclusionGpuElapsedTiming.Instance.GetSample(EOcclusionGpuElapsedStage.Test, frameId) with
            {
                Availability = OcclusionGpuElapsedTiming.Instance.GetDiagnosticAvailability(EOcclusionGpuElapsedStage.Test),
            };
        }
        else
        {
            buildTiming = new(EOcclusionGpuElapsedAvailability.Disabled, 0u, 0u, 0u, 0u);
            testTiming = buildTiming;
        }
        HiZBuildGpuMs = buildTiming.ElapsedNanoseconds / 1_000_000.0;
        HiZTestGpuMs = testTiming.ElapsedNanoseconds / 1_000_000.0;
        HiZBuildGpuSourceFrame = buildTiming.SourceFrameId;
        HiZTestGpuSourceFrame = testTiming.SourceFrameId;
        HiZBuildGpuAgeFrames = buildTiming.AgeFrames;
        HiZTestGpuAgeFrames = testTiming.AgeFrames;
        HiZBuildGpuSequence = buildTiming.Sequence;
        HiZTestGpuSequence = testTiming.Sequence;
        HiZBuildGpuAvailability = buildTiming.Availability;
        HiZTestGpuAvailability = testTiming.Availability;
        LastFrameHiZBuildDispatches = Interlocked.Exchange(ref _hiZBuildDispatches, 0);
        LastFrameHiZTestDispatches = Interlocked.Exchange(ref _hiZTestDispatches, 0);
        LastFrameHiZSourcePixels = Interlocked.Exchange(ref _hiZSourcePixels, 0);
        LastFrameHiZTestCapacity = Interlocked.Exchange(ref _hiZTestCapacity, 0);
        LastFrameHiZBuildCpuMs = Interlocked.Exchange(ref _hiZBuildCpuTicks, 0) / 10000.0;
        LastFrameHiZTestCpuMs = Interlocked.Exchange(ref _hiZTestCpuTicks, 0) / 10000.0;
    }
}
