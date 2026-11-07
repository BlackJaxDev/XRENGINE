using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// The default program prewarmer. It sees the material variant build and the backend state of each T0 draw: the program
/// link and, on Vulkan, the graphics pipelines of the passes of the cutscene view.
/// </summary>
/// <remarks>
/// <para>
/// The material step uses the asynchronous uber variant build (<see cref="XRMaterial.RequestUberVariantPreparationIfNeeded"/>)
/// and its state (<see cref="XRMaterial.UberVariantStatus"/> and <see cref="XRMaterial.IsUberVariantReadyForRendering"/>).
/// A material without uber state needs no variant build.
/// </para>
/// <para>
/// The backend step runs on the render thread. One render-thread coroutine for each open checks each draw once in each
/// frame and stores the result. While the uber variant of a material builds, the draw is
/// <see cref="ECutsceneProgramState.Compiling"/> and the backend is not asked, because the variant changes the program and
/// the pipelines. Otherwise it calls <see cref="XRMeshRenderer.QueryDrawReadiness"/> with <see cref="View"/>. Only
/// failed and not-supported states are final. A ready draw is checked again in each frame, so a later program or pipeline
/// change makes it not ready again. The coroutine ends at <see cref="Stop"/>. A query that throws makes that draw failed,
/// with the exception message, and the coroutine continues. The update thread only reads the stored results, so it never
/// waits for the render thread or for a compile. One closure and one entry for each draw are made at the open; a frame
/// allocates nothing.
/// </para>
/// <para>
/// The draw is ready only when both steps are ready. A failed variant build or a failed backend build gives
/// <see cref="ECutsceneProgramState.Failed"/>. A backend that does not report the state gives
/// <see cref="ECutsceneProgramState.NotSupported"/>, never ready.
/// </para>
/// </remarks>
public sealed class CutsceneBackendProgramPrewarmer : ICutsceneProgramPrewarmer
{
    private const string ReadinessCoroutineName = "CutsceneOpen.ProgramReadiness";
    private const string VariantBuildsReason = "the uber shader variant builds";

    /// <summary>One T0 draw: the update thread writes the key, and the render thread writes the state and the reason.</summary>
    private sealed class DrawEntry(XRMaterial material, XRMeshRenderer renderer)
    {
        public XRMaterial Material { get; } = material;
        public XRMeshRenderer Renderer { get; } = renderer;
        public int State = (int)ERenderDrawReadiness.NotStarted;
        public string Reason = "the render thread did not check the draw yet";
    }

    private readonly CutsceneDrawReadinessProbe _probe;
    private readonly Action<Func<bool>>? _scheduler;
    private readonly Func<XRMaterial, bool> _isVariantReady;
    private readonly Func<bool> _step;
    private readonly Dictionary<(XRMaterial Material, XRMeshRenderer Renderer), DrawEntry> _entriesByKey = [];
    private readonly List<DrawEntry> _entries = [];
    private readonly Lock _entriesLock = new();
    private XRRenderPipelineInstance? _view;
    private int _running;
    private int _stopped;

    /// <summary>Creates a prewarmer that checks the active render backend. Create one for each open.</summary>
    public CutsceneBackendProgramPrewarmer()
        : this(DefaultProbe, null, null)
    {
    }

