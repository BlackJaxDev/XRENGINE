using System.Globalization;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Caches the plan snapshots and the rows that the Execution view draws. The backend snapshot comes from the renderer.
/// The offline snapshot comes from the neutral compiler and is the fallback when the renderer has no plan. Rows
/// rebuild only when a snapshot, the structure, or a filter changes.
/// </summary>
public sealed class RenderPipelineWorkbenchPlanView
{
    private readonly List<RenderPipelinePlanRow> _planRows = new(64);
    private readonly List<RenderPipelinePlanBarrierRow> _barrierRows = new(128);
    private readonly List<int> _visibleBarriers = new(128);
    private readonly List<RenderPipelinePlanLifetimeRow> _lifetimeRows = new(64);
    private readonly List<RenderPipelineWorkbenchInspectorItem> _reorderItems = new(32);
    private readonly List<RenderPipelinePlanCompareRow> _compareRows = new(64);
    private readonly List<RenderPipelineWorkbenchInspectorItem> _unmappedItems = new(8);
    private IReadOnlyCollection<RenderPassMetadata>? _offlinePasses;
    private int _offlinePassBuild = -1;
    private RenderGraphPlanSnapshot? _rowSnapshot;
    private RenderGraphPlanSnapshot? _compareBackend;
    private RenderGraphPlanSnapshot? _compareOffline;
    private int _structureBuild = -1;
    private string _barrierSearch = string.Empty;
    private string _barrierSearchLower = string.Empty;
    private ERenderPipelineBarrierKindFilter _barrierKinds = ERenderPipelineBarrierKindFilter.All;
    private int _barrierPass = -1;
    private string? _barrierResource;
    private bool _visibleDirty = true;

    /// <summary>Gets the snapshot that the renderer published, or null.</summary>
    public RenderGraphPlanSnapshot? Backend { get; private set; }

    /// <summary>Gets the snapshot of the neutral compiler.</summary>
    public RenderGraphPlanSnapshot? Offline { get; private set; }

    /// <summary>Gets the snapshot that the view shows: the backend snapshot when it exists, otherwise the offline snapshot.</summary>
    public RenderGraphPlanSnapshot? Active => Backend ?? Offline;

    /// <summary>Gets the one-line summary of the active snapshot.</summary>
    public string SummaryText { get; private set; } = string.Empty;

    /// <summary>Gets the short source label for the target bar badge.</summary>
    public string BadgeText { get; private set; } = "Plan: none";

    /// <summary>Gets the rows of the Plan tab.</summary>
    public IReadOnlyList<RenderPipelinePlanRow> PlanRows => _planRows;

    /// <summary>Gets every barrier row.</summary>
    public IReadOnlyList<RenderPipelinePlanBarrierRow> BarrierRows => _barrierRows;

    /// <summary>Gets the indices of the barrier rows that pass the filters.</summary>
    public IReadOnlyList<int> VisibleBarriers => _visibleBarriers;

    /// <summary>Gets the bars of the Lifetimes tab.</summary>
    public IReadOnlyList<RenderPipelinePlanLifetimeRow> LifetimeRows => _lifetimeRows;

    /// <summary>Gets the highest plan position of the Lifetimes tab.</summary>
    public int LifetimeSpan { get; private set; }

    /// <summary>Gets the lines of the Reorders tab.</summary>
    public List<RenderPipelineWorkbenchInspectorItem> ReorderItems => _reorderItems;

    /// <summary>Gets the rows of the Compare tab in authored order.</summary>
    public IReadOnlyList<RenderPipelinePlanCompareRow> CompareRows => _compareRows;

    /// <summary>Gets the commands that do GPU work but declare no pass, as linked lines.</summary>
    public List<RenderPipelineWorkbenchInspectorItem> UnmappedItems => _unmappedItems;

    /// <summary>Gets the warning text for the order violations, or an empty string when there are none.</summary>
    public string ViolationText { get; private set; } = string.Empty;

    /// <summary>Gets how many order violations the active snapshot has.</summary>
    public int ViolationCount => Active?.OrderViolations.Count ?? 0;

    /// <summary>Gets how many times the rows were rebuilt.</summary>
    public int RowBuildCount { get; private set; }

