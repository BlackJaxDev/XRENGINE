using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

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
/// the pipelines. Otherwise it calls <see cref="XRMeshRenderer.QueryDrawReadiness"/> with <see cref="View"/> and the render
/// options override of the draw. Only failed and not-supported states are final. A ready draw is checked again in each
/// frame, so a later program or pipeline change makes it not ready again. A query that throws makes that draw failed, with
/// the exception message, and the coroutine continues. The update thread only reads the stored results, so it never waits
/// for the render thread or for a compile. One closure and one entry for each draw are made at the open; a frame allocates
/// nothing.
/// </para>
/// <para>
/// At <see cref="Stop"/>, the next coroutine step ends the backend readiness requests of every draw
/// (<see cref="XRMeshRenderer.EndDrawReadinessQueries"/>) and the coroutine ends. <see cref="Reset"/> ends the requests of
/// the dropped draws in the same way.
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

    /// <summary>One T0 draw: the update thread writes the key and the options, and the render thread writes the state and the reason.</summary>
    private sealed class DrawEntry(XRMaterial material, XRMeshRenderer renderer)
    {
        public XRMaterial Material { get; } = material;
        public XRMeshRenderer Renderer { get; } = renderer;
        public RenderingParameters? RenderOptionsOverride;
        public int State = (int)ERenderDrawReadiness.NotStarted;
        public string Reason = "the render thread did not check the draw yet";
    }

    private readonly CutsceneDrawReadinessProbe _probe;
    private readonly Action<Func<bool>>? _scheduler;
    private readonly Func<XRMaterial, bool> _isVariantReady;
    private readonly Action<XRMeshRenderer> _endQueries;
    private readonly Func<bool> _step;
    private readonly Dictionary<(XRMaterial Material, XRMeshRenderer Renderer), DrawEntry> _entriesByKey = [];
    private readonly List<DrawEntry> _entries = [];
    private readonly List<DrawEntry> _endingEntries = [];
    private readonly Lock _entriesLock = new();
    private XRRenderPipelineInstance? _view;
    private int _running;
    private int _stopped;

    /// <summary>Creates a prewarmer that checks the active render backend. Create one for each open.</summary>
    public CutsceneBackendProgramPrewarmer()
        : this(DefaultProbe, null, null, null)
    {
    }

    /// <summary>Creates a prewarmer with another backend probe, scheduler, variant check, and request end. Tests use it.</summary>
    /// <param name="probe">Gives the backend state of a draw. It runs where the scheduler runs the step.</param>
    /// <param name="scheduler">
    /// Schedules the readiness step, or null for the render-thread coroutine of the host. The step follows the coroutine
    /// contract: it returns true when it is complete and false to run again in the next frame.
    /// </param>
    /// <param name="isVariantReady">Tells if the uber variant of a material is ready, or null for the material state.</param>
    /// <param name="endQueries">Ends the backend requests of a renderer, or null for <see cref="XRMeshRenderer.EndDrawReadinessQueries"/>.</param>
    internal CutsceneBackendProgramPrewarmer(
        CutsceneDrawReadinessProbe probe,
        Action<Func<bool>>? scheduler,
        Func<XRMaterial, bool>? isVariantReady,
        Action<XRMeshRenderer>? endQueries)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
        _scheduler = scheduler;
        _isVariantReady = isVariantReady ?? DefaultIsVariantReady;
        _endQueries = endQueries ?? DefaultEndQueries;
        _step = Step;
    }

    /// <inheritdoc/>
    public XRRenderPipelineInstance? View
    {
        get => Volatile.Read(ref _view);
        set => Volatile.Write(ref _view, value);
    }

    /// <inheritdoc/>
    public void Request(XRMaterial material, XRMeshRenderer? renderer, RenderingParameters? renderOptionsOverride)
    {
        ArgumentNullException.ThrowIfNull(material);
        material.RequestUberVariantPreparationIfNeeded();
        if (renderer is not null)
            _ = GetOrAddEntry(material, renderer, renderOptionsOverride);
    }

    /// <inheritdoc/>
    public ECutsceneProgramState GetState(
        XRMaterial material,
        XRMeshRenderer? renderer,
        RenderingParameters? renderOptionsOverride,
        out string? reason)
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

        DrawEntry entry = GetOrAddEntry(material, renderer, renderOptionsOverride);
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
        {
            _endingEntries.AddRange(_entries);
            _entries.Clear();
        }
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
        RenderingParameters? renderOptionsOverride,
        XRRenderPipelineInstance? view,
        out string reason)
        => renderer.QueryDrawReadiness(material, renderOptionsOverride, view, out reason);

    private static bool DefaultIsVariantReady(XRMaterial material)
        => material.IsUberVariantReadyForRendering();

    private static void DefaultEndQueries(XRMeshRenderer renderer)
        => renderer.EndDrawReadinessQueries();

    /// <summary>True for a state that a later check cannot change: failed or not supported. Ready is not final.</summary>
    private static bool IsFinal(ERenderDrawReadiness state)
        => state is ERenderDrawReadiness.Failed or ERenderDrawReadiness.NotSupported;

    // Open-time work: one entry for each new draw. A known draw allocates nothing.
    private DrawEntry GetOrAddEntry(XRMaterial material, XRMeshRenderer renderer, RenderingParameters? renderOptionsOverride)
    {
        if (!_entriesByKey.TryGetValue((material, renderer), out DrawEntry? entry))
        {
            entry = new DrawEntry(material, renderer);
            _entriesByKey.Add((material, renderer), entry);
            lock (_entriesLock)
                _entries.Add(entry);
            EnsureRunning();
        }

        Volatile.Write(ref entry.RenderOptionsOverride, renderOptionsOverride);
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
    /// Ends the requests of dropped draws, then checks each draw whose state is not final one time. After <see cref="Stop"/>
    /// it ends the requests of every draw and completes. It follows the coroutine contract: true when the open stopped, false
    /// to check again in the next frame.
    /// </summary>
    private bool Step()
    {
        EndDroppedEntries();
        if (Volatile.Read(ref _stopped) != 0)
        {
            EndAllEntries();
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

    private void EndDroppedEntries()
    {
        for (int i = 0; ; i++)
        {
            DrawEntry entry;
            lock (_entriesLock)
            {
                if (i >= _endingEntries.Count)
                {
                    _endingEntries.Clear();
                    return;
                }
                entry = _endingEntries[i];
            }

            // A renderer that a new draw of the open uses again keeps its requests.
            if (!IsRendererInUse(entry.Renderer))
                EndEntry(entry);
        }
    }

    private bool IsRendererInUse(XRMeshRenderer renderer)
    {
        lock (_entriesLock)
        {
            for (int i = 0; i < _entries.Count; i++)
                if (ReferenceEquals(_entries[i].Renderer, renderer))
                    return true;
            return false;
        }
    }

    private void EndAllEntries()
    {
        for (int i = 0; ; i++)
        {
            DrawEntry entry;
            lock (_entriesLock)
            {
                if (i >= _entries.Count)
                    return;
                entry = _entries[i];
            }

            EndEntry(entry);
        }
    }

    private void EndEntry(DrawEntry entry)
    {
        try
        {
            _endQueries(entry.Renderer);
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                ELogCategory.Animation,
                "Cutscene open: ending the readiness requests of material '{0}' threw. {1}",
                entry.Material.Name ?? "<unnamed>",
                ex);
        }
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
                state = _probe(entry.Renderer, entry.Material, Volatile.Read(ref entry.RenderOptionsOverride), view, out reason);
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
