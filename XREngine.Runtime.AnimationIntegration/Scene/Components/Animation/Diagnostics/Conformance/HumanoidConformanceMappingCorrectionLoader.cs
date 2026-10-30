using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XREngine.Scene;

namespace XREngine.Components.Animation;

/// <summary>
/// Loads Phase 10 persisted mapping corrections, verifies their fixture identity,
/// and applies them through <see cref="HumanoidComponent"/>'s normal persisted
/// avatar-definition workflow.
/// </summary>
public static class HumanoidConformanceMappingCorrectionLoader
{
    private static readonly EHumanoidAvatarBoneRole[] RequiredRoles =
    [
        EHumanoidAvatarBoneRole.Hips,
        EHumanoidAvatarBoneRole.Spine,
        EHumanoidAvatarBoneRole.Head,
        EHumanoidAvatarBoneRole.LeftUpperArm,
        EHumanoidAvatarBoneRole.LeftLowerArm,
        EHumanoidAvatarBoneRole.LeftHand,
        EHumanoidAvatarBoneRole.RightUpperArm,
        EHumanoidAvatarBoneRole.RightLowerArm,
        EHumanoidAvatarBoneRole.RightHand,
        EHumanoidAvatarBoneRole.LeftUpperLeg,
        EHumanoidAvatarBoneRole.LeftLowerLeg,
        EHumanoidAvatarBoneRole.LeftFoot,
        EHumanoidAvatarBoneRole.RightUpperLeg,
        EHumanoidAvatarBoneRole.RightLowerLeg,
        EHumanoidAvatarBoneRole.RightFoot,
    ];

    /// <summary>Loads, validates, and applies a correction without special-casing fixture or avatar names.</summary>
    public static HumanoidConformanceMappingCorrectionResult LoadValidateAndApply(
        string sidecarPath,
        string sourceFbxPath,
        HumanoidComponent humanoid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sidecarPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFbxPath);
        ArgumentNullException.ThrowIfNull(humanoid);

        string fullSidecarPath = Path.GetFullPath(sidecarPath);
        string fullSourceFbxPath = Path.GetFullPath(sourceFbxPath);
        var result = new HumanoidConformanceMappingCorrectionResult { SidecarPath = fullSidecarPath };
        using var files = new HumanoidConformanceFileRead("Humanoid conformance mapping correction read");
        HumanoidConformanceMappingCorrection? correction = Load(fullSidecarPath, result, files);
        if (correction is null)
        {
            using IDisposable publication = files.BeginPublication();
            return result;
        }

        result.Correction = correction;
        string? sourceHash = Validate(correction, fullSidecarPath, fullSourceFbxPath, humanoid, result, files);
        result.MappingSignature = ComputeCanonicalSignature(correction);
        if (result.Issues.Count > 0)
        {
            using IDisposable publication = files.BeginPublication();
            return result;
        }

        // The observed source bytes, not the sidecar declaration, are authoritative.
        // Validation above proves that the declaration matches before any mapping mutates.
        using IDisposable appliedPublication = files.BeginPublication();
        humanoid.SetSourceModelContentSha256(sourceHash!);

        var resolved = new List<(EHumanoidAvatarBoneRole Role, SceneNode Node)>(correction.Roles.Count);
        foreach ((string roleName, string path) in correction.Roles.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            _ = Enum.TryParse(roleName, ignoreCase: false, out EHumanoidAvatarBoneRole role);
            SceneNode? node = ResolveDeclaredPath(humanoid.SceneNode, correction.RootPath, path);
            if (node is null)
            {
                Add(result, "RolePathUnresolved", $"Role '{roleName}' path '{path}' could not be resolved uniquely below '{correction.RootPath}'.");
                continue;
            }
            resolved.Add((role, node));
        }
        if (result.Issues.Count > 0)
            return result;

        // Apply the complete correction as one authoring transaction so body axes,
        // scale, and canonical corrections are derived from the final mapping rather
        // than being frozen by the first partial role assignment.
        humanoid.SetAvatarBoneMappings(
            resolved.ToDictionary(static x => x.Role, static x => (SceneNode?)x.Node),
            lockEditorCorrections: true);

