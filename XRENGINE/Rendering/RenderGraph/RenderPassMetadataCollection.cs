using System.Collections.ObjectModel;

namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Mutable accumulator used while describing passes. Converts to an immutable list once the pipeline is done.
/// </summary>
public sealed class RenderPassMetadataCollection
{
    private readonly Dictionary<int, RenderPassMetadata> _passes = new();

    public RenderPassBuilder ForPass(int passIndex, string? name = null, ERenderGraphPassStage stage = ERenderGraphPassStage.Graphics)
    {
        if (!_passes.TryGetValue(passIndex, out RenderPassMetadata? metadata))
        {
            metadata = new RenderPassMetadata(passIndex, name ?? $"Pass{passIndex}", stage);
            _passes.Add(passIndex, metadata);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(name))
                metadata.UpdateName(name!);
            metadata.UpdateStage(stage);
        }

        EnsureDefaultDescriptorSchemas(metadata);

        return new RenderPassBuilder(metadata);
    }

    private static void EnsureDefaultDescriptorSchemas(RenderPassMetadata metadata)
    {
        metadata.AddDescriptorSchema(RenderGraphDescriptorSchemaCatalog.EngineGlobals.Name);

        if (metadata.Stage == ERenderGraphPassStage.Graphics)
            metadata.AddDescriptorSchema(RenderGraphDescriptorSchemaCatalog.MaterialResources.Name);
    }

    public IReadOnlyCollection<RenderPassMetadata> Build()
        => new ReadOnlyCollection<RenderPassMetadata>(_passes.Values.OrderBy(p => p.PassIndex).ToList());
}
