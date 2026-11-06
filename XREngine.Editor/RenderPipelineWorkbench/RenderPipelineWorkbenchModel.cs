using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;
using XREngine.Rendering.Resources;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Caches everything the Workbench views draw for one pipeline instance. Each part rebuilds only when its key
/// changes: the outline and Flow layout on the command generation, the pass graph on the pass metadata and its
/// revision, and the structure map on either. A refresh with no key change does no work and allocates nothing.
/// </summary>
public sealed class RenderPipelineWorkbenchModel
{
    private readonly RenderPipelineTimelineLayoutBuilder _timelineBuilder = new();
    private readonly List<RenderCommandChildContainer> _childScratch = new(8);
    private readonly HashSet<ViewportRenderCommandContainer> _visited = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _searchTerms = new(StringComparer.Ordinal);
    private bool _hasInputs;
    private ViewportRenderCommandContainer? _root;
    private ulong _commandGeneration;
    private IReadOnlyCollection<RenderPassMetadata>? _passes;
    private int _passRevision;
    private RenderResourceGeneration? _generation;

    /// <summary>Gets the pipeline of the last refresh.</summary>
    public RenderPipeline? Pipeline { get; private set; }

    /// <summary>Gets the instance of the last refresh.</summary>
    public XRRenderPipelineInstance? Instance { get; private set; }

    /// <summary>
    /// Gets or sets whether the model ignores refreshes. Frozen mode keeps the last data while the scene changes.
    /// </summary>
    public bool IsFrozen { get; set; }

    /// <summary>Gets the command generation of the cached data.</summary>
    public ulong CommandGeneration => _commandGeneration;

    /// <summary>Gets the pass metadata of the cached data.</summary>
    public IReadOnlyCollection<RenderPassMetadata> Passes => _passes ?? Array.Empty<RenderPassMetadata>();

    /// <summary>Gets the active resource generation of the cached data, or null.</summary>
    public RenderResourceGeneration? Generation => _generation;

    /// <summary>Gets the command-to-pass map.</summary>
    public RenderPipelineStructureSnapshot Structure { get; private set; } = RenderPipelineStructureSnapshot.Empty;

    /// <summary>Gets the outline rows in depth-first order.</summary>
    public List<RenderPipelineOutlineRow> Outline { get; } = new(128);

    /// <summary>Gets the outline row index of each node id.</summary>
    public Dictionary<int, int> OutlineRowByNodeId { get; } = new(128);

    /// <summary>Gets the Flow view layout.</summary>
    public RenderPipelineTimelineLayout Timeline { get; } = new();

    /// <summary>Gets the Passes view layout.</summary>
    public RenderPipelinePassGraphLayout PassGraph { get; } = new();

    /// <summary>Gets how many times the structure map was rebuilt.</summary>
    public int StructureBuildCount { get; private set; }

    /// <summary>Gets how many times the outline was rebuilt.</summary>
    public int OutlineBuildCount { get; private set; }

    /// <summary>Gets how many times the Flow layout was rebuilt.</summary>
    public int TimelineBuildCount { get; private set; }

    /// <summary>Gets how many times the pass graph was rebuilt.</summary>
    public int PassGraphBuildCount { get; private set; }

    /// <summary>Gets how many times the active resource generation changed.</summary>
    public int GenerationChangeCount { get; private set; }

    /// <summary>
    /// Raised after any part rebuilds. Views that cache their own derived data listen to it.
    /// </summary>
    public event Action<RenderPipelineWorkbenchModel>? Rebuilt;

    /// <summary>Refreshes the model from a pipeline and an optional instance.</summary>
    /// <returns>True when any part rebuilt.</returns>
    public bool Refresh(RenderPipeline pipeline, XRRenderPipelineInstance? instance)
        => Refresh(RenderPipelineWorkbenchInputs.From(pipeline, instance));

    /// <summary>Refreshes the model from explicit inputs.</summary>
    /// <returns>True when any part rebuilt.</returns>
    public bool Refresh(in RenderPipelineWorkbenchInputs inputs)
    {
        if (IsFrozen && _hasInputs)
            return false;

        bool targetChanged = !_hasInputs ||
            !ReferenceEquals(Pipeline, inputs.Pipeline) ||
            !ReferenceEquals(Instance, inputs.Instance) ||
            !ReferenceEquals(_root, inputs.Root);
        bool commandsChanged = targetChanged || _commandGeneration != inputs.CommandGeneration;
        bool passesChanged = targetChanged || !ReferenceEquals(_passes, inputs.Passes) || _passRevision != inputs.PassRevision;
        bool generationChanged = targetChanged || !ReferenceEquals(_generation, inputs.Generation);

        if (!commandsChanged && !passesChanged && !generationChanged)
            return false;

        Pipeline = inputs.Pipeline;
        Instance = inputs.Instance;
        _root = inputs.Root;
        _commandGeneration = inputs.CommandGeneration;
        _passes = inputs.Passes;
        _passRevision = inputs.PassRevision;
        _generation = inputs.Generation;
        _hasInputs = true;

        if (commandsChanged || passesChanged)
        {
            Structure = inputs.Structure is { } prebuilt &&
                prebuilt.CommandGeneration == inputs.CommandGeneration &&
                ReferenceEquals(prebuilt.SourcePassMetadata, inputs.Passes)
                ? prebuilt
                : RenderPipelineStructureSnapshot.Build(inputs.CommandGeneration, inputs.Root, inputs.Passes);
            StructureBuildCount++;

            // Outline rows show pass counts and pass names, so they follow the structure map.
            RebuildOutline();
        }

        if (commandsChanged)
        {
            Timeline.Clear();
            if (inputs.Root is not null)
                _timelineBuilder.Build(inputs.Root, Timeline);
            TimelineBuildCount++;
        }

        if (passesChanged)
        {
            PassGraph.Build(inputs.Passes, Structure);
            PassGraphBuildCount++;
        }

        if (generationChanged)
            GenerationChangeCount++;

        Rebuilt?.Invoke(this);
        return true;
    }

