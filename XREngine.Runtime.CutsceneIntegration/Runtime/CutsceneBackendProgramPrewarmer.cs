using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// The default program prewarmer. It sees the material variant build and the backend state of each T0 draw: the program
/// link and, on Vulkan, the graphics pipelines of the passes that draw the renderer.
/// </summary>
/// <remarks>
/// <para>
/// The material step uses the asynchronous uber variant build (<see cref="XRMaterial.RequestUberVariantPreparationIfNeeded"/>)
/// and its state (<see cref="XRMaterial.UberVariantStatus"/> and <see cref="XRMaterial.IsUberVariantReadyForRendering"/>).
/// A material without uber state needs no variant build.
/// </para>
/// <para>
/// The backend step runs on the render thread. One render-thread coroutine for each open calls
/// <see cref="XRMeshRenderer.QueryDrawReadiness"/> once in each frame for each draw whose state is not final, and stores the
/// result. The update thread only reads the stored results, so it never waits for the render thread or for a compile.
/// Ready, failed, and not-supported states are final. The coroutine ends when every draw has a final state or when
/// <see cref="Stop"/> is called. One closure and one entry for each draw are made at the open; a frame allocates nothing.
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
    private readonly Func<bool> _step;
    private readonly Dictionary<(XRMaterial Material, XRMeshRenderer Renderer), DrawEntry> _entriesByKey = [];
    private readonly List<DrawEntry> _entries = [];
    private readonly Lock _entriesLock = new();
    private int _running;
    private int _stopped;

    /// <summary>Creates a prewarmer that checks the active render backend. Create one for each open.</summary>
    public CutsceneBackendProgramPrewarmer()
        : this(DefaultProbe, null)
    {
    }

    /// <summary>Creates a prewarmer with another backend probe and scheduler. Tests use it.</summary>
    /// <param name="probe">Gives the backend state of a draw. It runs where the scheduler runs the step.</param>
    /// <param name="scheduler">
    /// Schedules the readiness step, or null for the render-thread coroutine of the host. The step follows the coroutine
    /// contract: it returns true when it is complete and false to run again in the next frame.
    /// </param>
    internal CutsceneBackendProgramPrewarmer(CutsceneDrawReadinessProbe probe, Action<Func<bool>>? scheduler)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
        _scheduler = scheduler;
        _step = Step;
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
        if (!material.IsUberVariantReadyForRendering())
        {
            reason = "the uber shader variant builds";
            return ECutsceneProgramState.Compiling;
        }

        ERenderDrawReadiness backendState = (ERenderDrawReadiness)Volatile.Read(ref entry.State);
        reason = Volatile.Read(ref entry.Reason);
        return ToProgramState(backendState);
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

    private static ERenderDrawReadiness DefaultProbe(XRMeshRenderer renderer, XRMaterial material, out string reason)
        => renderer.QueryDrawReadiness(material, out reason);

    private static bool IsFinal(ERenderDrawReadiness state)
        => state is ERenderDrawReadiness.Ready or ERenderDrawReadiness.Failed or ERenderDrawReadiness.NotSupported;

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
            return;
        }

        RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadCoroutine(_step, ReadinessCoroutineName);
    }

    /// <summary>
    /// Checks each draw whose state is not final one time. It follows the coroutine contract: true when the work is complete
    /// (every draw has a final state, or the open stopped), false to check again in the next frame.
    /// </summary>
    private bool Step()
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            Volatile.Write(ref _running, 0);
            return true;
        }

        int count;
        lock (_entriesLock)
            count = _entries.Count;

        bool allFinal = true;
        for (int i = 0; i < count; i++)
        {
            DrawEntry entry;
            lock (_entriesLock)
                entry = _entries[i];

            if (IsFinal((ERenderDrawReadiness)Volatile.Read(ref entry.State)))
                continue;

            ERenderDrawReadiness state = _probe(entry.Renderer, entry.Material, out string reason);
            Volatile.Write(ref entry.Reason, reason);
            Volatile.Write(ref entry.State, (int)state);
            if (!IsFinal(state))
                allFinal = false;
        }

        if (!allFinal)
            return false;

        // An entry added after the count was read needs another step. Keep the coroutine if one was added.
        Volatile.Write(ref _running, 0);
        int countAfter;
        lock (_entriesLock)
            countAfter = _entries.Count;
        if (countAfter != count && Volatile.Read(ref _stopped) == 0 && Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            return false;
        return true;
    }
}
