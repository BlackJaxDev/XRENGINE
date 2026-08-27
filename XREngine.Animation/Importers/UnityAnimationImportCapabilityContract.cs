namespace XREngine.Animation.Importers;

/// <summary>
/// Public, versioned declaration of the Unity YAML AnimationClip source contract.
/// Importers must reject fields outside this contract instead of silently
/// approximating or discarding their behavior.
/// </summary>
public static class UnityAnimationImportCapabilityContract
{
    public const int CurrentVersion = 1;
    public const string SourceFormat = "UnityYamlAnimationClip";

    private static readonly int[] SerializedVersions = [6, 7];
    private static readonly int[] InfinityModes = [0, 1, 2, 4, 8];
    private static readonly int[] WeightedModes = [0, 1, 2, 3];
    private static readonly EUnityAnimationWrapMode[] WrapModes =
    [
        EUnityAnimationWrapMode.Default,
        EUnityAnimationWrapMode.Once,
        EUnityAnimationWrapMode.Loop,
        EUnityAnimationWrapMode.PingPong,
        EUnityAnimationWrapMode.ClampForever,
    ];
    private static readonly EUnityAnimationBindingValueKind[] BindingValueKinds =
    [
        EUnityAnimationBindingValueKind.Float,
        EUnityAnimationBindingValueKind.Integer,
        EUnityAnimationBindingValueKind.Boolean,
        EUnityAnimationBindingValueKind.Enum,
        EUnityAnimationBindingValueKind.Vector2,
        EUnityAnimationBindingValueKind.Vector3,
        EUnityAnimationBindingValueKind.Vector4,
        EUnityAnimationBindingValueKind.Quaternion,
        EUnityAnimationBindingValueKind.Euler,
        EUnityAnimationBindingValueKind.ObjectReference,
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

    public static IReadOnlyList<EUnityAnimationWrapMode> SupportedWrapModes => WrapModes;

    public static IReadOnlyList<EUnityAnimationBindingValueKind> SupportedBindingValueKinds => BindingValueKinds;

    public static IReadOnlyList<string> ExecutableCurveFamilies => CurveFamilies;

    public static bool SupportsSerializedVersion(int version)
    {
        for (int i = 0; i < SerializedVersions.Length; i++)
            if (SerializedVersions[i] == version)
                return true;
        return false;
    }
}
