using System.Numerics;

namespace XREngine.Components;

public interface IRuntimeVrHeightScaleComponent
{
    Vector3 ScaledToRealWorldEyeOffsetFromHead { get; }
    void MeasureAvatarHeight();
    void CalculateEyeOffsetFromHead(XRComponent? eyesModelComponent, string? eyeLBoneName, string? eyeRBoneName, bool forceXToZero = true);
}