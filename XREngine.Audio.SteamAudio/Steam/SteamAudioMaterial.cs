using System.Numerics;

namespace XREngine.Audio.Steam;

/// <summary>
/// Defines acoustic material properties for Steam Audio scene geometry.
/// <para>
/// Acoustic materials describe how surfaces interact with sound. Each property is specified
/// as a 3-band value (low / mid / high frequency) to capture frequency-dependent behavior.
/// </para>
/// </summary>
public sealed class SteamAudioMaterial
{
    /// <summary>
    /// Per-frequency-band absorption coefficients (0–1). Higher values mean the surface absorbs
    /// more sound at that frequency band.
    /// <para>Default: (0.1, 0.1, 0.1) — low absorption (e.g., concrete, brick).</para>
    /// </summary>
    public Vector3 Absorption { get; set; } = new(0.1f, 0.1f, 0.1f);

    /// <summary>
    /// Scattering coefficient (0–1). Higher values scatter sound more diffusely.
    /// <para>Default: 0.5 — moderate scattering.</para>
    /// </summary>
    public float Scattering { get; set; } = 0.5f;

    /// <summary>
    /// Per-frequency-band transmission coefficients (0–1). Higher values allow more sound
    /// to pass through the surface.
    /// <para>Default: (0.1, 0.1, 0.1) — low transmission (e.g., thick wall).</para>
    /// </summary>
    public Vector3 Transmission { get; set; } = new(0.1f, 0.1f, 0.1f);

    /// <summary>
    /// Converts this managed material to the native <see cref="IPLMaterial"/> struct
    /// expected by the Phonon API.
    /// </summary>
    public IPLMaterial ToIPL() => new()
    {
        absorption = [Absorption.X, Absorption.Y, Absorption.Z],
        scattering = Scattering,
        transmission = [Transmission.X, Transmission.Y, Transmission.Z],
    };

    // --- Common presets ---

    /// <summary>Default material for geometry with no explicit acoustic material assigned.</summary>
    public static SteamAudioMaterial Default { get; } = new();

    /// <summary>Concrete / brick — low absorption, moderate scattering, very low transmission.</summary>
    public static SteamAudioMaterial Concrete { get; } = new()
    {
        Absorption = new(0.05f, 0.07f, 0.08f),
        Scattering = 0.05f,
        Transmission = new(0.015f, 0.011f, 0.01f),
    };

    /// <summary>Wood (thin panels) — moderate absorption, moderate scattering.</summary>
    public static SteamAudioMaterial Wood { get; } = new()
    {
        Absorption = new(0.11f, 0.07f, 0.06f),
        Scattering = 0.20f,
        Transmission = new(0.070f, 0.014f, 0.005f),
    };

    /// <summary>Glass — low absorption, low scattering, moderate transmission.</summary>
    public static SteamAudioMaterial Glass { get; } = new()
    {
        Absorption = new(0.06f, 0.03f, 0.02f),
        Scattering = 0.05f,
        Transmission = new(0.060f, 0.044f, 0.011f),
    };

    /// <summary>Metal — very low absorption, high scattering, very low transmission.</summary>
    public static SteamAudioMaterial Metal { get; } = new()
    {
        Absorption = new(0.20f, 0.07f, 0.06f),
        Scattering = 0.05f,
        Transmission = new(0.010f, 0.005f, 0.002f),
    };

    /// <summary>Carpet / soft furnishing — high absorption, moderate scattering.</summary>
    public static SteamAudioMaterial Carpet { get; } = new()
    {
        Absorption = new(0.24f, 0.69f, 0.73f),
        Scattering = 0.50f,
        Transmission = new(0.020f, 0.005f, 0.003f),
    };

    /// <summary>Outdoor ground / dirt — moderate absorption, moderate scattering, no transmission.</summary>
    public static SteamAudioMaterial Dirt { get; } = new()
    {
        Absorption = new(0.15f, 0.25f, 0.40f),
        Scattering = 0.60f,
        Transmission = new(0.0f, 0.0f, 0.0f),
    };
}
