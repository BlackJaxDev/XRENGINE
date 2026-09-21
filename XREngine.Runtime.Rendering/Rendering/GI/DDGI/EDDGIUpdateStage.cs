namespace XREngine.Rendering.GI.DDGI;

/// <summary>Ordered completion markers for one DDGI GPU update submission.</summary>
internal enum EDDGIUpdateStage
{
    None,
    Prepared,
    Rays,
    Hits,
    Radiance,
    Relocation,
    Irradiance,
    Visibility,
    Complete,
}
