namespace XREngine.Rendering.Commands;

/// <summary>
/// Names the reason why a publication with moved rows ran the whole-scene plan instead of the
/// moved-row patch of the unchanged-row gate.
/// </summary>
public enum EAdvancedMovedRowPatchFallback : byte
{
    /// <summary>
    /// A row moved, but its planned draw row is not a dynamic shadow caster. A static row that
    /// starts to move takes the whole-scene plan once; the swap after its promotion carries the
    /// dynamic flag.
    /// </summary>
    StaticRow,

    /// <summary>More rows moved than <c>AdvancedGpuScenePublisher.MovedRowPatchLimit</c>.</summary>
    LimitExceeded,

    /// <summary>The publisher has no retained submission rows to commit with the patch.</summary>
    NoRetainedRows,

    /// <summary>A transform or instance table cannot take the replacements of the patch in this publication.</summary>
    TableCapacity,

    /// <summary>A moved row's transform or instance record is not current.</summary>
    RegistrationNotCurrent,

    /// <summary>The number of reasons. Not a reason.</summary>
    Count,
}
