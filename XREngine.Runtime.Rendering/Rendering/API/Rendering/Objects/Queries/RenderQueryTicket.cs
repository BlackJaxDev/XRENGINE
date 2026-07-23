namespace XREngine.Rendering;

/// <summary>
/// Identifies one recorded query use and the submission that owns its result.
/// </summary>
public readonly record struct RenderQueryTicket(
    ulong Epoch,
    uint PoolIdentity,
    uint FirstQuery,
    uint QueryCount,
    ulong SubmissionValue)
{
    public bool IsValid => Epoch != 0ul && PoolIdentity != 0u && QueryCount != 0u;
}