    /// <summary>Refreshes the snapshots and the rows.</summary>
    /// <param name="model">The Workbench model.</param>
    /// <param name="source">The plan source of the renderer, or null.</param>
    public void Update(RenderPipelineWorkbenchModel model, IRenderGraphPlanDiagnosticsSource? source)
    {
        IReadOnlyCollection<RenderPassMetadata> passes = model.Passes;
        if (!ReferenceEquals(passes, _offlinePasses) || _offlinePassBuild != model.PassGraphBuildCount)
        {
            _offlinePasses = passes;
            _offlinePassBuild = model.PassGraphBuildCount;
            Offline = RenderGraphOfflinePlanner.Plan(passes, model.CommandGeneration);
        }

        // Frozen mode keeps the last backend plan, so the view does not change while the scene changes.
        if (!model.IsFrozen)
            Backend = model.Instance is null ? null : source?.GetPlanSnapshot(model.Instance);

        RenderGraphPlanSnapshot? active = Active;
        if (!ReferenceEquals(active, _rowSnapshot))
        {
            _rowSnapshot = active;
            RebuildRows(active);
            _visibleDirty = true;
        }

        if (!ReferenceEquals(Backend, _compareBackend) || !ReferenceEquals(Offline, _compareOffline))
        {
            _compareBackend = Backend;
            _compareOffline = Offline;
            RebuildCompare(passes);
        }

        if (_structureBuild != model.StructureBuildCount)
        {
            _structureBuild = model.StructureBuildCount;
            RebuildUnmapped(model.Structure);
        }
    }

    /// <summary>Updates the barrier filters and rebuilds the visible list when a filter changed.</summary>
    /// <param name="search">The search text.</param>
    /// <param name="kinds">The barrier kinds to show.</param>
    /// <param name="passIndex">The pass to show, or -1 for every pass.</param>
    /// <param name="resourceName">The resource to show, or null for every resource.</param>
    public void UpdateBarrierFilter(string search, ERenderPipelineBarrierKindFilter kinds, int passIndex, string? resourceName)
    {
        if (!ReferenceEquals(search, _barrierSearch))
        {
            _barrierSearch = search;
            _barrierSearchLower = search.Trim().ToLowerInvariant();
            _visibleDirty = true;
        }

        if (kinds != _barrierKinds || passIndex != _barrierPass || !string.Equals(resourceName, _barrierResource, StringComparison.Ordinal))
        {
            _barrierKinds = kinds;
            _barrierPass = passIndex;
            _barrierResource = resourceName;
            _visibleDirty = true;
        }

        if (!_visibleDirty)
            return;

        _visibleDirty = false;
        _visibleBarriers.Clear();
        for (int i = 0; i < _barrierRows.Count; i++)
        {
            RenderPipelinePlanBarrierRow row = _barrierRows[i];
            if ((row.Kind & _barrierKinds) == 0)
                continue;
            if (_barrierPass >= 0 && row.Barrier.PassIndex != _barrierPass)
                continue;
            if (_barrierResource is not null && !RenderPipelineWorkbenchLinks.NameMatches(row.Barrier.ResourceName, _barrierResource))
                continue;
            if (_barrierSearchLower.Length > 0 && !row.SearchText.Contains(_barrierSearchLower, StringComparison.Ordinal))
                continue;

            _visibleBarriers.Add(i);
        }
    }

    private void RebuildRows(RenderGraphPlanSnapshot? snapshot)
    {
        RowBuildCount++;
        _planRows.Clear();
        _barrierRows.Clear();
        _lifetimeRows.Clear();
        _reorderItems.Clear();
        LifetimeSpan = 0;
        ViolationText = snapshot is null || snapshot.OrderViolations.Count == 0
            ? string.Empty
            : string.Concat(
                snapshot.OrderViolations.Count.ToString(CultureInfo.InvariantCulture),
                " dependency edges run in the wrong order. Open the Reorders tab.");
        if (snapshot is null)
        {
            SummaryText = "No plan.";
            BadgeText = "Plan: none";
            return;
        }

        BuildSummary(snapshot);
        BuildPlanRows(snapshot);
        BuildBarrierRows(snapshot);
        BuildLifetimeRows(snapshot);
        BuildReorderItems(snapshot);
    }

