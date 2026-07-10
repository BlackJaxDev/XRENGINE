namespace XREngine;

public enum ERenderOutputPriority
{
    Critical = 0,
    RequiredDependency = 1,
    Interactive = 2,
    VisibleAuxiliary = 3,
    Background = 4,
    Diagnostic = 5,
}
