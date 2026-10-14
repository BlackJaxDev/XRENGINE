namespace XREngine.Rendering.Compute;

/// <summary>
/// Cumulative render-thread time of the GPU physics-chain dispatcher stages, in milliseconds.
/// Compare two captures over the same window and divide by the change in
/// <see cref="Calls"/> to get the cost of each stage per frame. Stages run in sequence;
/// <see cref="TotalMilliseconds"/> includes all of them.
/// </summary>
public readonly record struct PhysicsChainDispatcherStageTimings(
    long Calls,
    double TotalMilliseconds,
    double PollReceiptsMilliseconds,
    double ConfirmSpatialMilliseconds,
    double AcceptInputsMilliseconds,
    double PrepareArenaMilliseconds,
    double SimulationMilliseconds,
    double SelectiveReadbackMilliseconds,
    double CollectBindingsMilliseconds,
    double PaletteMatricesMilliseconds,
    double PreviousPaletteMilliseconds,
    double BoundsMilliseconds,
    double CommitMilliseconds,
    double FinalizeOwnershipMilliseconds,
    double PaletteSourcesMilliseconds,
    double CompleteUpdatesMilliseconds,
    PhysicsChainDispatcherSubStageTimings Detail);
