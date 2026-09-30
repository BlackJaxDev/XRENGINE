using XREngine.Components.VR;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap.Builders;

/// <summary>Attaches eye-visible calibration feedback without depending on spectator enablement.</summary>
public static class BootstrapVrCalibrationFeedbackFactory
{
    public static VrCalibrationFeedbackComponent Create(SceneNode parent, VRPlayerCharacterComponent player)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(player);
        SceneNode root = parent.NewChild("VR Calibration Feedback");
        VrCalibrationFeedbackComponent feedback = root.AddComponent<VrCalibrationFeedbackComponent>()!;
        feedback.Player = player;
        return feedback;
    }
}
