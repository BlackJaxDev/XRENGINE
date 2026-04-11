namespace XREngine.Rendering;

public enum ETonemappingType
{
    /// <summary>
    /// Linear tonemapping, which applies exposure without any dynamic-range compression.
    /// This preserves the HDR response shape and can still exceed the displayable range.
    /// Algorithm: output = input * exposure
    /// </summary>
    Linear,
    /// <summary>
    /// Gamma tonemapping, which applies exposure first and then gamma correction without highlight compression.
    /// This changes the perceptual response but does not perform true HDR range compression.
    /// Algorithm: output = pow(input * exposure, 1 / gamma)
    /// </summary>
    Gamma,
    /// <summary>
    /// Clip tonemapping, which applies exposure and then hard-clamps the result into the displayable range.
    /// This is simple but tends to lose highlight detail abruptly.
    /// Algorithm: output = clamp(input * exposure, 0, 1)
    /// </summary>
    Clip,
    /// <summary>
    /// Reinhard tonemapping, which applies exposure and then compresses the range with a simple rational curve.
    /// This gives a soft highlight rolloff with inexpensive math.
    /// Algorithm: let x = input * exposure; output = x / (x + 1)
    /// </summary>
    Reinhard,
    /// <summary>
    /// Hable tonemapping, which uses the Uncharted 2-style filmic curve after exposure.
    /// This favors a cinematic shoulder and midtone response.
    /// Algorithm: let x = max(input * exposure - E, 0); output = ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F
    /// </summary>
    Hable,
    /// <summary>
    /// Mobius tonemapping, which applies exposure and then a Mobius-style rational transform.
    /// This produces a smooth highlight rolloff with a configurable transition parameter.
    /// Algorithm: let x = input * exposure; output = (x * (a + 1)) / (x + a)
    /// </summary>
    Mobius,
    /// <summary>
    /// ACES tonemapping, which uses the fitted ACES filmic approximation after exposure.
    /// This is the classic fast ACES curve used in many real-time pipelines.
    /// Algorithm: let x = input * exposure; output = (x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14)
    /// </summary>
    ACES,
    /// <summary>
    /// Neutral tonemapping, which uses the current neutral rational curve after exposure.
    /// Algorithm: let x = input * exposure; output = (x * (x + 0.0245786)) / (x * (0.983729 * x + 0.432951) + 0.238081)
    /// </summary>
    Neutral,
    /// <summary>
    /// Filmic tonemapping, which uses a classic ALU-friendly filmic curve after exposure.
    /// This favors a strong shoulder and differs intentionally from the Neutral option.
    /// Algorithm: let x = max(input * exposure - 0.004, 0); output = (x * (6.2 * x + 0.5)) / (x * (6.2 * x + 1.7) + 0.06)
    /// </summary>
    Filmic
}
