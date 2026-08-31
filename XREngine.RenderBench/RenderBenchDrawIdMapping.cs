namespace XREngine.RenderBench;

/// <summary>
/// Cold-path classification of one GPU DrawID. A record is emitted for every
/// raw early or late entry so candidate filtering cannot hide an unexpected
/// scene command.
/// </summary>
public sealed record RenderBenchDrawIdMapping
{
    public uint DrawId { get; init; }
    public int? CandidateId { get; init; }
    public bool IsKnownOccluder { get; init; }
}
