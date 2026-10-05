using System.Numerics;

namespace XREngine.Components;

public interface IRuntimeVrHeightScaleComponent
{
    Vector3 ScaledToRealWorldEyeOffsetFromHead { get; }
    bool TryApplyPlayerMeasurements(out string notice)
    {
        notice = VrBodyScaleCalculation.UnsetNotice;
        return false;
    }
    string? GetCaptureMeasurementWarning(float trackedEyeHeightMeters) => null;
    void MeasureAvatarHeight();
    void CalculateEyeOffsetFromHead(XRComponent? eyesModelComponent, string? eyeLBoneName, string? eyeRBoneName, bool forceXToZero = true);
}
