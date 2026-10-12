using System.Diagnostics;
using System.Runtime.CompilerServices;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private readonly ConditionalWeakTable<RenderResourceGeneration, WebGpuResourceGenerationPreparation> _engineGenerationPreparations = new();

    internal override ERenderResourceGenerationPreparationStatus PrepareRenderResourceGeneration(
        XRRenderPipelineInstance pipeline, RenderResourceGeneration generation, XRViewport? viewport,
        out IRenderResourceGenerationTransaction? transaction, out string? failureReason)
    {
        transaction = null;
        failureReason = null;
        if (_engineGenerationPreparations.TryGetValue(generation, out WebGpuResourceGenerationPreparation? preparation) &&
            preparation.Revision != generation.Registry.InstanceRevision)
        {
            _engineGenerationPreparations.Remove(generation);
            preparation = null;
        }
        if (preparation is null)
        {
            preparation = new(generation.Registry);
            _engineGenerationPreparations.Add(generation, preparation);
        }
        long started = Stopwatch.GetTimestamp();
        int completed = 0;
        while (preparation.Cursor < preparation.Resources.Length)
        {
            try
            {
                GenericRenderObject resource = preparation.Resources[preparation.Cursor];
                if (resource is XRQuadFrameBuffer quad && resource.Name is { } name &&
                    generation.Layout.TryGet(name, out RenderPipelineResourceSpec? spec) && spec is QuadMaterialSpec)
                {
                    // Material-only helpers share the framebuffer registry, but render into
                    // a separately bound target. Prepare their retained draw owner instead.
                    quad.PrepareForInitialRendering();
                }
                else
                    GetOrCreateAPIRenderObject(resource, generateNow: true)!.Generate();
            }
            catch (RenderResourcePreparationPendingException)
            {
                failureReason = "Physical generation dependencies are awaiting engine resource acceptance.";
                return ERenderResourceGenerationPreparationStatus.Pending;
            }
            preparation.Cursor++;
            if (++completed >= 4 || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 2)
                break;
        }
        return preparation.Cursor == preparation.Resources.Length
            ? ERenderResourceGenerationPreparationStatus.Ready : ERenderResourceGenerationPreparationStatus.Pending;
    }
}
