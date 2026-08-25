using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Avatar-specific Unity humanoid calibration. The profile separates measured
/// neutral-pose and muscle-response data from the geometry-only fallback solver.
/// </summary>
public sealed class UnityHumanoidAvatarProfile
{
    public const int CurrentSchemaVersion = 3;

    private readonly UnityHumanoidAvatarRoleProfile?[] _rolesByIndex =
        new UnityHumanoidAvatarRoleProfile?[(int)EUnityHumanoidAvatarRole.Count];
    private readonly Quaternion[] _neutralRotationsByRole =
        new Quaternion[(int)EUnityHumanoidAvatarRole.Count];
    private readonly bool[] _hasNeutralRotationByRole =
        new bool[(int)EUnityHumanoidAvatarRole.Count];
    private readonly Vector3[] _neutralPositionsByRole =
        new Vector3[(int)EUnityHumanoidAvatarRole.Count];
    private readonly bool[] _hasNeutralPositionByRole =
        new bool[(int)EUnityHumanoidAvatarRole.Count];
    private readonly UnityHumanoidBoneResponseProfile?[] _responsesByRole =
        new UnityHumanoidBoneResponseProfile?[(int)EUnityHumanoidAvatarRole.Count];
    private readonly UnityHumanoidCoupledBoneModel?[] _coupledModelsByRole =
        new UnityHumanoidCoupledBoneModel?[(int)EUnityHumanoidAvatarRole.Count];

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Source { get; set; } = "UnityMecanim";
    public string AvatarName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public float HumanScale { get; set; }
    public string CalibrationClipName { get; set; } = string.Empty;
    public UnityHumanoidAvatarDescription AvatarSettings { get; set; } = new();
    public UnityHumanoidBodyAxes BodyAxes { get; set; } = new();
    public List<UnityHumanoidAvatarRoleProfile> Roles { get; set; } = [];
    public List<UnityHumanoidTwistChainProfile> TwistChains { get; set; } = [];
    public Dictionary<string, Quaternion> NeutralPoseBoneRotations { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Vector3> UnityNeutralBoneLocalPositions { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, UnityHumanoidBoneResponseProfile> BoneResponses { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, UnityHumanoidCoupledBoneModel> CoupledBoneModels { get; set; } = new(StringComparer.Ordinal);

    public bool TryGetBoneResponse(string boneName, out UnityHumanoidBoneResponseProfile response)
        => BoneResponses.TryGetValue(boneName, out response!);

    public bool TryGetCoupledBoneModel(string boneName, out UnityHumanoidCoupledBoneModel model)
        => CoupledBoneModels.TryGetValue(boneName, out model!);

    public bool TryGetRole(EUnityHumanoidAvatarRole role, out UnityHumanoidAvatarRoleProfile profile)
    {
        int index = (int)role;
        profile = (uint)index < (uint)_rolesByIndex.Length ? _rolesByIndex[index]! : null!;
        return profile is not null;
    }

    public bool TryGetNeutralRotation(EUnityHumanoidAvatarRole role, out Quaternion rotation)
    {
        int index = (int)role;
        if ((uint)index >= (uint)_neutralRotationsByRole.Length || !_hasNeutralRotationByRole[index])
        {
            rotation = Quaternion.Identity;
            return false;
        }

        rotation = _neutralRotationsByRole[index];
        return true;
    }

    public bool TryGetNeutralPosition(EUnityHumanoidAvatarRole role, out Vector3 position)
    {
        int index = (int)role;
        if ((uint)index >= (uint)_neutralPositionsByRole.Length || !_hasNeutralPositionByRole[index])
        {
            position = Vector3.Zero;
            return false;
        }

        position = _neutralPositionsByRole[index];
        return true;
    }

    public bool TryGetBoneResponse(EUnityHumanoidAvatarRole role, out UnityHumanoidBoneResponseProfile response)
    {
        int index = (int)role;
        response = (uint)index < (uint)_responsesByRole.Length ? _responsesByRole[index]! : null!;
        return response is not null;
    }

    public bool TryGetCoupledBoneModel(EUnityHumanoidAvatarRole role, out UnityHumanoidCoupledBoneModel model)
    {
        int index = (int)role;
        model = (uint)index < (uint)_coupledModelsByRole.Length ? _coupledModelsByRole[index]! : null!;
        return model is not null;
    }

    internal void BuildDenseLookups()
    {
        Array.Clear(_rolesByIndex);
        Array.Clear(_neutralRotationsByRole);
        Array.Clear(_hasNeutralRotationByRole);
        Array.Clear(_neutralPositionsByRole);
        Array.Clear(_hasNeutralPositionByRole);
        Array.Clear(_responsesByRole);
        Array.Clear(_coupledModelsByRole);

        for (int i = 0; i < Roles.Count; i++)
        {
            UnityHumanoidAvatarRoleProfile role = Roles[i];
            int roleIndex = (int)role.Role;
            if ((uint)roleIndex < (uint)_rolesByIndex.Length)
                _rolesByIndex[roleIndex] = role;
        }

        foreach ((string boneName, Quaternion rotation) in NeutralPoseBoneRotations)
        {
            if (!TryParseRole(boneName, out EUnityHumanoidAvatarRole role))
                continue;
            int index = (int)role;
            _neutralRotationsByRole[index] = rotation;
            _hasNeutralRotationByRole[index] = true;
        }

        foreach ((string boneName, Vector3 position) in UnityNeutralBoneLocalPositions)
        {
            if (!TryParseRole(boneName, out EUnityHumanoidAvatarRole role))
                continue;
            int index = (int)role;
            _neutralPositionsByRole[index] = position;
            _hasNeutralPositionByRole[index] = true;
        }

        foreach ((string boneName, UnityHumanoidBoneResponseProfile response) in BoneResponses)
            if (TryParseRole(boneName, out EUnityHumanoidAvatarRole role))
                _responsesByRole[(int)role] = response;

        foreach ((string boneName, UnityHumanoidCoupledBoneModel model) in CoupledBoneModels)
            if (TryParseRole(boneName, out EUnityHumanoidAvatarRole role))
                _coupledModelsByRole[(int)role] = model;
    }

    public static bool TryParseRole(string value, out EUnityHumanoidAvatarRole role)
        => Enum.TryParse(value, ignoreCase: false, out role) && role != EUnityHumanoidAvatarRole.Count;
}
