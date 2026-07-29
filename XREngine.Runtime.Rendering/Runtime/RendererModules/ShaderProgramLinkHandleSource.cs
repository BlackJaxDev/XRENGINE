namespace XREngine.Rendering;

/// <summary>
/// Identifies how a backend owns the native handle used by a linked shader program.
/// </summary>
public enum ShaderProgramLinkHandleSource
{
    None,
    OwnedSource,
    OwnedBinary,
    SharedLinkedProgram,
}
