using System.Numerics;
using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

/// <summary>
/// Avatar-specific Unity humanoid calibration. The profile separates measured
/// neutral-pose and muscle-response data from the geometry-only fallback solver.
/// </summary>
public sealed class ImportedHumanoidAvatarProfile
{
    public const int CurrentSchemaVersion = 5;

    private readonly ImportedHumanoidAvatarRoleProfile?[] _rolesByIndex =
        new ImportedHumanoidAvatarRoleProfile?[(int)EHumanoidAvatarRole.Count];
    private readonly Quaternion[] _neutralRotationsByRole =
        new Quaternion[(int)EHumanoidAvatarRole.Count];
    private readonly bool[] _hasNeutralRotationByRole =
        new bool[(int)EHumanoidAvatarRole.Count];
    private readonly Vector3[] _neutralPositionsByRole =
        new Vector3[(int)EHumanoidAvatarRole.Count];
    private readonly bool[] _hasNeutralPositionByRole =
        new bool[(int)EHumanoidAvatarRole.Count];
    private readonly ImportedHumanoidBoneResponseProfile?[] _responsesByRole =
        new ImportedHumanoidBoneResponseProfile?[(int)EHumanoidAvatarRole.Count];
    private readonly ImportedHumanoidCoupledBoneModel?[] _coupledModelsByRole =
        new ImportedHumanoidCoupledBoneModel?[(int)EHumanoidAvatarRole.Count];

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Source { get; set; } = "UnityMecanim";
    public string AvatarName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public float HumanScale { get; set; }
    public string CalibrationClipName { get; set; } = string.Empty;
    public ImportedHumanoidClipRootMotionSettings? CalibrationRootMotionSettings { get; set; }
    public ImportedHumanoidRootAllocationFrame? RootAllocationFrame { get; set; }
    public ImportedHumanoidAvatarDescription AvatarSettings { get; set; } = new();
    public ImportedHumanoidBodyAxes BodyAxes { get; set; } = new();
    public List<ImportedHumanoidAvatarRoleProfile> Roles { get; set; } = [];
    public List<ImportedHumanoidTwistChainProfile> TwistChains { get; set; } = [];
    public Dictionary<string, Quaternion> NeutralPoseBoneRotations { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Vector3> ImportedNeutralBoneLocalPositions { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ImportedHumanoidBoneResponseProfile> BoneResponses { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ImportedHumanoidCoupledBoneModel> CoupledBoneModels { get; set; } = new(StringComparer.Ordinal);

    public bool TryGetBoneResponse(string boneName, out ImportedHumanoidBoneResponseProfile response)
        => BoneResponses.TryGetValue(boneName, out response!);

    public bool TryGetCoupledBoneModel(string boneName, out ImportedHumanoidCoupledBoneModel model)
        => CoupledBoneModels.TryGetValue(boneName, out model!);

    public bool TryGetRole(EHumanoidAvatarRole role, out ImportedHumanoidAvatarRoleProfile profile)
    {
        int index = (int)role;
        profile = (uint)index < (uint)_rolesByIndex.Length ? _rolesByIndex[index]! : null!;
        return profile is not null;
    }

    public bool TryGetNeutralRotation(EHumanoidAvatarRole role, out Quaternion rotation)
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

    public bool TryGetNeutralPosition(EHumanoidAvatarRole role, out Vector3 position)
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

    public bool TryGetBoneResponse(EHumanoidAvatarRole role, out ImportedHumanoidBoneResponseProfile response)
    {
        int index = (int)role;
        response = (uint)index < (uint)_responsesByRole.Length ? _responsesByRole[index]! : null!;
        return response is not null;
    }

    public bool TryGetCoupledBoneModel(EHumanoidAvatarRole role, out ImportedHumanoidCoupledBoneModel model)
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
            ImportedHumanoidAvatarRoleProfile role = Roles[i];
            int roleIndex = (int)role.Role;
            if ((uint)roleIndex < (uint)_rolesByIndex.Length)
                _rolesByIndex[roleIndex] = role;
        }

        foreach ((string boneName, Quaternion rotation) in NeutralPoseBoneRotations)
        {
            if (!TryParseRole(boneName, out EHumanoidAvatarRole role))
                continue;
            int index = (int)role;
            _neutralRotationsByRole[index] = rotation;
            _hasNeutralRotationByRole[index] = true;
        }

        foreach ((string boneName, Vector3 position) in ImportedNeutralBoneLocalPositions)
        {
            if (!TryParseRole(boneName, out EHumanoidAvatarRole role))
                continue;
            int index = (int)role;
            _neutralPositionsByRole[index] = position;
            _hasNeutralPositionByRole[index] = true;
        }

        foreach ((string boneName, ImportedHumanoidBoneResponseProfile response) in BoneResponses)
            if (TryParseRole(boneName, out EHumanoidAvatarRole role))
                _responsesByRole[(int)role] = response;

        foreach ((string boneName, ImportedHumanoidCoupledBoneModel model) in CoupledBoneModels)
            if (TryParseRole(boneName, out EHumanoidAvatarRole role))
                _coupledModelsByRole[(int)role] = model;
    }

    public static bool TryParseRole(string value, out EHumanoidAvatarRole role)
        => Enum.TryParse(value, ignoreCase: false, out role) && role != EHumanoidAvatarRole.Count;
}
