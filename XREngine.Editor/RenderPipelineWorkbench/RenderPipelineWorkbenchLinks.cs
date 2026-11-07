using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;
using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Computes which commands and passes relate to the current selection, so each view can highlight them.
/// A command links to the passes it described; a pass links to its commands; a resource links to the passes
/// that use it and their commands; a stage links to the commands that read it.
/// The sets rebuild only when the selection or the model changes.
/// </summary>
public sealed class RenderPipelineWorkbenchLinks
{
    private readonly HashSet<int> _linkedNodes = [];
    private readonly HashSet<int> _linkedPasses = [];
    private readonly Dictionary<int, ERenderPipelineResourceAccess> _resourceAccessByNode = [];
    private readonly List<string> _stageScratch = [];
    private RenderPipelineWorkbenchSelectionEntry _selection;
    private int _structureBuild = -1;
    private int _resourceBuild = -1;
    private RenderPipelineWorkbenchModel? _model;

    /// <summary>Gets the node ids of linked commands.</summary>
    public IReadOnlySet<int> LinkedNodes => _linkedNodes;

    /// <summary>Gets the indices of linked passes.</summary>
    public IReadOnlySet<int> LinkedPasses => _linkedPasses;

    /// <summary>
    /// Gets how each linked command uses the selected resource. The map is empty unless a resource is selected.
    /// </summary>
    public IReadOnlyDictionary<int, ERenderPipelineResourceAccess> ResourceAccessByNode => _resourceAccessByNode;

    /// <summary>Gets how many times the sets were rebuilt.</summary>
    public int BuildCount { get; private set; }

    /// <summary>Rebuilds the sets when the selection or the model changed since the last call.</summary>
    public void Update(RenderPipelineWorkbenchModel model, RenderPipelineWorkbenchSelectionEntry selection)
    {
        if (ReferenceEquals(_model, model) &&
            _structureBuild == model.StructureBuildCount &&
            _resourceBuild == model.ResourceBuildCount &&
            _selection == selection)
        {
            return;
        }

        _model = model;
        _structureBuild = model.StructureBuildCount;
        _resourceBuild = model.ResourceBuildCount;
        _selection = selection;
        _linkedNodes.Clear();
        _linkedPasses.Clear();
        _resourceAccessByNode.Clear();
        BuildCount++;

        RenderPipelineStructureSnapshot structure = model.Structure;
        switch (selection.Kind)
        {
            case ERenderPipelineWorkbenchSelectionKind.Command:
                _linkedNodes.Add(selection.NodeId);
                if (model.TryGetStructureCommand(selection.NodeId, out RenderPipelineStructureCommand command))
                    AddPasses(command.PassIndices);
                break;
            case ERenderPipelineWorkbenchSelectionKind.Container:
                _linkedNodes.Add(selection.NodeId);
                break;
            case ERenderPipelineWorkbenchSelectionKind.Pass:
                _linkedPasses.Add(selection.PassIndex);
                AddPassCommands(model, selection.PassIndex);
                break;
            case ERenderPipelineWorkbenchSelectionKind.Resource when selection.Name is { } resource:
                foreach (RenderPassMetadata pass in model.Passes)
                {
                    ERenderPipelineResourceAccess access = GetResourceAccess(pass, resource, model.Resources);
                    if (access == ERenderPipelineResourceAccess.None)
                        continue;

                    _linkedPasses.Add(pass.PassIndex);
                    AddPassCommands(model, pass.PassIndex);
                    IReadOnlyList<ViewportRenderCommand> contributors = pass.ContributingCommands;
                    for (int i = 0; i < contributors.Count; i++)
                    {
                        int nodeId = RenderPipelineWorkbenchNodeIds.Get(contributors[i]);
                        _resourceAccessByNode[nodeId] = _resourceAccessByNode.GetValueOrDefault(nodeId) | access;
                    }
                }
                break;
            case ERenderPipelineWorkbenchSelectionKind.Stage when selection.Name is { } stageKey:
                for (int i = 0; i < structure.Commands.Count; i++)
                {
                    RenderPipelineStructureCommand entry = structure.Commands[i];
                    _stageScratch.Clear();
                    RenderCommandInfo.CollectConsumedStages(entry.Command, _stageScratch);
                    if (!_stageScratch.Contains(stageKey, StringComparer.Ordinal))
                        continue;

                    _linkedNodes.Add(RenderPipelineWorkbenchNodeIds.Get(entry.Command));
                    AddPasses(entry.PassIndices);
                }
                break;
        }
    }

    /// <summary>Returns whether <paramref name="pass"/> declares a use of <paramref name="resourceName"/>.</summary>
    public static bool UsesResource(RenderPassMetadata pass, string resourceName)
    {
        foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
        {
            if (NameMatches(usage.ResourceName, resourceName))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns how <paramref name="pass"/> reads and writes <paramref name="resourceName"/>. When
    /// <paramref name="resources"/> is given, a framebuffer attachment usage also counts for the attached texture.
    /// </summary>
    public static ERenderPipelineResourceAccess GetResourceAccess(
        RenderPassMetadata pass,
        string resourceName,
        RenderPipelineResourceSnapshot? resources = null)
    {
        ERenderPipelineResourceAccess access = ERenderPipelineResourceAccess.None;
        foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
        {
            if (!NameMatches(usage.ResourceName, resourceName) && !MapsToResource(resources, usage.ResourceName, resourceName))
                continue;

            access |= usage.Access switch
            {
                ERenderGraphAccess.Read => ERenderPipelineResourceAccess.Read,
                ERenderGraphAccess.Write => ERenderPipelineResourceAccess.Write,
                _ => ERenderPipelineResourceAccess.Read | ERenderPipelineResourceAccess.Write,
            };
        }

        return access;
    }

    /// <summary>
    /// Returns true when a pass usage name refers to the selected resource. The selection can hold a usage name, such
    /// as <c>fbo::Name::color</c>, or a logical name from the Resources view, such as <c>Name</c>.
    /// </summary>
    public static bool NameMatches(string usageName, string selectedName)
        => string.Equals(usageName, selectedName, StringComparison.OrdinalIgnoreCase) ||
           (RenderPipelineWorkbenchResourceNames.TryParse(usageName, out RenderPipelineWorkbenchResourceName parsed) &&
            string.Equals(parsed.BaseName, selectedName, StringComparison.OrdinalIgnoreCase));

    private static bool MapsToResource(RenderPipelineResourceSnapshot? resources, string usageName, string resourceName)
    {
        if (resources is null)
            return false;

        IReadOnlyList<RenderPipelineResourceEntry> entries = resources.GetForUsageName(usageName);
        for (int i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i].Name, resourceName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void AddPasses(IReadOnlyList<int> passIndices)
    {
        for (int i = 0; i < passIndices.Count; i++)
            _linkedPasses.Add(passIndices[i]);
    }

    private void AddPassCommands(RenderPipelineWorkbenchModel model, int passIndex)
    {
        if (!model.PassGraph.NodeIndexByPass.TryGetValue(passIndex, out int nodeIndex))
            return;

        IReadOnlyList<ViewportRenderCommand> contributors = model.PassGraph.Nodes[nodeIndex].Metadata.ContributingCommands;
        for (int i = 0; i < contributors.Count; i++)
            _linkedNodes.Add(RenderPipelineWorkbenchNodeIds.Get(contributors[i]));
    }
}