    private void BuildSummary(RenderGraphPlanSnapshot snapshot)
    {
        int executed = 0;
        for (int i = 0; i < snapshot.Passes.Count; i++)
            if (snapshot.Passes[i].Executed)
                executed++;

        string source = snapshot.Origin switch
        {
            EPlanSnapshotOrigin.Vulkan => string.Concat("Vulkan plan, revision ", snapshot.Revision.ToString(CultureInfo.InvariantCulture)),
            EPlanSnapshotOrigin.OpenGL => "OpenGL recorded order",
            _ => "Offline plan",
        };
        BadgeText = snapshot.Origin switch
        {
            EPlanSnapshotOrigin.Vulkan => string.Concat("Plan: Vulkan r", snapshot.Revision.ToString(CultureInfo.InvariantCulture)),
            EPlanSnapshotOrigin.OpenGL => "Plan: OpenGL recorded",
            _ => "Plan: offline",
        };

        string skipped = executed == snapshot.Passes.Count
            ? string.Empty
            : string.Concat(", ", (snapshot.Passes.Count - executed).ToString(CultureInfo.InvariantCulture), " did not run");
        SummaryText = string.Concat(
            source, ". ",
            executed.ToString(CultureInfo.InvariantCulture), " passes", skipped,
            ", ", snapshot.Batches.Count.ToString(CultureInfo.InvariantCulture), " batches",
            snapshot.Submissions.Count > 0 ? string.Concat(", ", snapshot.Submissions.Count.ToString(CultureInfo.InvariantCulture), " submissions") : string.Empty,
            snapshot.Barriers.Count > 0 ? string.Concat(", ", snapshot.Barriers.Count.ToString(CultureInfo.InvariantCulture), " barriers") : string.Empty,
            snapshot.AliasGroups.Count > 0 ? string.Concat(", ", snapshot.AliasGroups.Count.ToString(CultureInfo.InvariantCulture), " alias groups") : string.Empty,
            ".");
    }

    private void BuildPlanRows(RenderGraphPlanSnapshot snapshot)
    {
        HashSet<int> reordered = new(snapshot.Reorders.Count);
        for (int i = 0; i < snapshot.Reorders.Count; i++)
            reordered.Add(snapshot.Reorders[i].PassIndex);

        Dictionary<int, int> barrierCount = [];
        for (int i = 0; i < snapshot.Barriers.Count; i++)
            barrierCount[snapshot.Barriers[i].PassIndex] = barrierCount.GetValueOrDefault(snapshot.Barriers[i].PassIndex) + 1;

        int currentSubmission = int.MinValue;
        HashSet<int> listed = new(snapshot.Passes.Count);
        for (int b = 0; b < snapshot.Batches.Count; b++)
        {
            PlanBatchSnapshot batch = snapshot.Batches[b];
            if (snapshot.Submissions.Count > 0 && batch.SubmissionIndex != currentSubmission)
            {
                currentSubmission = batch.SubmissionIndex;
                _planRows.Add(new RenderPipelinePlanRow(
                    ERenderPipelinePlanRowKind.Submission,
                    string.Concat("Submission ", currentSubmission.ToString(CultureInfo.InvariantCulture)),
                    FindSubmissionLabel(snapshot, currentSubmission),
                    -1,
                    true,
                    false));
            }

            _planRows.Add(new RenderPipelinePlanRow(
                ERenderPipelinePlanRowKind.Batch,
                string.Concat("Batch ", batch.BatchIndex.ToString(CultureInfo.InvariantCulture), "  ", batch.Stage.ToString()),
                batch.AttachmentSignature.Length == 0 ? "no attachments" : batch.AttachmentSignature,
                -1,
                true,
                false));

            for (int p = 0; p < batch.PassIndices.Count; p++)
            {
                int passIndex = batch.PassIndices[p];
                if (snapshot.TryGetPass(passIndex, out PlanPassSnapshot? pass) && pass is not null)
                {
                    listed.Add(passIndex);
                    _planRows.Add(BuildPassRow(pass, reordered.Contains(passIndex), barrierCount.GetValueOrDefault(passIndex)));
                }
            }
        }

        // Passes outside every batch did not run, or the source has no batches for them.
        for (int i = 0; i < snapshot.Passes.Count; i++)
        {
            PlanPassSnapshot pass = snapshot.Passes[i];
            if (!listed.Contains(pass.PassIndex))
                _planRows.Add(BuildPassRow(pass, false, barrierCount.GetValueOrDefault(pass.PassIndex)));
        }
    }

