namespace XREngine.Rendering;

/// <summary>
/// The first reason why a backend could not keep its sealed visibility stream
/// for a frame and sealed it again.
/// </summary>
public enum EAdvancedVisibilityStreamReuseMiss
{
    /// <summary>The stream was kept.</summary>
    None = 0,
    /// <summary>The stream holds no sealed content from an earlier frame.</summary>
    NoRetainedStream = 1,
    /// <summary>A visibility column (payloads, producers, slices, ranges, or range order) changed.</summary>
    Columns = 2,
    /// <summary>The database epoch or the draw or geometry table generations changed.</summary>
    Publication = 3,
    /// <summary>The resident static vertex or index slice changed.</summary>
    SceneGeometry = 4,
    /// <summary>The pass index or the view mask changed.</summary>
    Pass = 5,
    /// <summary>The frame-operation context of the bin keys changed.</summary>
    Context = 6,
    /// <summary>The submission strategy, lane capabilities, output policy, or diagnostic plan changed.</summary>
    Submission = 7,
    /// <summary>The deformation output of this frame cannot bind the kept records.</summary>
    DeformationOutput = 8,
}
