using XREngine.Components;

namespace XREngine.Components.Animation;

/// <summary>
/// Carries an authored avatar T-pose from an importer to <see cref="HumanoidComponent"/>.
/// </summary>
/// <remarks>
/// The T-pose defines the humanoid reference pose only. It never changes the hierarchy or the skin bind pose.
/// </remarks>
public sealed class HumanoidAuthoredTPoseComponent : XRComponent
{
    private HumanoidAuthoredTPoseBone[] _bones = [];

    /// <summary>The authored transforms. An empty list selects the generated reference pose.</summary>
    public HumanoidAuthoredTPoseBone[] Bones
    {
        get => _bones;
        set => SetField(ref _bones, value ?? []);
    }
}
