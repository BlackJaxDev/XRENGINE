namespace XREngine.Rendering.Shadows;

/// <summary>
/// A diagnostic read of the shadow-only caster census of one <see cref="XREngine.Scene.VisualScene3D"/>.
/// </summary>
/// <remarks>
/// The values come from separate reads while collections can run, so they are not one atomic frame state.
/// </remarks>
/// <param name="IsGpuDispatchActive">Whether the scene uses GPU dispatch. Without it the census cannot see casters.</param>
/// <param name="PreviousCandidates">The candidates that the main views of the previous collect pass counted.</param>
/// <param name="CurrentCandidates">The candidates that the main views of the current collect pass counted so far.</param>
/// <param name="PreviousMainViewGpuCollections">The main-view GPU collections that completed in the previous pass.</param>
/// <param name="CurrentMainViewGpuCollections">The main-view GPU collections that completed in the current pass so far.</param>
/// <param name="ShadowCasterMembershipRevision">The current shadow caster membership revision of the scene.</param>
/// <param name="MembershipRevisionAtPassStart">The membership revision at the start of the current pass.</param>
/// <param name="MayHaveShadowOnlyCasters">The current result of the census test that the atlas planning reads.</param>
/// <param name="CapturePending">Whether a candidate capture waits for a main-view collection.</param>
public readonly record struct ShadowOnlyCasterCensusSnapshot(
    bool IsGpuDispatchActive,
    int PreviousCandidates,
    int CurrentCandidates,
    int PreviousMainViewGpuCollections,
    int CurrentMainViewGpuCollections,
    ulong ShadowCasterMembershipRevision,
    ulong MembershipRevisionAtPassStart,
    bool MayHaveShadowOnlyCasters,
    bool CapturePending);
