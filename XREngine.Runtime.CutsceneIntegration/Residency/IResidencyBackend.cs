namespace XREngine.Cutscenes;

/// <summary>
/// Loads and releases the levels of detail of assets for the cutscene residency planner.
/// The planner calls these members on the update thread. The backend reports complete work
/// through <see cref="CutsceneResidencyPlanner.NotifyCompleted"/>, which accepts calls from any thread.
/// </summary>
public interface IResidencyBackend
{
    /// <summary>
    /// Starts the work for a request. The planner calls this method in priority order.
    /// Returns <see cref="ResidencyTicket.None"/> if the backend does not accept the request.
    /// </summary>
    /// <param name="request">The request.</param>
    ResidencyTicket Request(in ResidencyRequest request);

    /// <summary>
    /// Stops the work of a ticket. If the work is complete, the ticket no longer holds its level,
    /// and the backend can release that data. A complete result can stay in a cache.
    /// </summary>
    /// <param name="ticket">The ticket to cancel.</param>
    void Cancel(ResidencyTicket ticket);

    /// <summary>
    /// Gets the highest level of an asset that is resident now. Set the level before the call to
    /// <see cref="CutsceneResidencyPlanner.NotifyCompleted"/>. The planner does not hold a level that only
    /// a cache supplies, with no ticket. The backend can release that data at any time.
    /// </summary>
    /// <param name="assetId">The asset.</param>
    EResidencyLevel GetLevel(Guid assetId);

    /// <summary>
    /// Gets the levels of many assets in one call, for a view that reads all of them in each frame.
    /// The default calls <see cref="GetLevel"/> for each asset. A backend with a lock can take it one time.
    /// </summary>
    /// <param name="assetIds">The assets.</param>
    /// <param name="levels">Receives the level of each asset. It must be at least as long as <paramref name="assetIds"/>.</param>
    void GetLevels(ReadOnlySpan<Guid> assetIds, Span<EResidencyLevel> levels)
    {
        for (int i = 0; i < assetIds.Length; i++)
            levels[i] = GetLevel(assetIds[i]);
    }

    /// <summary>
    /// Starts a manifest selection. <see cref="CutsceneResidencySession"/> calls it before it calls <see cref="CanResolve"/>
    /// for the assets of a selection. A backend can refresh its lookup tables one time here. The default does nothing.
    /// </summary>
    void BeginSelection()
    {
    }

    /// <summary>
    /// True if the backend can find the asset now. <see cref="CutsceneResidencySession"/> calls it one time for each asset
    /// when it selects a manifest, to find manifest references that do not match the scene. The default is true.
    /// </summary>
    /// <param name="assetId">The asset.</param>
    bool CanResolve(Guid assetId)
        => true;

    /// <summary>
    /// True while the assets that the backend finds are still loading, for example during a model import.
    /// <see cref="CutsceneResidencySession"/> then keeps a baked manifest whose assets it cannot find yet, and selects the
    /// manifest again when the loading ends. Read it on the update thread. It must not allocate. The default is false.
    /// </summary>
    bool AreAssetsLoading
        => false;

    /// <summary>
    /// True while the memory budget of the backend is under pressure. <see cref="CutsceneResidencySession"/> reads it after
    /// each planner update and then asks the planner to release the T1 and T2 data outside the request window.
    /// Read it on the update thread. It must not allocate. The default is false.
    /// </summary>
    bool IsUnderMemoryPressure
        => false;

    /// <summary>
    /// The frame of the budget evaluation that set <see cref="IsUnderMemoryPressure"/>. The session releases data at most
    /// one time for each new value, so a value that does not change (a stale flag) releases nothing more. The default is 0.
    /// </summary>
    long MemoryPressureFrameId
        => 0L;

    /// <summary>
    /// Sets the planner that receives complete work. <see cref="CutsceneResidencySession"/> calls it one time,
    /// before <see cref="CutsceneResidencyPlanner.Open"/>. The default does nothing.
    /// </summary>
    /// <param name="planner">The planner that uses this backend.</param>
    void AttachPlanner(CutsceneResidencyPlanner planner)
    {
    }

    /// <summary>
    /// Sets the playhead before a planner update, so that the backend can convert request deadlines.
    /// <see cref="CutsceneResidencySession"/> calls it before each planner update. The default does nothing.
    /// </summary>
    /// <param name="position">The playhead position.</param>
    /// <param name="rate">The playback rate. Zero is a pause.</param>
    void SetPlayhead(CutsceneTime position, double rate)
    {
    }
}
