namespace XREngine.Scene.Importers;

/// <summary>Distinguishes a named source GrabPass from an optimized shader that omits it.</summary>
internal enum SourceGrabShaderShape
{
    Unknown,
    Absent,
    Named,
}
