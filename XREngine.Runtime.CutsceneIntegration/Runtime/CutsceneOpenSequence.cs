using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Opens a cutscene on a <see cref="CutsceneDirectorComponent"/> and reports when an instant seek can show the T0 data.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunAsync"/> does these steps:
/// </para>
/// <list type="number">
/// <item><description>It opens the director: compile, bindings, player, and the residency session. The session requests T0
/// for each asset of the closure.</description></item>
/// <item><description>It finds the proxy materials: the material of the lowest LOD of each sub-mesh of the bound nodes and
/// their descendants. The proxy material is an authored LOD material, not a fallback. <see cref="ProxyMaterials"/> lists
/// them for diagnostics. It requests their programs at once.</description></item>
/// <item><description>At each director frame, it reads the T0 loads that did not complete
/// (<see cref="CutsceneResidencySession.PendingProxyCount"/>) and the program state of each proxy material. When both are
/// complete, it sets <see cref="CutscenePlayer.ReadyForInstantSeek"/>, requests the programs of the other LOD materials as
/// lower-priority work, and completes the task with true.</description></item>
/// </list>
/// <para>
/// The sequence advances on the director frame (<see cref="CutsceneDirectorComponent.AdvanceFrame"/>), on the update thread.
/// It never waits for the render thread. A program failure adds a diagnostic to <see cref="CutsceneDirectorComponent.Diagnostics"/>
/// that names the material and the reason; the task then completes with false and the cutscene stays open. A cancel closes
/// the director at the next frame, which restores the bound nodes and destroys the spawned instances, and the task is
/// canceled. A close of the director by another caller also cancels the task.
/// </para>
/// </remarks>
public sealed class CutsceneOpenSequence
{
    private const float OpenedProgress = 0.1f;
    private const float ProxyProgressShare = 0.6f;
    private const float ProgramProgressShare = 0.3f;

    private readonly CutsceneDirectorComponent _director;
    private readonly ICutsceneProgramPrewarmer _prewarmer;
    private readonly List<XRMaterial> _proxyMaterials = [];
    private readonly List<XRMaterial> _detailMaterials = [];
    private readonly HashSet<XRMaterial> _reportedFailures = new(ReferenceEqualityComparer.Instance);

    private TaskCompletionSource<bool>? _completion;
    private CancellationTokenRegistration _cancelRegistration;
    private CancellationToken _cancellationToken;
    private IProgress<float>? _progress;
    private CutscenePlayer? _player;
    private CutsceneResidencySession? _residency;
    private int _initialPendingProxies;
    private int _cancelRequested;
    private float _reportedProgress = -1.0f;
    private bool _started;
    private bool _finished;

    /// <summary>Creates an open sequence for a director.</summary>
    /// <param name="director">The director that opens the cutscene. Set its asset and resolver first.</param>
    /// <param name="prewarmer">The program prewarmer, or null for <see cref="CutsceneMaterialProgramPrewarmer.Instance"/>.</param>
    public CutsceneOpenSequence(CutsceneDirectorComponent director, ICutsceneProgramPrewarmer? prewarmer = null)
    {
        ArgumentNullException.ThrowIfNull(director);
        _director = director;
        _prewarmer = prewarmer ?? CutsceneMaterialProgramPrewarmer.Instance;
    }

    /// <summary>The materials of the lowest LOD of each bound sub-mesh: the T0 materials whose programs the open waits for.</summary>
    public IReadOnlyList<XRMaterial> ProxyMaterials => _proxyMaterials;

    /// <summary>The materials of the other LODs. Their programs start after T0 is ready, and the open does not wait for them.</summary>
    public IReadOnlyList<XRMaterial> DetailMaterials => _detailMaterials;

    /// <summary>The number of proxy materials whose programs were not ready at the last frame.</summary>
    public int PendingProgramCount { get; private set; }

    /// <summary>The number of proxy materials whose programs failed.</summary>
    public int FailedProgramCount => _reportedFailures.Count;

    /// <summary>True after the sequence completed, failed, or was canceled.</summary>
    public bool IsFinished => _finished;

    /// <summary>
    /// Opens the cutscene and completes when T0 and the proxy programs are ready. Call it one time, on the update thread.
    /// </summary>
    /// <param name="progress">Receives the progress from 0 to 1 when it changes, or null.</param>
    /// <param name="cancellationToken">A token that cancels the open. A cancel closes the director and restores the scene.</param>
    /// <returns>
    /// A task that gives true when <see cref="CutscenePlayer.ReadyForInstantSeek"/> is true, and false when the open failed
    /// or a proxy program failed. <see cref="CutsceneDirectorComponent.Diagnostics"/> gives the reason of a false result.
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

        Task open = _director.OpenAsync(cancellationToken);
        if (open.IsCanceled)
        {
            _finished = true;
            return Task.FromCanceled<bool>(cancellationToken);
        }

