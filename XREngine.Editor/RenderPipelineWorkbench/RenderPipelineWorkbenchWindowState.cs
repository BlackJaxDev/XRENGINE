using XREngine.Rendering;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Holds the state of the Workbench window: the target, the cached model, the shared selection, and the view settings.
/// The pipeline and the instance are weak references so the window never keeps a destroyed pipeline alive.
/// </summary>
public sealed class RenderPipelineWorkbenchWindowState
{
    private WeakReference<RenderPipeline>? _pinnedPipeline;
    private WeakReference<XRRenderPipelineInstance>? _pinnedInstance;

    /// <summary>Gets the cached model of the current target.</summary>
    public RenderPipelineWorkbenchModel Model { get; } = new();

    /// <summary>Gets the selection that every view shares.</summary>
    public RenderPipelineWorkbenchSelection Selection { get; } = new();

    /// <summary>Gets the commands and passes that relate to the selection.</summary>
    public RenderPipelineWorkbenchLinks Links { get; } = new();

    /// <summary>Gets which outline rows executed in the newest traced frame of the instance.</summary>
    public RenderPipelineWorkbenchExecutionState Execution { get; } = new();

    /// <summary>Gets the rows and visible list of the Resources view.</summary>
    public RenderPipelineWorkbenchResourceTable ResourceTable { get; } = new();

    /// <summary>Gets the cached detail tab lines of the selected resource.</summary>
    public RenderPipelineWorkbenchResourceDetail ResourceDetail { get; } = new();

    /// <summary>Gets or sets the Resources view search text.</summary>
    public string ResourceSearch { get; set; } = string.Empty;

    /// <summary>Gets or sets the Resources view row filters.</summary>
    public ERenderPipelineResourceFilter ResourceFilter { get; set; }

    /// <summary>Gets or sets the Resources view grouping.</summary>
    public ERenderPipelineResourceGrouping ResourceGrouping { get; set; }

    /// <summary>Gets or sets the visible Resources view detail tab.</summary>
    public ERenderPipelineResourceDetailTab ResourceDetailTab { get; set; }

    /// <summary>Gets the GPU timings of the selected instance, mapped to commands and passes.</summary>
    public RenderPipelineWorkbenchTiming Timing { get; } = new();

    /// <summary>Gets or sets whether the Flow view colors command blocks by GPU time.</summary>
    public bool ShowTimingHeat { get; set; }

    /// <summary>Gets the schema tree and stage detail of the Settings view.</summary>
    public RenderPipelineWorkbenchSettingsContent Settings { get; } = new();

    /// <summary>Gets or sets whether the Settings view shows the pipeline asset settings instead of a stage.</summary>
    public bool SettingsShowPipeline { get; set; } = true;

    /// <summary>Gets the plan snapshots and rows of the Execution view.</summary>
    public RenderPipelineWorkbenchPlanView Plan { get; } = new();

    /// <summary>Gets or sets the visible Execution view tab.</summary>
    public ERenderPipelineExecutionTab ExecutionTab { get; set; }

    /// <summary>Gets or sets the Barriers tab search text.</summary>
    public string BarrierSearch { get; set; } = string.Empty;

    /// <summary>Gets or sets the barrier kinds that the Barriers tab shows.</summary>
    public ERenderPipelineBarrierKindFilter BarrierKinds { get; set; } = ERenderPipelineBarrierKindFilter.All;

    /// <summary>Gets or sets whether the Barriers tab shows only the barriers of the selected pass.</summary>
    public bool BarriersOfSelectedPassOnly { get; set; }

    /// <summary>Gets or sets whether the Barriers tab shows only the barriers of the selected resource.</summary>
    public bool BarriersOfSelectedResourceOnly { get; set; }

    /// <summary>Gets the validator report and the rows of the Validation view.</summary>
    public RenderPipelineWorkbenchValidation Validation { get; } = new();

    /// <summary>Gets the cached script text of the Script view.</summary>
    public RenderPipelineWorkbenchScript Script { get; } = new();

    /// <summary>Gets the edit buffer of the Script view for an open draft.</summary>
    public RenderPipelineScriptDraftEditor ScriptEditor { get; } = new();

    /// <summary>Gets the draft, palette, and validation state of pipeline authoring.</summary>
    public RenderPipelineWorkbenchAuthoring Authoring { get; } = new();

    /// <summary>Gets the cached Inspector text for the selection.</summary>
    public RenderPipelineWorkbenchInspectorContent Inspector { get; } = new();

    /// <summary>Gets or sets the visible central view.</summary>
    public ERenderPipelineWorkbenchView ActiveView { get; set; } = ERenderPipelineWorkbenchView.Flow;

