using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Mutable accumulator used while describing passes. Converts to an immutable list once the pipeline is done.
/// </summary>
public sealed class RenderPassMetadataCollection
{
    private readonly Dictionary<int, RenderPassMetadata> _passes = new();
    private readonly RenderPassMetadataRevisionSource _revisionSource = new();
    private int _nextDeclarationOrder;

    /// <summary>
    /// Gets or sets the command that is describing passes now. Command containers set this
    /// while they walk their commands so each pass records the commands that own it.
    /// </summary>
    internal ViewportRenderCommand? CurrentCommand { get; set; }

    public RenderPassBuilder ForPass(int passIndex, string? name = null, ERenderGraphPassStage stage = ERenderGraphPassStage.Graphics)
    {
        if (!_passes.TryGetValue(passIndex, out RenderPassMetadata? metadata))
        {
            metadata = new RenderPassMetadata(
                passIndex,
                name ?? $"Pass{passIndex}",
                stage,
                _nextDeclarationOrder++,
                _revisionSource);
            _passes.Add(passIndex, metadata);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(name))
                metadata.UpdateName(name!);
            metadata.UpdateStage(stage);
        }

        EnsureDefaultDescriptorSchemas(metadata);
        if (CurrentCommand is { } command)
            metadata.AddContributingCommand(command);

        return new RenderPassBuilder(metadata);
    }

    /// <summary>
    /// Resolves a previously described pass by its stable render-graph name.
    /// </summary>
    public bool TryGetPassIndex(string name, out int passIndex)
    {
        foreach (RenderPassMetadata pass in _passes.Values)
        {
            if (!string.Equals(pass.Name, name, StringComparison.Ordinal))
                continue;

            passIndex = pass.PassIndex;
            return true;
        }

        passIndex = default;
        return false;
    }

    /// <summary>
    /// Adds an explicit dependency to a pass that a command already described. The method does not create a pass and
    /// does not change the pass stage, so data from an asset cannot add a phantom pass.
    /// </summary>
    /// <returns>True when <paramref name="passIndex"/> exists and the dependency is not a self edge.</returns>
    public bool TryAddDependency(int passIndex, int dependsOnPassIndex)
    {
        if (passIndex == dependsOnPassIndex || !_passes.TryGetValue(passIndex, out RenderPassMetadata? metadata))
            return false;

        metadata.AddDependency(dependsOnPassIndex);
        return true;
    }

    private static void EnsureDefaultDescriptorSchemas(RenderPassMetadata metadata)
    {
        metadata.AddDescriptorSchema(RenderGraphDescriptorSchemaCatalog.EngineGlobals.Name);

        if (metadata.Stage == ERenderGraphPassStage.Graphics)
            metadata.AddDescriptorSchema(RenderGraphDescriptorSchemaCatalog.MaterialResources.Name);
    }

    public IReadOnlyCollection<RenderPassMetadata> Build()
        => new RenderPassMetadataSnapshot(_passes.Values.OrderBy(p => p.PassIndex).ToArray(), _revisionSource);
}
