namespace XREngine.Browser.Diagnostics;

/// <summary>Selects the authored sky inputs used by the engine diagnostic.</summary>
internal enum EngineSkyDiagnosticProfile
{
    Gradient,
    Procedural,
    Sun,
    Cubemap,
    Equirectangular,
    Octahedral,
    FilteredEquirectangular,
    FilteredOctahedral,
}
