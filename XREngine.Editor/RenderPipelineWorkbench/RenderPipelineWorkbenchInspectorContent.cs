using System.Globalization;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;
using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Builds the text of the Workbench Inspector for the current selection. The content rebuilds only when the
/// selection or the model changes, so the Inspector draws cached strings.
/// </summary>
public sealed class RenderPipelineWorkbenchInspectorContent
{
    private readonly List<string> _stageScratch = [];
    private RenderPipelineWorkbenchSelectionEntry _selection;
    private RenderPipelineWorkbenchModel? _model;
    private int _structureBuild = -1;
    private int _passBuild = -1;
    private int _resourceBuild = -1;

    /// <summary>Gets the title line.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Gets the lines below the title.</summary>
    public List<RenderPipelineWorkbenchInspectorItem> Items { get; } = [];

    /// <summary>Gets the selected command, or null.</summary>
    public ViewportRenderCommand? Command { get; private set; }

    /// <summary>Gets the selected container, or null.</summary>
    public ViewportRenderCommandContainer? Container { get; private set; }

    /// <summary>Gets the selected pass, or null.</summary>
    public RenderPassMetadata? Pass { get; private set; }

    /// <summary>Gets the ImGui id prefix of the property editor.</summary>
    public string PropertyEditorId { get; private set; } = string.Empty;

    /// <summary>Gets how many times the content was rebuilt.</summary>
    public int BuildCount { get; private set; }

    /// <summary>Rebuilds the content when the selection or the model changed.</summary>
    public void Update(RenderPipelineWorkbenchModel model, RenderPipelineWorkbenchSelectionEntry selection)
    {
        if (ReferenceEquals(_model, model) &&
            _selection == selection &&
            _structureBuild == model.StructureBuildCount &&
            _passBuild == model.PassGraphBuildCount &&
            _resourceBuild == model.ResourceBuildCount)
        {
            return;
        }

        _model = model;
        _selection = selection;
        _structureBuild = model.StructureBuildCount;
        _passBuild = model.PassGraphBuildCount;
        _resourceBuild = model.ResourceBuildCount;
        BuildCount++;

        Items.Clear();
        Title = string.Empty;
        Command = null;
        Container = null;
        Pass = null;
        PropertyEditorId = string.Empty;

        switch (selection.Kind)
        {
            case ERenderPipelineWorkbenchSelectionKind.Command:
            case ERenderPipelineWorkbenchSelectionKind.Container:
                BuildNode(model, selection.NodeId);
                break;
            case ERenderPipelineWorkbenchSelectionKind.Pass:
                BuildPass(model, selection.PassIndex);
                break;
            case ERenderPipelineWorkbenchSelectionKind.Resource when selection.Name is not null:
                BuildResource(model, selection.Name);
                break;
            case ERenderPipelineWorkbenchSelectionKind.Stage when selection.Name is not null:
                BuildStage(model, selection.Name);
                break;
        }
    }

