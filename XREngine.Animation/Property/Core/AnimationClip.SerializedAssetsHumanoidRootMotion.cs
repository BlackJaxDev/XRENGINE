using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private ImportedHumanoidClipRootMotionSettings? _importedHumanoidRootMotionSettings;

    /// <summary>
    /// Unity humanoid Body-to-root projection metadata retained from the source
    /// <c>.anim</c> file. A null value means the clip was not imported from a
    /// Unity humanoid clip or the source did not contain these settings.
    /// </summary>
    public ImportedHumanoidClipRootMotionSettings? ImportedHumanoidRootMotionSettings
    {
        get => _importedHumanoidRootMotionSettings;
        set => SetField(ref _importedHumanoidRootMotionSettings, value);
    }
}