    /// <summary>Forgets the cached data so the next refresh rebuilds every part.</summary>
    public void Invalidate()
        => _hasInputs = false;

    /// <summary>Finds the outline row of a node id.</summary>
    public bool TryGetOutlineRow(int nodeId, out RenderPipelineOutlineRow row)
    {
        if (OutlineRowByNodeId.TryGetValue(nodeId, out int index))
        {
            row = Outline[index];
            return true;
        }

        row = null!;
        return false;
    }

    /// <summary>Finds the structure entry of the command with node id <paramref name="nodeId"/>.</summary>
    public bool TryGetStructureCommand(int nodeId, out RenderPipelineStructureCommand entry)
    {
        if (TryGetOutlineRow(nodeId, out RenderPipelineOutlineRow row) && row.StructureIndex >= 0)
        {
            entry = Structure.Commands[row.StructureIndex];
            return true;
        }

        entry = null!;
        return false;
    }

    private void RebuildOutline()
    {
        Outline.Clear();
        OutlineRowByNodeId.Clear();
        _visited.Clear();
        _childScratch.Clear();
        OutlineBuildCount++;

        if (_root is null)
            return;

        AddContainerRows(_root, depth: 0);
    }

    private void AddContainerRows(ViewportRenderCommandContainer container, int depth)
    {
        if (!_visited.Add(container))
            return;

        for (int i = 0; i < container.Count; i++)
        {
            ViewportRenderCommand command = container[i];
            int rowIndex = Outline.Count;
            int structureIndex = Structure.TryGetCommand(command, out RenderPipelineStructureCommand entry) ? entry.Index : -1;
            RenderCommandInfo info = RenderCommandInfo.Get(command);
            int passCount = structureIndex >= 0 ? entry.PassIndices.Count : 0;

            RenderPipelineOutlineRow row = new()
            {
                RowIndex = rowIndex,
                NodeId = RenderPipelineWorkbenchNodeIds.Get(command),
                Kind = ERenderPipelineOutlineRowKind.Command,
                Depth = depth,
                Command = command,
                Container = container,
                IndexInContainer = i,
                StructureIndex = structureIndex,
                Category = info.Category,
                Label = RenderPipelineWorkbenchText.GetCommandLabel(command),
                PassCount = passCount,
                PassBadge = passCount > 0 ? RenderPipelineWorkbenchText.FormatCount(passCount) : string.Empty,
                HasGpuWorkWithoutPass = structureIndex >= 0 && entry.IsGpuWorkWithoutPass,
                IsUncategorized = !info.HasAttribute,
                SearchText = BuildSearchText(command, info, structureIndex >= 0 ? entry.PassIndices : null),
            };
            Outline.Add(row);
            OutlineRowByNodeId[row.NodeId] = rowIndex;

            int childStart = _childScratch.Count;
            command.CollectChildContainers(_childScratch);
            int childEnd = _childScratch.Count;
            for (int c = childStart; c < childEnd; c++)
            {
                RenderCommandChildContainer child = _childScratch[c];
                int headerIndex = Outline.Count;
                RenderPipelineOutlineRow header = new()
                {
                    RowIndex = headerIndex,
                    NodeId = RenderPipelineWorkbenchNodeIds.Get(child.Container),
                    Kind = ERenderPipelineOutlineRowKind.Container,
                    Depth = depth + 1,
                    Container = child.Container,
                    Category = ERenderCommandCategory.Flow,
                    Label = child.Name,
                    SearchText = child.Name.ToLowerInvariant(),
                };
                Outline.Add(header);
                OutlineRowByNodeId[header.NodeId] = headerIndex;
                AddContainerRows(child.Container, depth + 2);
                header.SubtreeEnd = Outline.Count;
            }

            _childScratch.RemoveRange(childStart, childEnd - childStart);
            row.SubtreeEnd = Outline.Count;
        }
    }

    private string BuildSearchText(ViewportRenderCommand command, RenderCommandInfo info, IReadOnlyList<int>? passIndices)
    {
        _searchTerms.Clear();
        _searchTerms.Add(command.GetType().Name);
        _searchTerms.Add(info.DisplayName);
        if (RenderPipelineWorkbenchText.GetCommandDetail(command) is { Length: > 0 } detail)
            _searchTerms.Add(detail);

        if (passIndices is not null && _passes is RenderPassMetadataSnapshot snapshot)
        {
            for (int i = 0; i < passIndices.Count; i++)
            {
                if (!snapshot.TryGetPass(passIndices[i], out RenderPassMetadata pass))
                    continue;

                _searchTerms.Add(pass.Name);
                foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
                    _searchTerms.Add(usage.ResourceName);
            }
        }

        return string.Join('\n', _searchTerms).ToLowerInvariant();
    }
}
