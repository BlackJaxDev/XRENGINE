using MemoryPack;
using System.Numerics;

namespace XREngine.Scene;

/// <summary>
/// Stores linear diffuse SH in the packed Unity L0, L1, and L2 basis.
/// Coefficients are signed. C.W is unused.
/// </summary>
[MemoryPackable]
public readonly partial record struct AmbientSphericalHarmonics(
    Vector4 Ar, Vector4 Ag, Vector4 Ab,
    Vector4 Br, Vector4 Bg, Vector4 Bb, Vector4 C)
{
    /// <summary>Reports whether every coefficient is finite.</summary>
    [MemoryPackIgnore]
    public bool IsFinite => Finite(Ar) && Finite(Ag) && Finite(Ab) &&
        Finite(Br) && Finite(Bg) && Finite(Bb) && Finite(C);

    /// <summary>Creates an isotropic ambient input without directional terms.</summary>
    public static AmbientSphericalHarmonics Constant(Vector3 color) => new(
        new(0, 0, 0, color.X), new(0, 0, 0, color.Y), new(0, 0, 0, color.Z),
        Vector4.Zero, Vector4.Zero, Vector4.Zero, Vector4.Zero);

    /// <summary>Interpolates all coefficients without removing signed terms.</summary>
    public static AmbientSphericalHarmonics Lerp(
        in AmbientSphericalHarmonics from, in AmbientSphericalHarmonics to, float amount) => new(
        Vector4.Lerp(from.Ar, to.Ar, amount), Vector4.Lerp(from.Ag, to.Ag, amount),
        Vector4.Lerp(from.Ab, to.Ab, amount), Vector4.Lerp(from.Br, to.Br, amount),
        Vector4.Lerp(from.Bg, to.Bg, amount), Vector4.Lerp(from.Bb, to.Bb, amount),
        Vector4.Lerp(from.C, to.C, amount));

    private static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
}
