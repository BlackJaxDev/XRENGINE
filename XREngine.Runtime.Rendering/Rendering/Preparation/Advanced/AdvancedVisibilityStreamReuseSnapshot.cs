namespace XREngine.Rendering;

/// <summary>
/// Cumulative counts of sealed visibility stream reuse across frames. Read two
/// captures inside a window and compare the differences.
/// </summary>
/// <param name="Reuses">Seals that kept the stream of an earlier frame.</param>
/// <param name="Reseals">Seals that built the stream again.</param>
/// <param name="NoRetainedStream">Reseals because the stream held no earlier sealed content.</param>
/// <param name="ColumnMisses">Reseals because a visibility column changed.</param>
/// <param name="PublicationMisses">Reseals because the database epoch or a draw or geometry table generation changed.</param>
/// <param name="SceneGeometryMisses">Reseals because a resident static vertex or index slice changed.</param>
/// <param name="PassMisses">Reseals because the pass index or view mask changed.</param>
/// <param name="ContextMisses">Reseals because the frame-operation context changed.</param>
/// <param name="SubmissionMisses">Reseals because the submission strategy, capabilities, output policy, or diagnostic plan changed.</param>
/// <param name="DeformationOutputMisses">Reseals because the deformation output could not bind the kept records.</param>
public readonly record struct AdvancedVisibilityStreamReuseSnapshot(
    long Reuses,
    long Reseals,
    long NoRetainedStream,
    long ColumnMisses,
    long PublicationMisses,
    long SceneGeometryMisses,
    long PassMisses,
    long ContextMisses,
    long SubmissionMisses,
    long DeformationOutputMisses);
