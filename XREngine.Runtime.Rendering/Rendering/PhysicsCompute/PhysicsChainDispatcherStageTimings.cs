namespace XREngine.Rendering.Compute;

/// <summary>
/// Cumulative render-thread time of the GPU physics-chain dispatcher stages, in milliseconds.
/// Compare two captures over the same window and divide by the change in
/// <see cref="Calls"/> to get the cost of each stage per frame. Stages run in sequence;
/// <see cref="TotalMilliseconds"/> includes all of them.
/// </summary>
/// <param name="PreviousPaletteSingleCopies">
/// The number of previous palette publications that copied the whole palette layout with one
/// range copy, because the layout and every row's history source were unchanged.
/// </param>
/// <param name="PaletteSourceRebinds">
/// The number of palette source publications that bound the renderers to the shared palette
/// view again. Other publications only write the new buffers into the view.
/// </param>
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
    long PreviousPaletteSingleCopies,
    long PaletteSourceRebinds,
    PhysicsChainDispatcherSubStageTimings Detail);