    /// <summary>Gets or sets a view that the next draw selects, or null.</summary>
    public ERenderPipelineWorkbenchView? RequestedView { get; set; }

    /// <summary>Gets the Flow canvas pan and zoom.</summary>
    public RenderPipelineCanvasViewState FlowView { get; } = new();

    /// <summary>Gets the Passes canvas pan and zoom.</summary>
    public RenderPipelineCanvasViewState PassesView { get; } = new();

    /// <summary>Gets or sets whether the Outline panel shows.</summary>
    public bool ShowOutline { get; set; } = true;

    /// <summary>Gets or sets whether the Inspector panel shows.</summary>
    public bool ShowInspector { get; set; } = true;

    /// <summary>Gets or sets whether the preview strip shows.</summary>
    public bool ShowPreview { get; set; } = true;

    /// <summary>Gets or sets whether the preview flips textures vertically.</summary>
    public bool FlipPreview { get; set; } = true;

    /// <summary>Gets or sets the Outline search text.</summary>
    public string OutlineSearch { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the Outline shows only commands that do GPU work.</summary>
    public bool OutlineGpuWorkOnly { get; set; }

    /// <summary>Gets or sets whether the Outline shows only rows with problems.</summary>
    public bool OutlineProblemsOnly { get; set; }

    /// <summary>Gets or sets whether the Outline shows only commands that touch the selected resource.</summary>
    public bool OutlineTouchesSelectedResource { get; set; }

    /// <summary>Gets the node ids of collapsed Outline rows.</summary>
    public HashSet<int> CollapsedOutlineNodes { get; } = [];

    /// <summary>Gets or sets whether the Passes view dims passes that do not depend on the selected pass.</summary>
    public bool PassFocusMode { get; set; }

    /// <summary>Gets or sets whether the Passes view shows a table instead of the graph.</summary>
    public bool PassTableMode { get; set; }

    /// <summary>Gets the pinned pipeline, or null when the Workbench follows the active pipeline.</summary>
    public RenderPipeline? PinnedPipeline
        => _pinnedPipeline is not null && _pinnedPipeline.TryGetTarget(out RenderPipeline? pipeline) && !pipeline.IsDestroyed
            ? pipeline
            : null;

    /// <summary>Gets the pinned instance, or null.</summary>
    public XRRenderPipelineInstance? PinnedInstance
        => _pinnedInstance is not null && _pinnedInstance.TryGetTarget(out XRRenderPipelineInstance? instance)
            ? instance
            : null;

    /// <summary>Gets or sets the index of the last selected instance, used when the pinned instance is destroyed.</summary>
    public int LastInstanceIndex { get; set; }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<RenderPipeline, string> _scriptPaths = [];

    /// <summary>
    /// Pins a pipeline and an optional instance. The Workbench shows this target until the pipeline is destroyed
    /// or the user unpins it.
    /// </summary>
    public void Pin(RenderPipeline pipeline, XRRenderPipelineInstance? instance)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (!ReferenceEquals(PinnedPipeline, pipeline))
            Selection.Clear();

        _pinnedPipeline = new WeakReference<RenderPipeline>(pipeline);
        _pinnedInstance = instance is null ? null : new WeakReference<XRRenderPipelineInstance>(instance);
    }

    /// <summary>Pins an instance of the pinned pipeline.</summary>
    public void PinInstance(XRRenderPipelineInstance? instance)
        => _pinnedInstance = instance is null ? null : new WeakReference<XRRenderPipelineInstance>(instance);

    /// <summary>
    /// Records that <paramref name="pipeline"/> came from the script file at <paramref name="path"/>. The state keeps a
    /// strong reference to the most recent loaded pipeline, because the pin holds only a weak reference and nothing
    /// else may own a pipeline that a file made.
    /// </summary>
    public void SetScriptSource(RenderPipeline pipeline, string path)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _scriptPaths.AddOrUpdate(pipeline, path);
        LoadedScriptPipeline = pipeline;
    }

    /// <summary>Returns the script file of <paramref name="pipeline"/>, or null when it did not come from a file.</summary>
    public string? GetScriptPath(RenderPipeline pipeline)
        => _scriptPaths.TryGetValue(pipeline, out string? path) ? path : null;

    /// <summary>Gets the pipeline that the last script load made.</summary>
    public RenderPipeline? LoadedScriptPipeline { get; private set; }

    /// <summary>Removes the pinned target so the Workbench follows the active pipeline.</summary>
    public void Unpin()
    {
        _pinnedPipeline = null;
        _pinnedInstance = null;
    }
}
