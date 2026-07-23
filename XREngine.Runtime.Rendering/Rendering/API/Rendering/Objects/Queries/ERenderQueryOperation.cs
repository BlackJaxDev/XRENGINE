namespace XREngine.Rendering;

/// <summary>
/// Identifies a command-recording operation independently of query family.
/// </summary>
public enum ERenderQueryOperation
{
    Reset,
    Begin,
    End,
    WriteTimestamp,
    WriteProperties,
    CopyResults,
}
