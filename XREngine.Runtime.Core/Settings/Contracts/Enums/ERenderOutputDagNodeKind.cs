namespace XREngine;

public enum ERenderOutputDagNodeKind : byte
{
    Upload,
    Shadow,
    SceneView,
    ComposeMirror,
    Capture,
    ProbeFace,
    GenerateMip,
    OctahedralConversion,
    Irradiance,
    PrefilterMip,
    PostProcess,
    Present,
    Publish,
}
