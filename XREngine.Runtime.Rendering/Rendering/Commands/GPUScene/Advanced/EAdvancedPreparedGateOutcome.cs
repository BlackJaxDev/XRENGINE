namespace XREngine.Rendering.Commands;

/// <summary>
/// Identifies the result of one prepare or one consume of the prepared
/// unchanged-row verdict of the Advanced resident-scene publication.
/// </summary>
/// <remarks>
/// The collect thread prepares the verdict after visibility collection. The
/// next publication in the serial swap consumes it. A refusal or a miss
/// gives the reason. The reason also names the witness that detected the
/// change, so a verify disagreement uses the same values.
/// </remarks>
public enum EAdvancedPreparedGateOutcome : byte
{
    /// <summary>No result.</summary>
    None = 0,

    /// <summary>The prepare stored a verdict.</summary>
    Prepared,

    /// <summary>The consume accepted the verdict, and the swap skipped the row walk.</summary>
    Hit,

    /// <summary>The prepare threw an exception. The swap-time gate decides for this publication.</summary>
    Exception,

    /// <summary>The publication found no verdict. The prepare refused, or it did not run.</summary>
    NotPrepared,

    /// <summary>The consume ran on a thread other than the thread of the prepare.</summary>
    OtherThread,

    /// <summary>A publication ran during the prepare, or after the prepare and before the consume.</summary>
    PublisherChanged,

    /// <summary>The retained plan does not describe the current publication.</summary>
    BaselineInvalid,

    /// <summary>No publication stamped a registered row since the last rejection.</summary>
    NoStampedFrame,

    /// <summary>
    /// The boundary requirement of the resident tables changes at the next frame boundary. The next publication
    /// then skips the unchanged-row gate.
    /// </summary>
    BoundaryGrowth,

    /// <summary>The verdict storage is smaller than the row count.</summary>
    Capacity,

    /// <summary>
    /// A scene row, the source lookup, the command count, or a command render
    /// snapshot changed after the last swap copy.
    /// </summary>
    RowInputsChanged,

    /// <summary>The scene command count is not the row count of the verdict.</summary>
    RowCountChanged,

    /// <summary>The publication frame does not follow the frame of the last row stamp.</summary>
    FrameGap,

    /// <summary>The render thread wrote a late world matrix of a mesh command.</summary>
    LateWorldWrite,

    /// <summary>A renderer changed its bone-set generation or its GPU coverage.</summary>
    BoneStateChanged,

    /// <summary>
    /// A mesh changed a compared geometry value: the vertex count, the index
    /// count, the geometry revision, the primitive type, or the triangle count.
    /// </summary>
    GeometryChanged,

    /// <summary>The skinning settings changed.</summary>
    SettingsChanged,

    /// <summary>A command changed its renderer, submesh, mesh, or material reference.</summary>
    CommandChanged,

    /// <summary>A material changed a version, its texture use, or its committed header.</summary>
    MaterialChanged,

    /// <summary>A raster option of a row changed.</summary>
    RenderOptionsChanged,

    /// <summary>A registration or a retained identity no longer matches its plan.</summary>
    RegistrationChanged,

    /// <summary>An unregistered row no longer gives the same rejection.</summary>
    UnsupportedRowChanged,

    /// <summary>
    /// The registration count or the committed bounds epoch of the covered rows changed.
    /// </summary>
    BoundsRouteChanged,

    /// <summary>The number of values. This value is not a result.</summary>
    Count,
}
