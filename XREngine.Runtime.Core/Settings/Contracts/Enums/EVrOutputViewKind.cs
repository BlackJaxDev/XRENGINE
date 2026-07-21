namespace XREngine;

public enum EVrOutputViewKind
{
    LeftEye,
    RightEye,
    DesktopEditor,
    CyclopeanDesktop,
    LeftWide,
    RightWide,
    LeftInset,
    RightInset,
    /// <summary>
    /// A published or secondary scene output with its own durable history owner.
    /// </summary>
    Secondary,
    Debug,
}
