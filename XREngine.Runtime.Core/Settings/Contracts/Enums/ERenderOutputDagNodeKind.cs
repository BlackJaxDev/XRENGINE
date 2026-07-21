namespace XREngine;

public enum ERenderOutputDagNodeKind : byte
{
    SceneView,
    ComposeMirror,
    Capture,
    ProbeFace,
    GenerateMip,
    OctahedralConversion,
    Irradiance,
    PrefilterMip,
    PostProcess,
    Publish,
}
