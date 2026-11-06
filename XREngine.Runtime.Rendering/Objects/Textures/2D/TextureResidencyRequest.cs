namespace XREngine.Rendering;

/// <summary>
/// One explicit residency request of <see cref="ImportedTextureStreamingManager"/>.
/// The manager changes the mutable fields only under its request lock.
/// </summary>
internal sealed class TextureResidencyRequest(
    long id,
    ImportedTextureStreamingRecord record,
    int targetMip,
    long deadlineFrameId,
    JobPriority priority,
    int generation)
{
    /// <summary>The ticket identifier.</summary>
    public readonly long Id = id;

    /// <summary>The streaming record of the texture. The request keeps the record alive; the record holds the texture weakly.</summary>
    public readonly ImportedTextureStreamingRecord Record = record;

    /// <summary>The requested mip. 0 is the full source size. Each step halves the largest side.</summary>
    public readonly int TargetMip = targetMip;

    /// <summary>The collect frame at which the caller needs the mip. Work at or after this frame gets the highest priority.</summary>
    public readonly long DeadlineFrameId = deadlineFrameId;

    /// <summary>The job priority that the caller gives the request.</summary>
    public readonly JobPriority Priority = priority;

    /// <summary>The caller generation. The rejection and cancel logs show it; the manager does not compare it.</summary>
    public readonly int Generation = generation;

    /// <summary>The current state.</summary>
    public ETextureResidencyRequestState State = ETextureResidencyRequestState.Pending;

    /// <summary>The largest side, in texels, that the request needs. Zero until the source size is known.</summary>
    public uint RequestedMaxDimension;

    /// <summary>The index of the request in the active list of the manager.</summary>
    public int ListIndex = -1;
}
