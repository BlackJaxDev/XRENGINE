using System.Runtime.CompilerServices;
using XREngine.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Caches the selector text of each pipeline instance. The text rebuilds only when the camera, the viewport,
/// the active resource generation, or the view setup of the instance changes.
/// </summary>
public static class RenderPipelineWorkbenchInstanceLabels
{
    private sealed class Entry
    {
        public XRCamera? Camera;
        public XRViewport? Viewport;
        public RenderResourceGeneration? Generation;
        public RenderPipelineViewSetup Setup;
        public string Label = string.Empty;
    }

    private static readonly ConditionalWeakTable<XRRenderPipelineInstance, Entry> Entries = new();

    /// <summary>Returns the selector text of <paramref name="instance"/>.</summary>
    public static string Get(XRRenderPipelineInstance instance)
    {
        Entry entry = Entries.GetOrCreateValue(instance);
        XRCamera? camera = instance.LastSceneCamera;
        XRViewport? viewport = instance.LastWindowViewport;
        RenderResourceGeneration? generation = instance.ActiveGeneration;
        RenderPipelineViewSetup setup = instance.Pipeline is IRenderPipelineViewSetupSource source
            ? source.GetViewSetup(instance)
            : default;

        if (entry.Label.Length > 0 &&
            ReferenceEquals(entry.Camera, camera) &&
            ReferenceEquals(entry.Viewport, viewport) &&
            ReferenceEquals(entry.Generation, generation) &&
            entry.Setup == setup)
        {
            return entry.Label;
        }

        entry.Camera = camera;
        entry.Viewport = viewport;
        entry.Generation = generation;
        entry.Setup = setup;
        entry.Label = Build(instance, generation, setup);
        return entry.Label;
    }

    /// <summary>Returns the view setup text of <paramref name="instance"/>, or an empty string for single-view pipelines.</summary>
    public static string GetViewSetupName(XRRenderPipelineInstance instance)
        => instance.Pipeline is IRenderPipelineViewSetupSource source
            ? source.GetViewSetup(instance).Name ?? string.Empty
            : string.Empty;

    private static string Build(XRRenderPipelineInstance instance, RenderResourceGeneration? generation, RenderPipelineViewSetup setup)
    {
        string label = string.Concat("#", RenderPipelineWorkbenchText.FormatCount(instance.InstanceId), "  ", instance.DebugDescriptor);
        if (generation is not null)
        {
            ResourceGenerationKey key = generation.Key;
            label = string.Concat(
                label,
                "  ",
                RenderPipelineWorkbenchText.FormatCount((int)key.InternalWidth), "x", RenderPipelineWorkbenchText.FormatCount((int)key.InternalHeight),
                key.Stereo ? "  stereo" : string.Empty);
        }

        if (!string.IsNullOrEmpty(setup.Name))
            label = string.Concat(label, "  [", setup.Name, "]");

        return label;
    }
}