        if (!humanoid.ConfirmAvatarDefinition(out string diagnostic))
        {
            Add(result, "AvatarDefinitionRejected", diagnostic);
            return result;
        }

        result.AppliedAvatarDefinitionSignature = humanoid.AvatarDefinition.DefinitionContentSha256;
        if (!string.Equals(
                result.AppliedAvatarDefinitionSignature,
                correction.ExpectedAvatarDefinitionSignature,
                StringComparison.OrdinalIgnoreCase))
        {
            Add(result, "AvatarDefinitionSignatureMismatch",
                $"Correction expected avatar definition signature '{correction.ExpectedAvatarDefinitionSignature}', " +
                $"but application produced '{result.AppliedAvatarDefinitionSignature}'.");
            return result;
        }

        result.Applied = true;
        return result;
    }

    /// <summary>Computes the stable SHA-256 signature of correction semantics, not JSON formatting.</summary>
    public static string ComputeCanonicalSignature(HumanoidConformanceMappingCorrection correction)
    {
        ArgumentNullException.ThrowIfNull(correction);
        var canonical = new StringBuilder(1024);
        Append(canonical, correction.SchemaVersion);
        Append(canonical, correction.FixtureVersion);
        Append(canonical, correction.Fixture);
        Append(canonical, correction.MappingMode);
        Append(canonical, correction.RootPath);
        Append(canonical, NormalizeSha256(correction.SourceFbxSha256));
        Append(canonical, correction.ExpectedAvatarDefinitionSignature);
        foreach ((string role, string path) in correction.Roles.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Append(canonical, role);
            Append(canonical, path);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static HumanoidConformanceMappingCorrection? Load(
        string fullSidecarPath,
        HumanoidConformanceMappingCorrectionResult result,
        HumanoidConformanceFileRead files)
    {
        if (!files.FileExists(fullSidecarPath))
        {
            Add(result, "SidecarMissing", $"Mapping correction '{fullSidecarPath}' does not exist.");
            return null;
        }

        try
        {
            using Stream input = files.OpenRead(fullSidecarPath);
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            JToken token = JToken.ReadFrom(new JsonTextReader(reader), new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            });
            return token.ToObject<HumanoidConformanceMappingCorrection>();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Add(result, "SidecarUnreadable", $"Could not load mapping correction '{fullSidecarPath}': {ex.Message}");
            return null;
        }
    }

    private static string? Validate(
        HumanoidConformanceMappingCorrection correction,
        string sidecarPath,
        string sourceFbxPath,
        HumanoidComponent humanoid,
        HumanoidConformanceMappingCorrectionResult result,
        HumanoidConformanceFileRead files)
    {
        if (correction.SchemaVersion != HumanoidConformanceMappingCorrection.CurrentSchemaVersion)
            Add(result, "UnsupportedSchema", $"Schema version {correction.SchemaVersion} is unsupported; expected {HumanoidConformanceMappingCorrection.CurrentSchemaVersion}.");
        Require(correction.FixtureVersion, "FixtureVersion", result);
        Require(correction.Fixture, "Fixture", result);
        Require(correction.RootPath, "RootPath", result);
        Require(correction.SourceFbxSha256, "SourceFbxSha256", result);
        Require(correction.ExpectedAvatarDefinitionSignature, "ExpectedAvatarDefinitionSignature", result);
        if (!string.Equals(correction.MappingMode, "persisted-corrections", StringComparison.Ordinal))
            Add(result, "MappingMode", "MappingMode must be 'persisted-corrections'.");

        if (!files.FileExists(sourceFbxPath))
        {
            Add(result, "SourceFbxMissing", $"Source FBX '{sourceFbxPath}' does not exist.");
            return null;
        }

        string sourceHash = ComputeFileSha256(sourceFbxPath, files);
        if (!string.Equals(sourceHash, NormalizeSha256(correction.SourceFbxSha256), StringComparison.OrdinalIgnoreCase))
            Add(result, "SourceFbxHashMismatch", $"Source FBX hash '{sourceHash}' does not match declared SourceFbxSha256.");

        if (humanoid.SceneNode is null)
        {
            Add(result, "HumanoidRootMissing", "The target HumanoidComponent has no scene-node root.");
            return sourceHash;
        }
        if (!string.Equals(correction.RootPath, ".", StringComparison.Ordinal))
            Add(result, "RootPath", "Schema 3 mapping corrections must declare '.' as the path-independent imported hierarchy root.");

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var seenRoles = new HashSet<EHumanoidAvatarBoneRole>();
        foreach ((string roleName, string path) in correction.Roles)
        {
            if (!Enum.TryParse(roleName, ignoreCase: false, out EHumanoidAvatarBoneRole role)
                || !Enum.IsDefined(role))
            {
                Add(result, "UnknownRole", $"'{roleName}' is not a recognized humanoid role.");
                continue;
            }
            if (!seenRoles.Add(role))
                Add(result, "DuplicateRole", $"Role '{roleName}' occurs more than once.");
            if (!IsDeclaredPathContained(correction.RootPath, path))
                Add(result, "RolePathOutsideRoot", $"Role '{roleName}' path '{path}' is not a safe descendant of RootPath '{correction.RootPath}'.");
            else if (!seenPaths.Add(path))
                Add(result, "DuplicatePath", $"Path '{path}' is assigned to more than one role.");
        }

        foreach (EHumanoidAvatarBoneRole role in RequiredRoles)
            if (!seenRoles.Contains(role))
                Add(result, "RequiredRoleMissing", $"Required humanoid role '{role}' is not mapped exactly once.");
        return sourceHash;
    }

    private static bool IsDeclaredPathContained(string rootPath, string path)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(path))
            return false;
        if (!string.Equals(rootPath, ".", StringComparison.Ordinal))
            return false;
        string[] pathSegments = path.Split('/', StringSplitOptions.None);
        if (pathSegments.Length < 1)
            return false;
        for (int i = 0; i < pathSegments.Length; i++)
            if (string.IsNullOrWhiteSpace(pathSegments[i]) || pathSegments[i] is "." or ".." || pathSegments[i].Contains('\\'))
                return false;
        return true;
    }

    private static SceneNode? ResolveDeclaredPath(SceneNode root, string rootPath, string path)
    {
        if (!IsDeclaredPathContained(rootPath, path))
            return null;
        string[] segments = path.Split('/', StringSplitOptions.None);
        SceneNode current = root;
        for (int i = 0; i < segments.Length; i++)
        {
            SceneNode? match = null;
            int matches = 0;
            foreach (var childTransform in current.Transform.Children)
            {
                SceneNode? child = childTransform.SceneNode;
                if (child is null || !string.Equals(child.Name, segments[i], StringComparison.Ordinal))
                    continue;
                match = child;
                matches++;
            }
            if (matches != 1 || match is null)
                return null;
            current = match;
        }
        return current;
    }

    private static void Require(string value, string field, HumanoidConformanceMappingCorrectionResult result)
    {
        if (string.IsNullOrWhiteSpace(value))
            Add(result, "RequiredFieldMissing", $"Required field '{field}' is missing.");
    }

    private static string NormalizeSha256(string value)
        => value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();

    private static string ComputeFileSha256(string path, HumanoidConformanceFileRead files)
    {
        using Stream stream = files.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        files.EnsureCurrent();
        return hash;
    }

    private static void Append(StringBuilder builder, string value)
        => builder.Append(value ?? string.Empty).Append('\n');

    private static void Append(StringBuilder builder, int value)
        => builder.Append(value).Append('\n');

    private static void Add(HumanoidConformanceMappingCorrectionResult result, string code, string message)
        => result.Issues.Add(new HumanoidConformanceMappingCorrectionIssue { Code = code, Message = message });
}
