using XREngine.Execution;
using XREngine.Rendering;

namespace XREngine.UnitTests.SoftwareVulkan;

/// <summary>Checks explicit capability installation, reset, and stale-scope disposal.</summary>
internal static class PresentationlessCompositionCheck
{
    public static void Run()
    {
        IDisposable original = RuntimeRenderingHostServices.InstallPresentationless();
        try
        {
            if (RuntimeRenderingHostServices.Presentation.IsOpenXRActive ||
                RuntimeRenderingHostServices.Presentation.IsOpenXrRuntimeRequested ||
                RuntimeRenderingHostServices.Presentation.IsInVR ||
                !ReferenceEquals(RuntimeRenderingHostServices.Work.RenderWork, RuntimeWorkScheduler.Scheduler!.Render))
                throw new InvalidOperationException("Presentationless composition did not preserve inactive XR and the existing scheduler.");
            RequireUnavailable(() => _ = RuntimeRenderingHostServices.Assets);
            RequireUnavailable(() => _ = RuntimeRenderingHostServices.Factories);
            RequireUnavailable(() => _ = RuntimeRenderingHostServices.Scheduling);
            RequireUnavailable(() => RuntimeRenderingHostServices.InstallPresentationless());
            RequireUnavailable(() => RuntimeRenderingHostServices.Install(RuntimeRenderingHostServices.Current));
            RuntimeRenderingHostServices.Reset();
            RequireUnavailable(() => _ = RuntimeRenderingHostServices.Presentation);
            RequireUnavailable(() => _ = RuntimeRenderingHostServices.Work);
            using IDisposable replacement = RuntimeRenderingHostServices.InstallPresentationless();
            original.Dispose();
            _ = RuntimeRenderingHostServices.Presentation;
            _ = RuntimeRenderingHostServices.Work;
        }
        finally
        {
            original.Dispose();
        }
        RequireUnavailable(() => _ = RuntimeRenderingHostServices.Presentation);
        RequireUnavailable(() => _ = RuntimeRenderingHostServices.Work);
    }

    private static void RequireUnavailable(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("An unavailable or conflicting host capability was accepted.");
    }
}
