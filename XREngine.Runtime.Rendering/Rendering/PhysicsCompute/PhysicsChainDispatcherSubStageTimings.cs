namespace XREngine.Rendering.Compute;

/// <summary>
/// Cumulative render-thread milliseconds of the parts of the bounds, simulation, and commit
/// stages of the GPU physics-chain dispatcher, and two cumulative commit counts. Each time
/// value is included in its parent stage in <see cref="PhysicsChainDispatcherStageTimings"/>.
/// </summary>
/// <param name="BoundsRoutesMilliseconds">Bounds stage: the capture of the scene routes.</param>
/// <param name="BoundsRowsMilliseconds">
/// Bounds stage: the bounds work rows and the renderer state rows of the output page.
/// </param>
/// <param name="BoundsSceneCopyMilliseconds">Bounds stage: the bounds copy into each scene.</param>
/// <param name="SimTreeParamsMilliseconds">Simulation stage: the tree parameter rows.</param>
/// <param name="SimActiveWorkMilliseconds">
/// Simulation stage: the active work records and the capacity checks.
/// </param>
/// <param name="SimUploadRangesMilliseconds">Simulation stage: the dirty request range uploads.</param>
/// <param name="SimUploadCollidersMilliseconds">Simulation stage: the collider upload.</param>
/// <param name="SimUploadOtherMilliseconds">
/// Simulation stage: the transform, tree header, and active work uploads, and the input
/// publication pass.
/// </param>
/// <param name="CommitBackendBatchMilliseconds">
/// Commit stage: the backend batch commit. On Vulkan, this moves the captured frame operations
/// into the pending stream. It is not a native queue submission.
/// </param>
/// <param name="CommitPageLockWaitMilliseconds">
/// Commit stage: the wait to get the output page lock. Other threads hold this lock for page
/// leases and bounds queries.
/// </param>
/// <param name="CommitFenceMilliseconds">Commit stage: the producer fence insertion.</param>
/// <param name="CommitPageStateMilliseconds">
/// Commit stage: the output page state update. This includes the fence result check, the epoch
/// and page index update, the bounds source compare and copy, and the palette input map swap.
/// </param>
/// <param name="CommitCollectNotificationsMilliseconds">
/// Commit stage: the collection of renderer notifications, the spatial epoch advance, and the
/// lock release.
/// </param>
/// <param name="CommitNotifyMilliseconds">
/// Commit stage: the notifications to each renderer after the lock release.
/// </param>
/// <param name="CommitOtherMilliseconds">
/// The commit stage minus the six commit parts. It must be near zero. A failed output page
/// commit adds to the parts but not to the commit stage, so it can make this value negative.
/// </param>
/// <param name="CommittedOutputPages">The number of successful output page commits.</param>
/// <param name="CommitNotifications">
/// The number of renderer notifications that successful output page commits sent.
/// </param>
/// <param name="BoundsRowReuses">The number of bounds builds that reused every retained row.</param>
/// <param name="BoundsRowRebuilds">The number of bounds builds that rebuilt every row.</param>
public readonly record struct PhysicsChainDispatcherSubStageTimings(
    double BoundsRoutesMilliseconds,
    double BoundsRowsMilliseconds,
    double BoundsSceneCopyMilliseconds,
    double SimTreeParamsMilliseconds,
    double SimActiveWorkMilliseconds,
    double SimUploadRangesMilliseconds,
    double SimUploadCollidersMilliseconds,
    double SimUploadOtherMilliseconds,
    double CommitBackendBatchMilliseconds,
    double CommitPageLockWaitMilliseconds,
    double CommitFenceMilliseconds,
    double CommitPageStateMilliseconds,
    double CommitCollectNotificationsMilliseconds,
    double CommitNotifyMilliseconds,
    double CommitOtherMilliseconds,
    long CommittedOutputPages,
    long CommitNotifications,
    long BoundsRowReuses,
    long BoundsRowRebuilds);
