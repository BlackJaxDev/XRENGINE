namespace XREngine.Data;

/// <summary>The state of the Ogg page index of a <see cref="VorbisStreamingDecoder"/>.</summary>
public enum EVorbisSeekIndexState
{
    /// <summary>The decoder does not use an index. A backward seek decodes from the start of the file.</summary>
    Disabled,

    /// <summary>The index is not built yet. The first backward seek builds it.</summary>
    NotBuilt,

    /// <summary>The index is ready. A backward seek starts the decode at the nearest indexed page before the target.</summary>
    Ready,

    /// <summary>
    /// The index or a restart from it failed. The decoder does not use the index again, and a backward seek decodes from
    /// the start of the file. <see cref="VorbisStreamingDecoder.SeekIndexFailure"/> gives the reason.
    /// </summary>
    Failed,
}