    private void BuildNode(RenderPipelineWorkbenchModel model, int nodeId)
    {
        if (!model.TryGetOutlineRow(nodeId, out RenderPipelineOutlineRow row))
        {
            Title = "The selected object is no longer in the pipeline.";
            return;
        }

        PropertyEditorId = string.Concat("WorkbenchNode", nodeId.ToString(CultureInfo.InvariantCulture));
        if (row.Kind == ERenderPipelineOutlineRowKind.Container || row.Command is null)
        {
            Container = row.Container;
            Title = row.Label;
            Add(string.Concat("Commands: ", row.Container.Count.ToString(CultureInfo.InvariantCulture)));
            Add(string.Concat("Branch resources: ", row.Container.BranchResources.ToString()));
            return;
        }

        ViewportRenderCommand command = row.Command;
        Command = command;
        RenderCommandInfo info = RenderCommandInfo.Get(command);
        Title = row.Label;
        Add(string.Concat(command.GetType().Name, "  (", info.Category.ToString(), ")"));
        if (info.Description.Length > 0)
            Add(info.Description);
        if (!info.HasAttribute)
            Add("This command type has no RenderCommandInfo attribute.");
        Add(string.Concat("Position: ", (row.IndexInContainer + 1).ToString(CultureInfo.InvariantCulture), " of ", row.Container.Count.ToString(CultureInfo.InvariantCulture)));

        if (model.TryGetStructureCommand(nodeId, out RenderPipelineStructureCommand entry))
        {
            Add(string.Concat("Profiling path: ", entry.ProfilingPath));
            Header("Passes");
            if (entry.PassIndices.Count == 0)
                Add(info.DoesGpuWork ? "None. This command does GPU work but declares no pass." : "None.");
            for (int i = 0; i < entry.PassIndices.Count; i++)
                AddPassLink(model, entry.PassIndices[i], 1);
        }

        _stageScratch.Clear();
        RenderCommandInfo.CollectConsumedStages(command, _stageScratch);
        if (_stageScratch.Count > 0)
        {
            Header("Consumed stages");
            for (int i = 0; i < _stageScratch.Count; i++)
                Items.Add(new RenderPipelineWorkbenchInspectorItem(_stageScratch[i], RenderPipelineWorkbenchSelectionEntry.ForStage(_stageScratch[i]), 1));
        }
    }

    private void BuildPass(RenderPipelineWorkbenchModel model, int passIndex)
    {
        if (!model.PassGraph.NodeIndexByPass.TryGetValue(passIndex, out int nodeIndex))
        {
            Title = "The selected pass is no longer in the pipeline.";
            return;
        }

        RenderPipelinePassGraphNode node = model.PassGraph.Nodes[nodeIndex];
        RenderPassMetadata pass = node.Metadata;
        Pass = pass;
        Title = pass.Name;
        Add(node.Subtitle);
        Add(string.Concat("Declaration order: ", pass.DeclarationOrder.ToString(CultureInfo.InvariantCulture), "  Topological order: ", node.OrderText));
        Add(string.Concat("Requires pipeline ready: ", pass.RequiresPipelineReady ? "yes" : "no"));
        Add(string.Concat("Secondary cache policy: ", pass.SecondaryCachePolicy.ToString()));
        Add(node.AttachmentSummary);

        Header("Commands");
        if (pass.ContributingCommands.Count == 0)
            Add("Declared by the pipeline.", 1);
        for (int i = 0; i < pass.ContributingCommands.Count; i++)
        {
            ViewportRenderCommand command = pass.ContributingCommands[i];
            Items.Add(new RenderPipelineWorkbenchInspectorItem(
                RenderPipelineWorkbenchText.GetCommandLabel(command),
                RenderPipelineWorkbenchSelectionEntry.ForCommand(RenderPipelineWorkbenchNodeIds.Get(command)),
                1));
        }

        Header("Resources");
        foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
        {
            Items.Add(new RenderPipelineWorkbenchInspectorItem(usage.ResourceName, RenderPipelineWorkbenchSelectionEntry.ForResource(usage.ResourceName), 1));
            string detail = string.Concat(
                usage.ResourceType.ToString(), "  ", usage.Access.ToString(),
                usage.IsAttachment ? string.Concat("  load ", usage.LoadOp.ToString(), "  store ", usage.StoreOp.ToString()) : string.Empty,
                usage.LogicalVersion >= 0 ? string.Concat("  v", usage.LogicalVersion.ToString(CultureInfo.InvariantCulture)) : string.Empty,
                usage.IsImported ? "  imported" : string.Empty,
                usage.SubresourceRange.IsWholeResource ? string.Empty : string.Concat("  range ", usage.SubresourceRange.ToString()));
            Add(detail, 2);
        }

        Header("Depends on");
        if (pass.ExplicitDependencies.Count == 0 && node.IncomingEdges.Count == 0)
            Add("Nothing.", 1);
        for (int i = 0; i < node.IncomingEdges.Count; i++)
        {
            RenderPipelinePassGraphEdge edge = model.PassGraph.Edges[node.IncomingEdges[i]];
            RenderPipelinePassGraphNode producer = model.PassGraph.Nodes[edge.FromNode];
            Items.Add(new RenderPipelineWorkbenchInspectorItem(
                string.Concat(producer.Title, "  (", edge.Kind.ToString(), edge.ResourceName.Length > 0 ? string.Concat(": ", edge.ResourceName) : string.Empty, ")"),
                RenderPipelineWorkbenchSelectionEntry.ForPass(producer.PassIndex),
                1));
        }

        Header("Descriptor schemas");
        for (int i = 0; i < pass.DescriptorSchemas.Count; i++)
            Add(pass.DescriptorSchemas[i], 1);
    }

