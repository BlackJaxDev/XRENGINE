namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Selects the runtime-facing subsystem adapters installed by an application composition root.
/// </summary>
[Flags]
public enum RuntimeAdapterProfile
{
    None = 0,
    Animation = 1 << 0,
    Audio = 1 << 1,
    Input = 1 << 2,
    Modeling = 1 << 3,
    All = Animation | Audio | Input | Modeling,
}