    private static RenderPipelinePlanRow BuildPassRow(PlanPassSnapshot pass, bool reordered, int barriers)
    {
        string text = string.Concat(
            pass.Executed ? pass.ExecutionOrder.ToString(CultureInfo.InvariantCulture) : "-", ".  ",
            pass.Name, "  #", pass.PassIndex.ToString(CultureInfo.InvariantCulture));
        string detail = string.Concat(
            pass.Stage.ToString(),
            "  authored ", pass.AuthoredOrder.ToString(CultureInfo.InvariantCulture),
            barriers > 0 ? string.Concat("  barriers ", barriers.ToString(CultureInfo.InvariantCulture)) : string.Empty,
            reordered ? "  moved" : string.Empty,
            pass.Executed ? string.Empty : "  did not run");
        return new RenderPipelinePlanRow(ERenderPipelinePlanRowKind.Pass, text, detail, pass.PassIndex, pass.Executed, reordered);
    }

    private static string FindSubmissionLabel(RenderGraphPlanSnapshot snapshot, int submissionIndex)
    {
        for (int i = 0; i < snapshot.Submissions.Count; i++)
            if (snapshot.Submissions[i].SubmissionIndex == submissionIndex)
                return snapshot.Submissions[i].Label;

        return string.Empty;
    }

    private void BuildBarrierRows(RenderGraphPlanSnapshot snapshot)
    {
        for (int i = 0; i < snapshot.Barriers.Count; i++)
        {
            PlanBarrierSnapshot barrier = snapshot.Barriers[i];
            string passText = snapshot.TryGetPass(barrier.PassIndex, out PlanPassSnapshot? pass) && pass is not null
                ? string.Concat(pass.Name, "  #", barrier.PassIndex.ToString(CultureInfo.InvariantCulture))
                : string.Concat("#", barrier.PassIndex.ToString(CultureInfo.InvariantCulture));
            string layout = barrier.OldLayout.Length == 0 && barrier.NewLayout.Length == 0
                ? string.Empty
                : string.Concat(barrier.OldLayout, " > ", barrier.NewLayout);
            string stages = string.Concat(barrier.SourceStages, " > ", barrier.DestinationStages);
            string access = string.Concat(barrier.SourceAccess, " > ", barrier.DestinationAccess);
            ERenderPipelineBarrierKindFilter kind = barrier.Kind switch
            {
                "Image" => ERenderPipelineBarrierKindFilter.Image,
                "Buffer" => ERenderPipelineBarrierKindFilter.Buffer,
                _ => ERenderPipelineBarrierKindFilter.Swapchain,
            };
            string search = string.Concat(passText, " ", barrier.ResourceName, " ", layout, " ", stages, " ", access).ToLowerInvariant();
            _barrierRows.Add(new RenderPipelinePlanBarrierRow(barrier, kind, passText, layout, stages, access, search));
        }
    }

    private void BuildLifetimeRows(RenderGraphPlanSnapshot snapshot)
    {
        int span = 0;
        for (int i = 0; i < snapshot.Passes.Count; i++)
            if (snapshot.Passes[i].Executed)
                span++;
        LifetimeSpan = Math.Max(span, 1);

        PlanResourceLifetimeSnapshot[] sorted = [.. snapshot.Lifetimes];
        Array.Sort(sorted, static (a, b) =>
        {
            int order = a.FirstOrder.CompareTo(b.FirstOrder);
            return order != 0 ? order : string.CompareOrdinal(a.ResourceName, b.ResourceName);
        });

        for (int i = 0; i < sorted.Length; i++)
        {
            PlanResourceLifetimeSnapshot lifetime = sorted[i];
            string tooltip = string.Concat(
                lifetime.ResourceName,
                "\nPositions ", lifetime.FirstOrder.ToString(CultureInfo.InvariantCulture), " to ", lifetime.LastOrder.ToString(CultureInfo.InvariantCulture),
                lifetime.AliasGroup >= 0 ? string.Concat("\nAlias group ", lifetime.AliasGroup.ToString(CultureInfo.InvariantCulture)) : "\nNo shared memory",
                lifetime.IsTransient ? "\nTransient" : string.Empty);
            _lifetimeRows.Add(new RenderPipelinePlanLifetimeRow(lifetime.ResourceName, lifetime.FirstOrder, lifetime.LastOrder, lifetime.AliasGroup, tooltip));
        }
    }