    /// <summary>Creates a prewarmer with another backend probe, scheduler, and variant check. Tests use it.</summary>
    /// <param name="probe">Gives the backend state of a draw. It runs where the scheduler runs the step.</param>
    /// <param name="scheduler">
    /// Schedules the readiness step, or null for the render-thread coroutine of the host. The step follows the coroutine
    /// contract: it returns true when it is complete and false to run again in the next frame.
    /// </param>
    /// <param name="isVariantReady">Tells if the uber variant of a material is ready, or null for the material state.</param>
    internal CutsceneBackendProgramPrewarmer(
        CutsceneDrawReadinessProbe probe,
        Action<Func<bool>>? scheduler,
        Func<XRMaterial, bool>? isVariantReady)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
        _scheduler = scheduler;
        _isVariantReady = isVariantReady ?? DefaultIsVariantReady;
        _step = Step;
    }

    /// <inheritdoc/>
    public XRRenderPipelineInstance? View
    {
        get => Volatile.Read(ref _view);
        set => Volatile.Write(ref _view, value);
    }

    /// <inheritdoc/>
    public void Request(XRMaterial material, XRMeshRenderer? renderer)
    {
        ArgumentNullException.ThrowIfNull(material);
        material.RequestUberVariantPreparationIfNeeded();
        if (renderer is not null)
            _ = GetOrAddEntry(material, renderer);
    }

    /// <inheritdoc/>
    public ECutsceneProgramState GetState(XRMaterial material, XRMeshRenderer? renderer, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(material);
        UberMaterialVariantStatus status = material.UberVariantStatus;
        if (status.Stage == EUberMaterialVariantStage.Failed)
        {
            reason = status.FailureReason ?? "the uber shader variant build failed";
            return ECutsceneProgramState.Failed;
        }

        if (renderer is null)
        {
            reason = "no renderer draws the T0 LOD yet";
            return ECutsceneProgramState.NotStarted;
        }

        DrawEntry entry = GetOrAddEntry(material, renderer);
        if (!_isVariantReady(material))
        {
            reason = VariantBuildsReason;
            return ECutsceneProgramState.Compiling;
        }

        ERenderDrawReadiness backendState = (ERenderDrawReadiness)Volatile.Read(ref entry.State);
        reason = Volatile.Read(ref entry.Reason);
        return ToProgramState(backendState);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _entriesByKey.Clear();
        lock (_entriesLock)
            _entries.Clear();
    }

    /// <inheritdoc/>
    public void Stop()
        => Volatile.Write(ref _stopped, 1);

    /// <summary>Maps a backend draw state to the open state. The two enumerations have the same members.</summary>
    internal static ECutsceneProgramState ToProgramState(ERenderDrawReadiness state)
        => state switch
        {
            ERenderDrawReadiness.NotStarted => ECutsceneProgramState.NotStarted,
            ERenderDrawReadiness.Compiling => ECutsceneProgramState.Compiling,
            ERenderDrawReadiness.Ready => ECutsceneProgramState.Ready,
            ERenderDrawReadiness.Failed => ECutsceneProgramState.Failed,
            _ => ECutsceneProgramState.NotSupported,
        };

    private static ERenderDrawReadiness DefaultProbe(
        XRMeshRenderer renderer,
        XRMaterial material,
        XRRenderPipelineInstance? view,
        out string reason)
        => renderer.QueryDrawReadiness(material, view, out reason);

    private static bool DefaultIsVariantReady(XRMaterial material)
        => material.IsUberVariantReadyForRendering();

    /// <summary>True for a state that a later check cannot change: failed or not supported. Ready is not final.</summary>
    private static bool IsFinal(ERenderDrawReadiness state)
        => state is ERenderDrawReadiness.Failed or ERenderDrawReadiness.NotSupported;

    // Open-time work: one entry for each new draw. A known draw allocates nothing.
    private DrawEntry GetOrAddEntry(XRMaterial material, XRMeshRenderer renderer)
    {
        if (_entriesByKey.TryGetValue((material, renderer), out DrawEntry? entry))
            return entry;

        entry = new DrawEntry(material, renderer);
        _entriesByKey.Add((material, renderer), entry);
        lock (_entriesLock)
            _entries.Add(entry);
        EnsureRunning();
        return entry;
    }

    private void EnsureRunning()
    {
        if (Volatile.Read(ref _stopped) != 0 || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;

        if (_scheduler is { } scheduler)
        {
            scheduler(_step);
            return;
        }

        // With no render host, no backend exists and no draw can wait, so one step runs here.
        if (!RuntimeRenderingHostServices.HasConcreteHost)
        {
            _ = _step();
            Volatile.Write(ref _running, 0);
            return;
        }

        RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadCoroutine(_step, ReadinessCoroutineName);
    }

    /// <summary>
    /// Checks each draw whose state is not final one time. It follows the coroutine contract: true when the open stopped,
    /// false to check again in the next frame.
    /// </summary>
    private bool Step()
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            Volatile.Write(ref _running, 0);
            return true;
        }

        XRRenderPipelineInstance? view = Volatile.Read(ref _view);
        for (int i = 0; ; i++)
        {
            DrawEntry entry;
            lock (_entriesLock)
            {
                if (i >= _entries.Count)
                    break;
                entry = _entries[i];
            }

            if (IsFinal((ERenderDrawReadiness)Volatile.Read(ref entry.State)))
                continue;

            CheckEntry(entry, view);
        }

        return false;
    }

    private void CheckEntry(DrawEntry entry, XRRenderPipelineInstance? view)
    {
        ERenderDrawReadiness state;
        string reason;
        try
        {
            if (!_isVariantReady(entry.Material))
            {
                // The variant replaces the program and the pipelines, so a backend result now would be stale.
                state = ERenderDrawReadiness.Compiling;
                reason = VariantBuildsReason;
            }
            else
            {
                state = _probe(entry.Renderer, entry.Material, view, out reason);
            }
        }
        catch (Exception ex)
        {
            state = ERenderDrawReadiness.Failed;
            reason = $"the readiness check threw {ex.GetType().Name}: {ex.Message}";
            Debug.LogWarning(
                ELogCategory.Animation,
                "Cutscene open: the readiness check of material '{0}' threw. The draw is marked failed. {1}",
                entry.Material.Name ?? "<unnamed>",
                ex);
        }

        Volatile.Write(ref entry.Reason, reason);
        Volatile.Write(ref entry.State, (int)state);
    }
}
