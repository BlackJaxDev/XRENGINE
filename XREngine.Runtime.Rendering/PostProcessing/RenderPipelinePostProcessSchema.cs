using System.Collections.ObjectModel;

namespace XREngine.Rendering.PostProcessing;

public sealed class RenderPipelinePostProcessSchema(
    IReadOnlyDictionary<string, PostProcessStageDescriptor> stages,
    IReadOnlyList<PostProcessCategoryDescriptor> categories)
{
    public static RenderPipelinePostProcessSchema Empty { get; } = new(
        new ReadOnlyDictionary<string, PostProcessStageDescriptor>(new Dictionary<string, PostProcessStageDescriptor>(StringComparer.Ordinal)),
        []);

    public IReadOnlyDictionary<string, PostProcessStageDescriptor> StagesByKey { get; } =
        stages ?? new ReadOnlyDictionary<string, PostProcessStageDescriptor>(new Dictionary<string, PostProcessStageDescriptor>(StringComparer.Ordinal));

    public IReadOnlyList<PostProcessCategoryDescriptor> Categories { get; } = categories ?? [];

    public bool TryGetStage(string key, out PostProcessStageDescriptor? descriptor)
        => StagesByKey.TryGetValue(key, out descriptor);

    public bool IsEmpty => StagesByKey.Count == 0;
}
