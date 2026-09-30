using System.ComponentModel;
using System.Numerics;

namespace XREngine;

public partial class UserSettings
{
    private Dictionary<string, Vector3> _vrControllerWristOffsets = new(StringComparer.Ordinal);
    [Category("VR Calibration")]
    [Description("Grip-to-wrist translation in meters, keyed by OpenXR interaction-profile path. Empty uses an unqualified neutral palm estimate; validate on hardware.")]
    public Dictionary<string, Vector3> VrControllerWristOffsets
    {
        get => _vrControllerWristOffsets;
        set => SetField(ref _vrControllerWristOffsets, value ?? new(StringComparer.Ordinal));
    }
}