    private void BuildReorderItems(RenderGraphPlanSnapshot snapshot)
    {
        Header("Order violations");
        if (snapshot.OrderViolations.Count == 0)
            Add("None. Every pass runs after the passes that it waits for.", 1);
        for (int i = 0; i < snapshot.OrderViolations.Count; i++)
        {
            PlanOrderViolation violation = snapshot.OrderViolations[i];
            _reorderItems.Add(new RenderPipelineWorkbenchInspectorItem(
                violation.Message,
                RenderPipelineWorkbenchSelectionEntry.ForPass(violation.ConsumerPassIndex),
                1));
        }

        Header("Moved passes");
        if (snapshot.Reorders.Count == 0)
            Add("None. The plan keeps the authored order.", 1);
        for (int i = 0; i < snapshot.Reorders.Count; i++)
        {
            PlanReorderEntry entry = snapshot.Reorders[i];
            string name = snapshot.TryGetPass(entry.PassIndex, out PlanPassSnapshot? pass) && pass is not null ? pass.Name : string.Empty;
            _reorderItems.Add(new RenderPipelineWorkbenchInspectorItem(
                string.Concat(
                    name, "  #", entry.PassIndex.ToString(CultureInfo.InvariantCulture),
                    "  ", entry.AuthoredPosition.ToString(CultureInfo.InvariantCulture),
                    " > ", entry.ExecutionPosition.ToString(CultureInfo.InvariantCulture),
                    "  ", entry.Cause),
                RenderPipelineWorkbenchSelectionEntry.ForPass(entry.PassIndex),
                1));
        }

        Header("Notes");
        for (int i = 0; i < snapshot.Notes.Count; i++)
            Add(snapshot.Notes[i], 1);
    }

    private void RebuildCompare(IReadOnlyCollection<RenderPassMetadata> passes)
    {
        _compareRows.Clear();
        RenderPassMetadata[] authored = [.. passes];
        Array.Sort(authored, static (a, b) =>
        {
            int order = a.DeclarationOrder.CompareTo(b.DeclarationOrder);
            return order != 0 ? order : a.PassIndex.CompareTo(b.PassIndex);
        });

        RenderGraphPlanSnapshot? backend = Backend;
        for (int i = 0; i < authored.Length; i++)
        {
            RenderPassMetadata pass = authored[i];
            bool executed = false;
            int backendOrder = -1;
            if (backend is not null && backend.TryGetPass(pass.PassIndex, out PlanPassSnapshot? planned) && planned is not null && planned.Executed)
            {
                executed = true;
                backendOrder = planned.ExecutionOrder;
            }

            int neutralOrder = Offline is not null && Offline.TryGetPass(pass.PassIndex, out PlanPassSnapshot? neutral) && neutral is not null
                ? neutral.ExecutionOrder
                : -1;
            int reference = backend is null ? neutralOrder : backendOrder;
            bool moved = reference >= 0 && reference != i;
            string backendText = backend is null
                ? (neutralOrder >= 0 ? neutralOrder.ToString(CultureInfo.InvariantCulture) : "-")
                : (executed ? backendOrder.ToString(CultureInfo.InvariantCulture) : "-");
            string delta = moved ? (reference - i).ToString("+0;-0", CultureInfo.InvariantCulture) : string.Empty;
            _compareRows.Add(new RenderPipelinePlanCompareRow(
                pass.PassIndex,
                pass.Name,
                i.ToString(CultureInfo.InvariantCulture),
                backendText,
                delta,
                moved,
                backend is null || executed));
        }
    }

    private void RebuildUnmapped(RenderPipelineStructureSnapshot structure)
    {
        _unmappedItems.Clear();
        for (int i = 0; i < structure.Commands.Count; i++)
        {
            RenderPipelineStructureCommand entry = structure.Commands[i];
            if (entry.PassIndices.Count > 0 || !RenderCommandInfo.Get(entry.Command).DoesGpuWork)
                continue;

            _unmappedItems.Add(new RenderPipelineWorkbenchInspectorItem(
                string.Concat(RenderPipelineWorkbenchText.GetCommandLabel(entry.Command), "  (", entry.ProfilingPath, ")"),
                RenderPipelineWorkbenchSelectionEntry.ForCommand(RenderPipelineWorkbenchNodeIds.Get(entry.Command)),
                1));
        }
    }

    private void Add(string text, int indent)
        => _reorderItems.Add(new RenderPipelineWorkbenchInspectorItem(text, RenderPipelineWorkbenchSelectionEntry.None, indent));

    private void Header(string text)
        => _reorderItems.Add(new RenderPipelineWorkbenchInspectorItem(text, RenderPipelineWorkbenchSelectionEntry.None, 0, IsHeader: true));
}
