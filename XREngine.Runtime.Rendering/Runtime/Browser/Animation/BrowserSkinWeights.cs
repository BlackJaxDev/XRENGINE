using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Four explicitly indexed linear-blend influences, including zero-weight lanes.</summary>
public readonly record struct BrowserSkinWeights(int Bone0, int Bone1, int Bone2, int Bone3, Vector4 Weights)
{
    internal void Validate(int boneCount)
    {
        if (Bone0 < 0 || Bone0 >= boneCount || Bone1 < 0 || Bone1 >= boneCount ||
            Bone2 < 0 || Bone2 >= boneCount || Bone3 < 0 || Bone3 >= boneCount ||
            !float.IsFinite(Weights.X) || !float.IsFinite(Weights.Y) || !float.IsFinite(Weights.Z) || !float.IsFinite(Weights.W) ||
            Weights.X is < 0 or > 1 || Weights.Y is < 0 or > 1 || Weights.Z is < 0 or > 1 || Weights.W is < 0 or > 1 ||
            MathF.Abs(Weights.X + Weights.Y + Weights.Z + Weights.W - 1) > 0.0001f)
            throw new ArgumentException("Skinning requires four in-range bone indices and finite nonnegative weights summing to one.");
    }
}
