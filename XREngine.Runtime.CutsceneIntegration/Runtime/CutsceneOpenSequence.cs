using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Opens a cutscene on an <see cref="ICutsceneOpenHost"/> (a <see cref="CutsceneDirectorComponent"/> or the editor preview
/// host) and reports when an instant seek can show the T0 data.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunAsync"/> does these steps:
/// </para>
/// <list type="number">
/// <item><description>It opens the host: compile, bindings, player, and the residency session. The session requests T0
/// for each asset of the closure.</description></item>
/// <item><description>It finds the T0 draws: for each renderable mesh of the bound nodes and their descendants, the
/// material override if the mesh has one, otherwise the material of the lowest LOD of its sub-mesh, drawn by the renderer of
/// the lowest LOD. The proxy material is an authored LOD material, not a fallback. <see cref="ProxyMaterials"/> lists the
/// materials for diagnostics. It requests the programs of the draws at once.</description></item>
/// <item><description>At each host frame, it reads the T0 loads that did not complete
/// (<see cref="CutsceneResidencySession.PendingProxyCount"/>) and the program state of each T0 draw on the active backend
/// (the variant build, the program link, and on Vulkan the graphics pipelines). When every T0 load completed and every T0
/// draw is ready, it sets <see cref="CutscenePlayer.ReadyForInstantSeek"/>, requests the programs of the other LOD
/// materials as lower-priority work, and completes the task with true.</description></item>
/// </list>
/// <para>
/// The sequence advances on the host frame, on the update thread. It never waits for the render thread, and a caller must
/// not block on its task, because the task needs later frames. A failed draw, or a draw whose backend does not report its
/// state, adds one diagnostic to the host that names the material and the reason; when no draw is still waiting, the task
/// completes with false and the cutscene stays open. A host with no residency session cannot tell that T0 is resident: the
/// sequence adds one diagnostic, <see cref="CutscenePlayer.ReadyForInstantSeek"/> stays false, and the task completes with
/// false when the programs are ready. A cancel closes the host at the next host frame, which restores the bound nodes and
/// destroys the spawned instances, and the task is canceled. A close of the host by another caller also cancels the task.
/// </para>
/// </remarks>
public sealed class CutsceneOpenSequence
{
    private const float OpenedProgress = 0.1f;
    private const float ProxyProgressShare = 0.6f;
    private const float ProgramProgressShare = 0.3f;

    private readonly ICutsceneOpenHost _host;
    private readonly ICutsceneProgramPrewarmer _prewarmer;
    private readonly List<CutsceneProxyDraw> _proxyDraws = [];
    private readonly List<XRMaterial> _proxyMaterials = [];
    private readonly List<XRMaterial> _detailMaterials = [];

    private TaskCompletionSource<bool>? _completion;
    private CancellationTokenRegistration _cancelRegistration;
    private CancellationToken _cancellationToken;
    private IProgress<float>? _progress;
    private bool[] _diagnosticReported = [];
    private bool[] _waitWarningReported = [];
    private int[] _notReadyFrames = [];
    private CutsceneBindingTable? _collectedBindings;
    private int _initialPendingProxies;
    private int _cancelRequested;
    private float _reportedProgress = -1.0f;
    private bool _started;
    private bool _finished;

    /// <summary>Creates an open sequence for a director.</summary>
    /// <param name="director">The director that opens the cutscene. Set its asset and resolver first.</param>
    /// <param name="prewarmer">The program prewarmer, or null for a new <see cref="CutsceneBackendProgramPrewarmer"/>.</param>
    public CutsceneOpenSequence(CutsceneDirectorComponent director, ICutsceneProgramPrewarmer? prewarmer = null)
        : this((ICutsceneOpenHost)director, prewarmer)
    {
    }

    /// <summary>Creates an open sequence for a host.</summary>
    /// <param name="host">The host that opens the cutscene.</param>
    /// <param name="prewarmer">The program prewarmer, or null for a new <see cref="CutsceneBackendProgramPrewarmer"/>.</param>
    public CutsceneOpenSequence(ICutsceneOpenHost host, ICutsceneProgramPrewarmer? prewarmer = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _prewarmer = prewarmer ?? new CutsceneBackendProgramPrewarmer();
    }

    /// <summary>The proxy materials: the distinct materials of the T0 draws whose programs the open waits for.</summary>
    public IReadOnlyList<XRMaterial> ProxyMaterials => _proxyMaterials;

    /// <summary>The materials of the other LODs. Their programs start after T0 is ready, and the open does not wait for them.</summary>
    public IReadOnlyList<XRMaterial> DetailMaterials => _detailMaterials;

    /// <summary>The number of T0 draws: one for each renderable mesh of the bound closure that has a T0 material.</summary>
    public int ProxyDrawCount => _proxyDraws.Count;

    /// <summary>The number of T0 draws that were not ready at the last frame and can still become ready.</summary>
    public int PendingProgramCount => NotStartedProgramCount + CompilingProgramCount;

    /// <summary>The number of T0 draws whose program work did not start, or whose state the backend cannot know yet.</summary>
    public int NotStartedProgramCount { get; private set; }

    /// <summary>The reason of the first T0 draw that was not started or compiling at the last frame, or null. For diagnostics.</summary>
    public string? FirstPendingReason { get; private set; }

    /// <summary>The number of T0 draws whose variant build, program link, or pipeline compile ran at the last frame.</summary>
    public int CompilingProgramCount { get; private set; }

    /// <summary>The number of T0 draws that were ready at the last frame.</summary>
    public int ReadyProgramCount { get; private set; }

    /// <summary>The number of T0 draws whose programs failed at the last frame.</summary>
    public int FailedProgramCount { get; private set; }

    /// <summary>The number of T0 draws whose backend did not report the state at the last frame.</summary>
    public int UnsupportedProgramCount { get; private set; }

    /// <summary>
    /// The number of host frames that a T0 draw can stay not started or compiling before the open adds one diagnostic for it.
    /// The open keeps waiting after the diagnostic.
    /// </summary>
    public int ProgramWaitWarningFrames { get; set; } = 1800;

    /// <summary>The number of T0 loads that did not complete at the last frame.</summary>
    public int PendingProxyCount { get; private set; }

    /// <summary>True after the sequence completed, failed, or was canceled.</summary>
    public bool IsFinished => _finished;

    /// <summary>
    /// Opens the cutscene and completes when T0 and the T0 draws are ready. Call it one time, on the update thread.
    /// Do not block on the task: it needs later host frames.
    /// </summary>
    /// <param name="progress">Receives the progress from 0 to 1 when it changes, or null.</param>
    /// <param name="cancellationToken">
    /// A token that cancels the open. The host closes and restores the scene at its next frame, so a host that does not tick
    /// keeps the cutscene open until a frame runs or another caller closes it.
    /// </param>
    /// <returns>
    /// A task that gives true when <see cref="CutscenePlayer.ReadyForInstantSeek"/> is true, and false when the open failed,
    /// a T0 draw failed or has no backend state, or no residency session exists. The diagnostics of the host give the reason.
    /// </returns>
    public Task<bool> RunAsync(IProgress<float>? progress, CancellationToken cancellationToken)
    {
        if (_started)
            throw new InvalidOperationException("An open sequence runs one time. Create a new sequence.");
        _started = true;

        if (cancellationToken.IsCancellationRequested)
        {
            _finished = true;
            return Task.FromCanceled<bool>(cancellationToken);
        }

        bool opened;
        try
        {
            opened = _host.OpenForSequence(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _finished = true;
            return Task.FromCanceled<bool>(cancellationToken);
        }

        if (!opened || _host.OpenPlayer is null)
        {
            _finished = true;
            return Task.FromResult(false);
        }

        _progress = progress;
        _cancellationToken = cancellationToken;
        _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        UpdatePrewarmerView();
        CollectAndRequestDraws();

        if (_host.OpenResidency is { } residency)
            _initialPendingProxies = residency.PendingProxyCount;
        else
            _host.AddOpenDiagnostic("No residency session runs, so the open cannot tell that T0 is resident. ReadyForInstantSeek stays false.");

        _host.AttachOpenSequence(this);
        if (cancellationToken.CanBeCanceled)
            _cancelRegistration = cancellationToken.Register(static state => ((CutsceneOpenSequence)state!).RequestCancel(), this);

        Report(OpenedProgress);
        Evaluate();
        return _completion.Task;
    }

    /// <summary>Advances the sequence. The host calls it at the end of each frame, on the update thread.</summary>
    public void OnHostFrame()
    {
        if (_finished)
            return;

        if (Volatile.Read(ref _cancelRequested) != 0)
        {
            Cancel();
            return;
        }

        Evaluate();
    }

    /// <summary>Cancels the task. The host calls it when it closes before the sequence finished.</summary>
    public void OnHostClosed()
    {
        if (_finished)
            return;

        _finished = true;
        _prewarmer.Stop();
        _cancelRegistration.Dispose();
        _completion?.TrySetCanceled();
    }

    // The token callback can run on any thread, so it only sets a flag. The next host frame closes the cutscene.
    private void RequestCancel()
        => Volatile.Write(ref _cancelRequested, 1);

    private void Cancel()
    {
        _finished = true;
        _prewarmer.Stop();
        _cancelRegistration.Dispose();
        _host.DetachOpenSequence(this);

        // The close restores the bound nodes with the restore setting of the asset and destroys the spawned instances.
        _host.CloseForSequence();
        _completion?.TrySetCanceled(_cancellationToken);
    }

    private void Evaluate()
    {
        if (_host.OpenPlayer is not { } player)
        {
            OnHostClosed();
            return;
        }

        UpdatePrewarmerView();

        // A preview recompile spawns the instances again and makes a new binding table, so the old draws point to
        // destroyed renderables. Collect the draws again from the new bindings.
        if (!ReferenceEquals(_host.OpenBindings, _collectedBindings))
        {
            _prewarmer.Reset();
            CollectAndRequestDraws();
        }

        CutsceneResidencySession? residency = _host.OpenResidency;
        int pendingProxies = residency is { IsClosed: false } ? residency.PendingProxyCount : 0;
        PendingProxyCount = pendingProxies;

        string? firstPendingReason = null;
        int notStarted = 0;
        int compiling = 0;
        int ready = 0;
        int failed = 0;
        int unsupported = 0;
        for (int i = 0; i < _proxyDraws.Count; i++)
        {
            CutsceneProxyDraw draw = _proxyDraws[i];
            ECutsceneProgramState state = _prewarmer.GetState(draw.Material, draw.ResolveRenderer(), draw.RenderOptionsOverride, out string? reason);
            if (firstPendingReason is null && state is ECutsceneProgramState.NotStarted or ECutsceneProgramState.Compiling)
                firstPendingReason = reason ?? "no reason was given";
            TrackWait(i, draw, state, reason);
            switch (state)
            {
                case ECutsceneProgramState.NotStarted:
                    notStarted++;
                    break;
                case ECutsceneProgramState.Compiling:
                    compiling++;
                    break;
                case ECutsceneProgramState.Ready:
                    ready++;
                    break;
                case ECutsceneProgramState.Failed:
                    failed++;
                    ReportDrawDiagnostic(i, draw.Material, "failed, so its T0 LOD cannot draw", reason);
                    break;
                default:
                    unsupported++;
                    ReportDrawDiagnostic(i, draw.Material, "has no readiness state on the active render backend, so the open cannot tell that its T0 LOD draws", reason);
                    break;
            }
        }
        NotStartedProgramCount = notStarted;
        FirstPendingReason = firstPendingReason;
        CompilingProgramCount = compiling;
        ReadyProgramCount = ready;
        FailedProgramCount = failed;
        UnsupportedProgramCount = unsupported;

        int pendingPrograms = notStarted + compiling;
        float proxyFraction = _initialPendingProxies <= 0 ? 1.0f : 1.0f - ((float)pendingProxies / _initialPendingProxies);
        float programFraction = _proxyDraws.Count == 0 ? 1.0f : (float)ready / _proxyDraws.Count;
        Report(OpenedProgress + (ProxyProgressShare * Math.Clamp(proxyFraction, 0.0f, 1.0f)) + (ProgramProgressShare * programFraction));

        if (pendingProxies > 0 || pendingPrograms > 0)
            return;

        // A failed or unsupported draw is never ready. With no residency session, T0 residency is not known.
        if (failed > 0 || unsupported > 0 || residency is null)
        {
            Finish(false);
            return;
        }

        player.ReadyForInstantSeek = true;
        for (int i = 0; i < _detailMaterials.Count; i++)
            _prewarmer.Request(_detailMaterials[i], null, null);
        Report(1.0f);
        Finish(true);
    }

    private void Finish(bool result)
    {
        _finished = true;
        _prewarmer.Stop();
        _cancelRegistration.Dispose();
        _host.DetachOpenSequence(this);
        _completion?.TrySetResult(result);
    }

    private void UpdatePrewarmerView()
        => _prewarmer.View = _host.OpenViewport?.RenderPipelineInstance;

    // Open-time work, and again after a preview recompile: the lists and arrays are made one time for each collection.
    private void CollectAndRequestDraws()
    {
        _proxyDraws.Clear();
        _proxyMaterials.Clear();
        _detailMaterials.Clear();
        _collectedBindings = _host.OpenBindings;
        if (_collectedBindings is { } bindings)
            CollectMaterials(bindings);

        _diagnosticReported = new bool[_proxyDraws.Count];
        _waitWarningReported = new bool[_proxyDraws.Count];
        _notReadyFrames = new int[_proxyDraws.Count];
        for (int i = 0; i < _proxyDraws.Count; i++)
        {
            CutsceneProxyDraw draw = _proxyDraws[i];
            _prewarmer.Request(draw.Material, draw.ResolveRenderer(), draw.RenderOptionsOverride);
        }
    }

    /// <summary>
    /// Counts the frames that a draw stays not started or compiling. After <see cref="ProgramWaitWarningFrames"/> frames it
    /// adds one diagnostic for the draw that names the material, the renderer, the mesh submission strategy, the state, and
    /// the reason. The open keeps waiting: a wait that does not end is shown, never hidden and never completed in silence.
    /// </summary>
    private void TrackWait(int drawIndex, CutsceneProxyDraw draw, ECutsceneProgramState state, string? reason)
    {
        if (state is not (ECutsceneProgramState.NotStarted or ECutsceneProgramState.Compiling))
        {
            _notReadyFrames[drawIndex] = 0;
            return;
        }

        if (++_notReadyFrames[drawIndex] < ProgramWaitWarningFrames || _waitWarningReported[drawIndex])
            return;

        _waitWarningReported[drawIndex] = true;
        string message =
            $"The T0 draw of proxy material '{draw.Material.Name ?? "<unnamed>"}' on renderer '{DescribeRenderer(draw.ResolveRenderer())}' " +
            $"is still {state} after {_notReadyFrames[drawIndex]} frames (mesh submission strategy {RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy()}): " +
            $"{reason ?? "no reason was given"}. ReadyForInstantSeek stays false while the open waits.";
        _host.AddOpenDiagnostic(message);
        Debug.LogWarning(ELogCategory.Animation, "Cutscene open: {0}", message);
    }

    private static string DescribeRenderer(XRMeshRenderer? renderer)
        => renderer is null ? "<none>" : renderer.Name ?? renderer.Mesh?.Name ?? "<unnamed>";

    private void ReportDrawDiagnostic(int drawIndex, XRMaterial material, string problem, string? reason)
    {
        if (_diagnosticReported[drawIndex])
            return;
        _diagnosticReported[drawIndex] = true;

        string message = $"The program of proxy material '{material.Name ?? "<unnamed>"}' {problem}: {reason ?? "no reason was given"}.";
        _host.AddOpenDiagnostic(message);
        Debug.LogWarning(ELogCategory.Animation, "Cutscene open: {0}", message);
    }

    private void Report(float value)
    {
        if (_progress is null || value <= _reportedProgress)
            return;

        _reportedProgress = value;
        _progress.Report(value);
    }

    // Open-time work: the lists and sets are made one time for each open.
    private void CollectMaterials(CutsceneBindingTable bindings)
    {
        HashSet<SceneNode> visited = new(ReferenceEqualityComparer.Instance);
        HashSet<XRMaterial> proxies = new(ReferenceEqualityComparer.Instance);
        HashSet<XRMaterial> details = new(ReferenceEqualityComparer.Instance);
        for (int slot = 0; slot < bindings.SlotCount; slot++)
            if (bindings.GetNode(slot) is { } node)
                CollectNode(node, visited, proxies, details);

        foreach (XRMaterial material in details)
            if (!proxies.Contains(material))
                _detailMaterials.Add(material);
    }

    private void CollectNode(SceneNode node, HashSet<SceneNode> visited, HashSet<XRMaterial> proxies, HashSet<XRMaterial> details)
    {
        if (!visited.Add(node))
            return;

        var components = node.Components;
        for (int c = 0; c < components.Count; c++)
        {
            if (components[c] is not RenderableComponent renderableComponent)
                continue;

            var meshes = renderableComponent.Meshes;
            for (int m = 0; m < meshes.Count; m++)
                if (meshes[m] is { } renderable)
                    CollectRenderable(renderable, proxies, details);
        }

        var children = node.Transform.Children;
        for (int i = 0; i < children.Count; i++)
            if (children[i]?.SceneNode is { } child)
                CollectNode(child, visited, proxies, details);
    }

    private void CollectRenderable(RenderableMesh renderable, HashSet<XRMaterial> proxies, HashSet<XRMaterial> details)
    {
        // A material override draws on every LOD, so it is the T0 material and no LOD material draws.
        if (renderable.MaterialOverride is { } materialOverride)
        {
            AddProxy(renderable, materialOverride, proxies);
            return;
        }

        if (renderable.SourceSubMesh is not { } subMesh)
        {
            if (renderable.CurrentLODRenderer?.Material is { } current)
                AddProxy(renderable, current, proxies);
            return;
        }

        // The LODs sort by visible distance, so the last LOD is the lowest one: the T0 data of the sub-mesh.
        SubMeshLOD? lowest = null;
        foreach (SubMeshLOD lod in subMesh.LODs)
        {
            if (lowest?.Material is { } detail)
                details.Add(detail);
            lowest = lod;
        }

        if (lowest?.Material is { } proxy)
            AddProxy(renderable, proxy, proxies);
    }

    private void AddProxy(RenderableMesh renderable, XRMaterial material, HashSet<XRMaterial> proxies)
    {
        // Each renderable is one T0 draw: two renderables with one material have their own program and pipeline state.
        _proxyDraws.Add(new CutsceneProxyDraw(renderable, material));
        if (proxies.Add(material))
            _proxyMaterials.Add(material);
    }
}