        if (!_director.IsOpen || _director.Player is not { } player)
        {
            _finished = true;
            return Task.FromResult(false);
        }

        _player = player;
        _residency = _director.Residency;
        _progress = progress;
        _cancellationToken = cancellationToken;
        _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_director.Bindings is { } bindings)
            CollectMaterials(bindings);
        for (int i = 0; i < _proxyMaterials.Count; i++)
            _prewarmer.Request(_proxyMaterials[i]);

        _initialPendingProxies = _residency?.PendingProxyCount ?? 0;
        _director.AttachOpenSequence(this);
        if (cancellationToken.CanBeCanceled)
            _cancelRegistration = cancellationToken.Register(static state => ((CutsceneOpenSequence)state!).RequestCancel(), this);

        Report(OpenedProgress);
        Evaluate();
        return _completion.Task;
    }

    /// <summary>Advances the sequence. The director calls it at the end of each frame, on the update thread.</summary>
    internal void OnFrame()
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

    /// <summary>Cancels the task when the director closes before the sequence finished.</summary>
    internal void OnDirectorClosed()
    {
        if (_finished)
            return;

        _finished = true;
        _cancelRegistration.Dispose();
        _completion?.TrySetCanceled();
    }

    // The token callback can run on any thread, so it only sets a flag. The next director frame closes the cutscene.
    private void RequestCancel()
        => Volatile.Write(ref _cancelRequested, 1);

    private void Cancel()
    {
        _finished = true;
        _cancelRegistration.Dispose();
        _director.DetachOpenSequence(this);

        // Close restores the bound nodes with the restore setting of the asset and destroys the spawned instances.
        _director.Close();
        _completion?.TrySetCanceled(_cancellationToken);
    }

    private void Evaluate()
    {
        int pendingProxies = _residency is { IsClosed: false } residency ? residency.PendingProxyCount : 0;

        int pendingPrograms = 0;
        for (int i = 0; i < _proxyMaterials.Count; i++)
        {
            XRMaterial material = _proxyMaterials[i];
            switch (_prewarmer.GetState(material, out string? failureReason))
            {
                case ECutsceneProgramState.Pending:
                    pendingPrograms++;
                    break;
                case ECutsceneProgramState.Failed:
                    if (_reportedFailures.Add(material))
                        ReportProgramFailure(material, failureReason);
                    break;
            }
        }
        PendingProgramCount = pendingPrograms;

        float proxyFraction = _initialPendingProxies <= 0 ? 1.0f : 1.0f - ((float)pendingProxies / _initialPendingProxies);
        float programFraction = _proxyMaterials.Count == 0 ? 1.0f : 1.0f - ((float)pendingPrograms / _proxyMaterials.Count);
        Report(OpenedProgress + (ProxyProgressShare * Math.Clamp(proxyFraction, 0.0f, 1.0f)) + (ProgramProgressShare * programFraction));

        if (pendingProxies > 0 || pendingPrograms > 0)
            return;

        if (_reportedFailures.Count > 0)
        {
            Finish(false);
            return;
        }

        _player!.ReadyForInstantSeek = true;
        for (int i = 0; i < _detailMaterials.Count; i++)
            _prewarmer.Request(_detailMaterials[i]);
        Report(1.0f);
        Finish(true);
    }

    private void Finish(bool result)
    {
        _finished = true;
        _cancelRegistration.Dispose();
        _director.DetachOpenSequence(this);
        _completion?.TrySetResult(result);
    }

    private void ReportProgramFailure(XRMaterial material, string? failureReason)
    {
        string message = $"The program of proxy material '{material.Name ?? "<unnamed>"}' failed, so its T0 LOD cannot draw: {failureReason ?? "no reason was given"}.";
        _director.AddOpenDiagnostic(message);
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
            if (components[c] is not ModelComponent { Model: { } model })
                continue;

            var meshes = model.Meshes;
            for (int m = 0; m < meshes.Count; m++)
                if (meshes[m] is { } subMesh)
                    CollectSubMesh(subMesh, proxies, details);
        }

        var children = node.Transform.Children;
        for (int i = 0; i < children.Count; i++)
            if (children[i]?.SceneNode is { } child)
                CollectNode(child, visited, proxies, details);
    }

    private void CollectSubMesh(SubMesh subMesh, HashSet<XRMaterial> proxies, HashSet<XRMaterial> details)
    {
        // The LODs sort by visible distance, so the last LOD is the lowest one: the T0 data of the sub-mesh.
        SubMeshLOD? lowest = null;
        foreach (SubMeshLOD lod in subMesh.LODs)
        {
            if (lowest?.Material is { } detail)
                details.Add(detail);
            lowest = lod;
        }

        if (lowest?.Material is { } proxy && proxies.Add(proxy))
            _proxyMaterials.Add(proxy);
    }
}