    private void BuildResource(RenderPipelineWorkbenchModel model, string resourceName)
    {
        Title = resourceName;
        if (model.Resources.TryGet(resourceName, out RenderPipelineResourceEntry? entry) && entry is not null)
        {
            Header("Resource");
            Add(string.Concat("Kind: ", entry.Kind?.ToString() ?? "undeclared"));
            if (entry.Width > 0)
                Add(string.Concat("Size: ", entry.Width.ToString(CultureInfo.InvariantCulture), "x", entry.Height.ToString(CultureInfo.InvariantCulture)));
            if (entry.FormatText.Length > 0)
                Add(string.Concat("Format: ", entry.FormatText));
            if (entry.EstimatedBytes > 0)
                Add(string.Concat("Memory: ", RenderPipelineWorkbenchText.FormatBytes((long)entry.EstimatedBytes)));
            Add(string.Concat("Flags: ", entry.Flags.ToString()));
        }

        Header("Used by passes");
        foreach (RenderPassMetadata pass in model.Passes)
        {
            ERenderPipelineResourceAccess access = RenderPipelineWorkbenchLinks.GetResourceAccess(pass, resourceName, model.Resources);
            if (access == ERenderPipelineResourceAccess.None)
                continue;

            Items.Add(new RenderPipelineWorkbenchInspectorItem(
                string.Concat(pass.Name, "  (", access.ToString(), ")"),
                RenderPipelineWorkbenchSelectionEntry.ForPass(pass.PassIndex),
                1));
        }
    }

    private void BuildStage(RenderPipelineWorkbenchModel model, string stageKey)
    {
        Title = stageKey;
        Header("Consumers");
        RenderPipelineStructureSnapshot structure = model.Structure;
        bool any = false;
        for (int i = 0; i < structure.Commands.Count; i++)
        {
            RenderPipelineStructureCommand entry = structure.Commands[i];
            _stageScratch.Clear();
            RenderCommandInfo.CollectConsumedStages(entry.Command, _stageScratch);
            if (!_stageScratch.Contains(stageKey, StringComparer.Ordinal))
                continue;

            any = true;
            Items.Add(new RenderPipelineWorkbenchInspectorItem(
                RenderPipelineWorkbenchText.GetCommandLabel(entry.Command),
                RenderPipelineWorkbenchSelectionEntry.ForCommand(RenderPipelineWorkbenchNodeIds.Get(entry.Command)),
                1));
        }

        if (!any)
            Add("No command reads this stage.", 1);
    }

    private void AddPassLink(RenderPipelineWorkbenchModel model, int passIndex, int indent)
    {
        string title = model.PassGraph.NodeIndexByPass.TryGetValue(passIndex, out int nodeIndex)
            ? string.Concat(model.PassGraph.Nodes[nodeIndex].Title, "  #", passIndex.ToString(CultureInfo.InvariantCulture))
            : string.Concat("#", passIndex.ToString(CultureInfo.InvariantCulture));
        Items.Add(new RenderPipelineWorkbenchInspectorItem(title, RenderPipelineWorkbenchSelectionEntry.ForPass(passIndex), indent));
    }

    private void Add(string text, int indent = 0)
        => Items.Add(new RenderPipelineWorkbenchInspectorItem(text, RenderPipelineWorkbenchSelectionEntry.None, indent));

    private void Header(string text)
        => Items.Add(new RenderPipelineWorkbenchInspectorItem(text, RenderPipelineWorkbenchSelectionEntry.None, 0, IsHeader: true));
}
