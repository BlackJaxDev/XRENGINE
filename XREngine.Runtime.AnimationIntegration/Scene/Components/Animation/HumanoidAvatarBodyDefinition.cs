namespace XREngine.Components.Animation;

/// <summary>
/// Serialized model-root body-frame definition. Segment data is authored
/// explicitly; this type intentionally supplies no inferred mass distribution.
/// </summary>
public sealed class HumanoidAvatarBodyDefinition
{
    public const int CurrentAlgorithmVersion = 1;

    /// <summary>Version of the body-frame derivation contract used to author this definition.</summary>
    public int AlgorithmVersion { get; set; } = CurrentAlgorithmVersion;

    /// <summary>Stable identity of the model this definition describes.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Explicit body segments. An empty collection is invalid at compilation time.</summary>
    public HumanoidAvatarBodySegment[] Segments { get; set; } = [];

    /// <summary>Left hip orientation landmark.</summary>
    public HumanoidAvatarBodyPoint LeftHip { get; set; } = new()
    {
        Role = EHumanoidAvatarBoneRole.LeftUpperLeg,
    };

    /// <summary>Right hip orientation landmark.</summary>
    public HumanoidAvatarBodyPoint RightHip { get; set; } = new()
    {
        Role = EHumanoidAvatarBoneRole.RightUpperLeg,
    };

    /// <summary>Left shoulder orientation landmark.</summary>
    public HumanoidAvatarBodyPoint LeftShoulder { get; set; } = new()
    {
        Role = EHumanoidAvatarBoneRole.LeftUpperArm,
    };

    /// <summary>Right shoulder orientation landmark.</summary>
    public HumanoidAvatarBodyPoint RightShoulder { get; set; } = new()
    {
        Role = EHumanoidAvatarBoneRole.RightUpperArm,
    };

    /// <summary>Non-negative contribution of the hip width to body side direction.</summary>
    public float HipOrientationWeight { get; set; } = 1.0f;

    /// <summary>Non-negative contribution of the shoulder width to body side direction.</summary>
    public float ShoulderOrientationWeight { get; set; } = 1.0f;
}
