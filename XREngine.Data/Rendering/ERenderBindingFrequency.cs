namespace XREngine.Data.Rendering;

/// <summary>
/// Declares the owner whose content generation controls publication of a
/// renderer binding value.
/// </summary>
public enum ERenderBindingFrequency : byte
{
    Unknown = 0,
    Frame,
    View,
    Pass,
    Material,
    Object,
    Instance,
    RuntimeCallback,
    Count,
}
