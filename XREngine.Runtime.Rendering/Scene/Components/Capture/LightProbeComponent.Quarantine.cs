using System.Collections.Concurrent;
using XREngine.Rendering;

namespace XREngine.Components.Capture.Lights;

public partial class LightProbeComponent
{
    // One terminal closure per renderer. Native wrappers remain owned by the
    // renderer and are destroyed only after its existing shutdown idle proof.
    // There is no CPU post-idle callback, so retain this exceptional closure for
    // process lifetime rather than invent completion or retry without a bound.
    private static readonly ConcurrentDictionary<AbstractRenderer, LightProbeComponent> s_unfencedIblProducers = new();
    private LightProbeIblOutputGeneration? _quarantinedIblOutput;

    private bool IsIblProducerQuarantined
        => _quarantinedIblOutput is not null ||
           AbstractRenderer.Current is { } renderer && s_unfencedIblProducers.ContainsKey(renderer);

    private void QuarantineIblProducer(AbstractRenderer renderer, LightProbeIblOutputGeneration output)
    {
        _quarantinedIblOutput = output;
        s_unfencedIblProducers.TryAdd(renderer, this);
        CancelPendingIblGenerationRetry();
        _realtimeCaptureTimer.Cancel();
        Debug.LogWarning("[LightProbe] An immediate GPU producer failed without a retention fence. " +
            "Probe production is disabled for this renderer; its resources remain retained until safe renderer teardown.");
    }
}
