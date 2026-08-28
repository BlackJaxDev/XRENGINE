namespace XREngine.Animation.Importers;

/// <summary>
/// Public, versioned declaration of the Unity YAML AnimationClip source contract.
/// Importers must reject fields outside this contract instead of silently
/// approximating or discarding their behavior.
/// </summary>
public static class ImportedAnimationImportCapabilityContract
{
    public const int CurrentVersion = 1;
    public const string SourceFormat = "UnityYamlAnimationClip";

    private static readonly int[] SerializedVersions = [6, 7];
    private static readonly int[] InfinityModes = [0, 1, 2, 4, 8];
    private static readonly int[] WeightedModes = [0, 1, 2, 3];
    private static readonly EImportedAnimationWrapMode[] WrapModes =
    [
        EImportedAnimationWrapMode.Default,
        EImportedAnimationWrapMode.Once,
        EImportedAnimationWrapMode.Loop,
        EImportedAnimationWrapMode.PingPong,
        EImportedAnimationWrapMode.ClampForever,
    ];
    private static readonly EImportedAnimationBindingValueKind[] BindingValueKinds =
    [
        EImportedAnimationBindingValueKind.Float,
        EImportedAnimationBindingValueKind.Integer,
        EImportedAnimationBindingValueKind.Boolean,
        EImportedAnimationBindingValueKind.Enum,
        EImportedAnimationBindingValueKind.Vector2,
        EImportedAnimationBindingValueKind.Vector3,
        EImportedAnimationBindingValueKind.Vector4,
        EImportedAnimationBindingValueKind.Quaternion,
        EImportedAnimationBindingValueKind.Euler,
        EImportedAnimationBindingValueKind.ObjectReference,
    ];
    private static readonly string[] CurveFamilies =
    [
        "m_RotationCurves",
        "m_CompressedRotationCurves",
        "m_EulerCurves",
        "m_PositionCurves",
        "m_ScaleCurves",
        "m_FloatCurves",
        "m_PPtrCurves",
        "m_MuscleClip.m_Clip.m_StreamedClip",
        "m_MuscleClip.m_Clip.m_DenseClip",
        "m_MuscleClip.m_Clip.m_ConstantClip",
    ];

    /// <summary>
    /// Unity YAML AnimationClip schema revisions covered by this contract.
    /// A version is accepted only when every source feature it actually uses is
    /// also listed by this capability contract.
    /// </summary>
    public static IReadOnlyList<int> SupportedSerializedVersions => SerializedVersions;

    public static IReadOnlyList<int> SupportedInfinityModes => InfinityModes;

    public static IReadOnlyList<int> SupportedWeightedModes => WeightedModes;

    public static IReadOnlyList<EImportedAnimationWrapMode> SupportedWrapModes => WrapModes;

    public static IReadOnlyList<EImportedAnimationBindingValueKind> SupportedBindingValueKinds => BindingValueKinds;

    public static IReadOnlyList<string> ExecutableCurveFamilies => CurveFamilies;

    public static bool SupportsSerializedVersion(int version)
    {
        for (int i = 0; i < SerializedVersions.Length; i++)
            if (SerializedVersions[i] == version)
                return true;
        return false;
    }
}
