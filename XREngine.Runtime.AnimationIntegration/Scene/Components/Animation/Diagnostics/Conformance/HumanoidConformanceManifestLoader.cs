using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace XREngine.Components.Animation;

/// <summary>Loads and validates Phase 10 corpus manifests without invoking Unity or mutating disk.</summary>
public static class HumanoidConformanceManifestLoader
{
    /// <summary>Loads a JSON manifest and validates its provenance, hashes, spaces, and matrix coverage.</summary>
    public static HumanoidConformanceValidationResult LoadAndValidate(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        string fullManifestPath = Path.GetFullPath(manifestPath);
        var result = new HumanoidConformanceValidationResult { ManifestPath = fullManifestPath };
        if (!File.Exists(fullManifestPath))
        {
            Add(result, "ManifestMissing", $"Manifest '{fullManifestPath}' does not exist.");
            return result;
        }

        HumanoidConformanceManifest? manifest;
        try
        {
            string json = File.ReadAllText(fullManifestPath, Encoding.UTF8);
            manifest = JsonConvert.DeserializeObject<HumanoidConformanceManifest>(json);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Add(result, "ManifestUnreadable", $"Could not read manifest '{fullManifestPath}': {ex.Message}");
            return result;
        }

        if (manifest is null)
        {
            Add(result, "ManifestInvalid", $"Manifest '{fullManifestPath}' did not deserialize.");
            return result;
        }

        result.Manifest = manifest;
        Validate(manifest, Path.GetDirectoryName(fullManifestPath)!, result);
        return result;
    }

    /// <summary>Validates an already materialized manifest against the supplied manifest root.</summary>
    public static HumanoidConformanceValidationResult Validate(HumanoidConformanceManifest manifest, string manifestRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestRoot);

        var result = new HumanoidConformanceValidationResult
        {
            Manifest = manifest,
            ManifestPath = Path.GetFullPath(manifestRoot),
        };
        Validate(manifest, Path.GetFullPath(manifestRoot), result);
        return result;
    }

    private static void Validate(HumanoidConformanceManifest manifest, string root, HumanoidConformanceValidationResult result)
    {
        if (manifest.SchemaVersion != HumanoidConformanceManifest.CurrentSchemaVersion)
            Add(result, "UnsupportedSchema", $"Schema version {manifest.SchemaVersion} is unsupported; expected {HumanoidConformanceManifest.CurrentSchemaVersion}.");
        Require(manifest.CorpusId, "CorpusId", result);
        Require(manifest.CorpusVersion, "CorpusVersion", result);
        Require(manifest.Provenance, "CorpusProvenance", result);
        RejectPlaceholder(manifest.CorpusId, "CorpusId", result);
        RejectPlaceholder(manifest.CorpusVersion, "CorpusVersion", result);
        RejectPlaceholder(manifest.Provenance, "CorpusProvenance", result);
        if (manifest.RequiresUnityInstallation)
            Add(result, "UnityDependency", "Phase 10 conformance manifests must not require a Unity executable or installation.");

        IReadOnlyList<HumanoidConformanceSourceFile> sourceFiles = manifest.SourceFiles ?? [];
        IReadOnlyList<HumanoidConformanceAvatar> avatarList = manifest.Avatars ?? [];
        IReadOnlyList<HumanoidConformanceClip> clipList = manifest.Clips ?? [];
        IReadOnlyList<HumanoidConformanceMatrixCase> matrix = manifest.Matrix ?? [];
        IReadOnlyList<HumanoidConformanceCaptureTool> captureToolList = manifest.CaptureTools ?? [];
        IReadOnlyList<HumanoidConformanceAssetCheck> assetCheckList = manifest.AssetChecks ?? [];
        if (manifest.SourceFiles is null)
            Add(result, "SourceFilesMissing", "Manifest does not declare source files.");
        if (manifest.Avatars is null)
            Add(result, "AvatarsMissing", "Manifest does not declare avatars.");
        if (manifest.Clips is null)
            Add(result, "ClipsMissing", "Manifest does not declare clips.");
        if (manifest.Matrix is null)
            Add(result, "MatrixMissing", "Manifest does not declare matrix cases.");
        if (manifest.CaptureTools is null)
            Add(result, "CaptureToolsMissing", "Manifest does not declare content-addressed capture tools.");
        if (manifest.AssetChecks is null)
            Add(result, "AssetChecksMissing", "Manifest does not declare executable asset checks.");

        var files = Index(sourceFiles, static x => x.Id, "SourceFile", result);
        var avatars = Index(avatarList, static x => x.Id, "Avatar", result);
        var clips = Index(clipList, static x => x.Id, "Clip", result);
        var cases = Index(matrix, static x => x.Id, "MatrixCase", result);
        var captureTools = Index(captureToolList, static x => x.Id, "CaptureTool", result);
        var assetChecks = Index(assetCheckList, static x => x.Id, "AssetCheck", result);
        ValidateFiles(files, root, result);
        ValidateCaptureTools(captureTools, FindRepositoryRoot(root), result);
        ValidateAssetChecks(assetChecks, files, avatarList, clipList, result);
        ValidateAvatars(avatarList, files, result);
        ValidateClips(clipList, files, result);
        ValidateMatrix(manifest, avatars, clips, files, captureTools, cases, result);
    }

    private static Dictionary<string, T> Index<T>(IReadOnlyList<T> values, Func<T, string> id, string type, HumanoidConformanceValidationResult result)
    {
        var indexed = new Dictionary<string, T>(StringComparer.Ordinal);
        for (int i = 0; i < values.Count; i++)
        {
            T value = values[i];
            string valueId = id(value);
            if (string.IsNullOrWhiteSpace(valueId))
            {
                Add(result, $"{type}IdMissing", $"{type} at index {i} has no ID.");
                continue;
            }

            if (!indexed.TryAdd(valueId, value))
                Add(result, $"{type}IdDuplicate", $"{type} ID '{valueId}' is duplicated.");
        }

        return indexed;
    }

    private static void ValidateFiles(Dictionary<string, HumanoidConformanceSourceFile> files, string root, HumanoidConformanceValidationResult result)
    {
        foreach ((string id, HumanoidConformanceSourceFile file) in files)
        {
            if (file.ArtifactKind == HumanoidConformanceArtifactKind.Unknown)
                Add(result, "SourceFileKindMissing", $"Source file '{id}' does not declare an artifact kind.");
            Require(file.RelativePath, $"SourceFilePath:{id}", result);
            Require(file.Sha256, $"SourceFileHash:{id}", result);
            Require(file.Provenance, $"SourceFileProvenance:{id}", result);
            Require(file.Signature, $"SourceFileSignature:{id}", result);
            if (!IsSha256(file.Sha256))
            {
                Add(result, "SourceFileHashInvalid", $"Source file '{id}' does not declare a SHA-256 hash.");
                continue;
            }

            if (!TryResolveContainedPath(root, file.RelativePath, out string fullPath))
            {
                Add(result, "UnsafePath", $"Source file '{id}' escapes the manifest root.");
                continue;
            }

            if (!File.Exists(fullPath))
            {
                Add(result, "SourceFileMissing", $"Source file '{id}' does not exist at '{file.RelativePath}'.");
                continue;
            }

            string actualHash;
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                Add(result, "SourceFileHashChanged", $"Source file '{id}' SHA-256 does not match the manifest.");
            RejectPlaceholder(file.Provenance, $"SourceFileProvenance:{id}", result);
            RejectPlaceholder(file.Signature, $"SourceFileSignature:{id}", result);
        }
    }

    private static void ValidateCaptureTools(
        Dictionary<string, HumanoidConformanceCaptureTool> captureTools,
        string repositoryRoot,
        HumanoidConformanceValidationResult result)
    {
        foreach ((string id, HumanoidConformanceCaptureTool tool) in captureTools)
        {
            Require(tool.RelativeRepositoryPath, $"CaptureToolPath:{id}", result);
            RequireHash(tool.Sha256, $"CaptureToolHash:{id}", result);
            Require(tool.Version, $"CaptureToolVersion:{id}", result);
            Require(tool.Provenance, $"CaptureToolProvenance:{id}", result);
            RejectPlaceholder(tool.Version, $"CaptureToolVersion:{id}", result);
            RejectPlaceholder(tool.Provenance, $"CaptureToolProvenance:{id}", result);
            if (!IsSha256(tool.Sha256)
                || !TryResolveContainedPath(repositoryRoot, tool.RelativeRepositoryPath, out string sourcePath))
                continue;
            if (!File.Exists(sourcePath))
            {
                Add(result, "CaptureToolMissing", $"Capture tool '{id}' does not exist at '{tool.RelativeRepositoryPath}'.");
                continue;
            }

            string sourceHash;
            using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                sourceHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(sourceHash, tool.Sha256, StringComparison.OrdinalIgnoreCase))
                Add(result, "CaptureToolHashChanged", $"Capture tool '{id}' SHA-256 does not match the manifest.");
        }
    }

    private static void ValidateAssetChecks(
        Dictionary<string, HumanoidConformanceAssetCheck> checks,
        Dictionary<string, HumanoidConformanceSourceFile> files,
        IReadOnlyList<HumanoidConformanceAvatar> avatars,
        IReadOnlyList<HumanoidConformanceClip> clips,
        HumanoidConformanceValidationResult result)
    {
        var checksBySource = new Dictionary<string, List<HumanoidConformanceAssetCheck>>(StringComparer.Ordinal);
        foreach ((string id, HumanoidConformanceAssetCheck check) in checks)
        {
            ValidateSourceReference(files, check.SourceFileId, $"AssetCheck:{id}", result);
            Require(check.Provenance, $"AssetCheckProvenance:{id}", result);
            RejectPlaceholder(check.Provenance, $"AssetCheckProvenance:{id}", result);
            if (!files.ContainsKey(check.SourceFileId))
                continue;
            if (!checksBySource.TryGetValue(check.SourceFileId, out List<HumanoidConformanceAssetCheck>? sourceChecks))
            {
                sourceChecks = [];
                checksBySource.Add(check.SourceFileId, sourceChecks);
            }
            sourceChecks.Add(check);
            if (check.Kind == HumanoidConformanceAssetCheckKind.ExpectedMalformedModelImport && check.ExpectedToPass)
                Add(result, "MalformedModelMustFail", $"Asset check '{id}' marks an expected-malformed model import as passing.");
            if (check.Kind != HumanoidConformanceAssetCheckKind.ExpectedMalformedModelImport && !check.ExpectedToPass)
                Add(result, "AssetCheckUnexpectedFailure", $"Asset check '{id}' may fail only when it is an expected-malformed model import.");
        }

        foreach ((string sourceId, HumanoidConformanceSourceFile source) in files)
        {
            string extension = Path.GetExtension(source.RelativePath);
            if (!extension.Equals(".anim", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!checksBySource.TryGetValue(sourceId, out List<HumanoidConformanceAssetCheck>? sourceChecks))
            {
                Add(result, "AssetCheckMissing", $"Corpus asset '{source.RelativePath}' has no executable classification.");
                continue;
            }

            bool valid = extension.Equals(".anim", StringComparison.OrdinalIgnoreCase)
                ? sourceChecks.Any(static x => x.Kind is HumanoidConformanceAssetCheckKind.AnimationBehaviorAndImport or HumanoidConformanceAssetCheckKind.AnimationImport)
                : sourceChecks.Any(static x => x.Kind is HumanoidConformanceAssetCheckKind.ValidModelImport or HumanoidConformanceAssetCheckKind.ExpectedMalformedModelImport);
            if (!valid)
                Add(result, "AssetCheckInvalid", $"Corpus asset '{source.RelativePath}' has no compatible executable classification.");
        }

        foreach (HumanoidConformanceAvatar avatar in avatars)
            RequireCheck(checksBySource, avatar.SourceFileId, HumanoidConformanceAssetCheckKind.HumanoidMatrixAvatar, $"Avatar:{avatar.Id}", result);
        foreach (HumanoidConformanceClip clip in clips)
            RequireAnimationCheck(checksBySource, clip.SourceFileId, $"Clip:{clip.Id}", result);
    }

    private static void RequireCheck(
        Dictionary<string, List<HumanoidConformanceAssetCheck>> checksBySource,
        string sourceFileId,
        HumanoidConformanceAssetCheckKind kind,
        string owner,
        HumanoidConformanceValidationResult result)
    {
        if (!checksBySource.TryGetValue(sourceFileId, out List<HumanoidConformanceAssetCheck>? checks)
            || !checks.Any(x => x.Kind == kind && x.ExpectedToPass))
            Add(result, "AssetCheckMissing", $"{owner} requires a passing '{kind}' check.");
    }

    private static void RequireAnimationCheck(
        Dictionary<string, List<HumanoidConformanceAssetCheck>> checksBySource,
        string sourceFileId,
        string owner,
        HumanoidConformanceValidationResult result)
    {
        if (!checksBySource.TryGetValue(sourceFileId, out List<HumanoidConformanceAssetCheck>? checks)
            || !checks.Any(static x => x.ExpectedToPass && x.Kind is HumanoidConformanceAssetCheckKind.AnimationBehaviorAndImport or HumanoidConformanceAssetCheckKind.AnimationImport))
            Add(result, "AssetCheckMissing", $"{owner} requires a passing animation import check.");
    }

    private static void ValidateAvatars(
        IReadOnlyList<HumanoidConformanceAvatar> avatars,
        Dictionary<string, HumanoidConformanceSourceFile> files,
        HumanoidConformanceValidationResult result)
    {
        int publicAvatarCount = 0;
        bool conventionalNames = false;
        bool arbitraryNames = false;
        bool distinctBindAxesAndProportions = false;
        bool missingOptionalRoles = false;
        bool automaticMapping = false;
        bool persistedCorrection = false;
        for (int i = 0; i < avatars.Count; i++)
        {
            HumanoidConformanceAvatar avatar = avatars[i];
            if (!avatar.IsIntegrationOnly)
            {
                publicAvatarCount++;
                conventionalNames |= avatar.HasConventionalBoneNames;
                arbitraryNames |= avatar.HasArbitraryBoneNames;
                distinctBindAxesAndProportions |= avatar.HasDistinctBindAxesAndProportions;
                missingOptionalRoles |= avatar.HasMissingOptionalRoles;
                automaticMapping |= avatar.MappingMode == HumanoidConformanceMappingMode.Automatic;
                persistedCorrection |= avatar.MappingMode == HumanoidConformanceMappingMode.PersistedCorrection;
            }
            ValidateSourceReference(files, avatar.SourceFileId, $"Avatar:{avatar.Id}", result);
            Require(avatar.AvatarDefinitionSignature, $"AvatarDefinitionSignature:{avatar.Id}", result);
            RequireHash(avatar.ImportSettingsHash, $"AvatarImportSettingsHash:{avatar.Id}", result);
            ValidateSpaces(avatar.CoordinateSpaces, $"Avatar:{avatar.Id}", result);
            if (avatar.MappingMode == HumanoidConformanceMappingMode.PersistedCorrection)
            {
                ValidateSourceReference(files, avatar.MappingCorrectionsSourceFileId, $"MappingCorrections:{avatar.Id}", result);
                if (files.TryGetValue(avatar.MappingCorrectionsSourceFileId, out HumanoidConformanceSourceFile? mappingFile))
                    RequireHash(mappingFile.Sha256, $"MappingCorrectionsHash:{avatar.Id}", result);
            }
        }

        if (publicAvatarCount < 3)
            Add(result, "AvatarCorpusIncomplete", "Phase 10 requires at least three redistributable, non-integration-only avatars.");
        RequireCoverageCategory(conventionalNames, "ConventionalNaming", result);
        RequireCoverageCategory(arbitraryNames, "ArbitraryNaming", result);
        RequireCoverageCategory(distinctBindAxesAndProportions, "DistinctBindAxesAndProportions", result);
        RequireCoverageCategory(missingOptionalRoles, "MissingOptionalRoles", result);
        RequireCoverageCategory(automaticMapping, "AutomaticMapping", result);
        RequireCoverageCategory(persistedCorrection, "PersistedMappingCorrection", result);
    }

    private static void ValidateClips(
        IReadOnlyList<HumanoidConformanceClip> clips,
        Dictionary<string, HumanoidConformanceSourceFile> files,
        HumanoidConformanceValidationResult result)
    {
        for (int i = 0; i < clips.Count; i++)
        {
            HumanoidConformanceClip clip = clips[i];
            ValidateSourceReference(files, clip.SourceFileId, $"Clip:{clip.Id}", result);
            Require(clip.ClipSignature, $"ClipSignature:{clip.Id}", result);
            RequireHash(clip.ImportSettingsHash, $"ClipImportSettingsHash:{clip.Id}", result);
            ValidateSpaces(clip.CoordinateSpaces, $"Clip:{clip.Id}", result);
        }
    }

    private static void ValidateMatrix(
        HumanoidConformanceManifest manifest,
        Dictionary<string, HumanoidConformanceAvatar> avatars,
        Dictionary<string, HumanoidConformanceClip> clips,
        Dictionary<string, HumanoidConformanceSourceFile> files,
        Dictionary<string, HumanoidConformanceCaptureTool> captureTools,
        Dictionary<string, HumanoidConformanceMatrixCase> cases,
        HumanoidConformanceValidationResult result)
    {
        var directRows = new HashSet<string>(StringComparer.Ordinal);
        var publicPlaybackModes = new HashSet<HumanoidConformancePlaybackMode>();
        foreach (HumanoidConformanceMatrixCase row in cases.Values)
        {
            if (!avatars.TryGetValue(row.AvatarId, out HumanoidConformanceAvatar? avatar))
            {
                Add(result, "MatrixAvatarMissing", $"Matrix case '{row.Id}' references unknown avatar '{row.AvatarId}'.");
                continue;
            }
            if (!clips.TryGetValue(row.ClipId, out HumanoidConformanceClip? clip))
            {
                Add(result, "MatrixClipMissing", $"Matrix case '{row.Id}' references unknown clip '{row.ClipId}'.");
                continue;
            }

            ValidateSourceReference(files, row.ReferenceFileId, $"Reference:{row.Id}", result);
            Require(row.ReferenceSignature, $"ReferenceSignature:{row.Id}", result);
            Require(row.Provenance, $"ReferenceProvenance:{row.Id}", result);
            ValidateSpaces(row.CoordinateSpaces, $"Matrix:{row.Id}", result);
            ValidateTolerances(row.Tolerances, row.Id, result);
            ValidateKnownAnswer(row, avatar, clip, files, captureTools, result);
            ValidatePlaybackConsistency(row, result);
            if (!string.Equals(row.AvatarDefinitionSignature, avatar.AvatarDefinitionSignature, StringComparison.Ordinal))
                Add(result, "StaleAvatarSignature", $"Matrix case '{row.Id}' does not match avatar '{row.AvatarId}' definition signature.");
            if (!string.Equals(row.ClipSignature, clip.ClipSignature, StringComparison.Ordinal))
                Add(result, "StaleClipSignature", $"Matrix case '{row.Id}' does not match clip '{row.ClipId}' signature.");
            if (files.TryGetValue(row.ReferenceFileId, out HumanoidConformanceSourceFile? reference))
            {
                if (reference.ArtifactKind != HumanoidConformanceArtifactKind.KnownAnswerReference)
                    Add(result, "ReferenceArtifactKindInvalid", $"Matrix case '{row.Id}' reference '{row.ReferenceFileId}' is not a known-answer artifact.");
                if (!string.Equals(row.ReferenceSignature, reference.Signature, StringComparison.Ordinal))
                    Add(result, "StaleReferenceSignature", $"Matrix case '{row.Id}' does not match reference '{row.ReferenceFileId}' signature.");
            }
            RejectPlaceholder(row.Provenance, $"ReferenceProvenance:{row.Id}", result);
            RejectPlaceholder(row.ReferenceSignature, $"ReferenceSignature:{row.Id}", result);

            if (!row.IsIntegrationOnly && row.PlaybackMode == HumanoidConformancePlaybackMode.DirectClip)
            {
                string key = $"{row.AvatarId}\u001f{row.ClipId}";
                if (!directRows.Add(key))
                    Add(result, "MatrixDuplicate", $"Multiple direct-clip cases cover avatar '{row.AvatarId}' and clip '{row.ClipId}'.");
            }

            if (!row.IsIntegrationOnly)
                publicPlaybackModes.Add(row.PlaybackMode);
        }

        ValidatePlaybackRouteCoverage(publicPlaybackModes, result);

        foreach (HumanoidConformanceAvatar avatar in avatars.Values)
        {
            if (avatar.IsIntegrationOnly)
                continue;
            foreach (HumanoidConformanceClip clip in clips.Values)
            {
                if (clip.IsIntegrationOnly || !IsCompatible(avatar, clip))
                    continue;
                string key = $"{avatar.Id}\u001f{clip.Id}";
                if (!directRows.Contains(key))
                    Add(result, "MatrixIncomplete", $"No direct-clip case covers compatible avatar '{avatar.Id}' and clip '{clip.Id}'.");
            }
        }
    }

    private static void ValidatePlaybackRouteCoverage(HashSet<HumanoidConformancePlaybackMode> modes, HumanoidConformanceValidationResult result)
    {
        foreach (HumanoidConformancePlaybackMode required in Enum.GetValues<HumanoidConformancePlaybackMode>())
            if (!modes.Contains(required))
                Add(result, "PlaybackRouteMissing", $"No public matrix row covers required playback route '{required}'.");
    }

    private static void ValidatePlaybackConsistency(HumanoidConformanceMatrixCase row, HumanoidConformanceValidationResult result)
    {
        HumanoidConformanceCapability capabilities = row.ExpectedCapabilities;
        const HumanoidConformanceCapability stateMachineFeatures =
            HumanoidConformanceCapability.StateMachine |
            HumanoidConformanceCapability.Transitions |
            HumanoidConformanceCapability.InterruptedTransitions |
            HumanoidConformanceCapability.BlendTree1D |
            HumanoidConformanceCapability.BlendTree2D |
            HumanoidConformanceCapability.DirectBlendTree;
        bool hasStateMachine = capabilities.HasFlag(HumanoidConformanceCapability.StateMachine);
        switch (row.PlaybackMode)
        {
            case HumanoidConformancePlaybackMode.DirectClip:
                if ((capabilities & stateMachineFeatures) != HumanoidConformanceCapability.None)
                    Add(result, "PlaybackModeInconsistent", $"Direct-clip row '{row.Id}' cannot claim state-machine or blend-tree capability coverage.");
                break;
            case HumanoidConformancePlaybackMode.StateMachine:
                RequireCapability(hasStateMachine, row, HumanoidConformanceCapability.StateMachine, result);
                break;
            case HumanoidConformancePlaybackMode.Transition:
                RequireCapability(hasStateMachine, row, HumanoidConformanceCapability.StateMachine, result);
                RequireCapability(capabilities.HasFlag(HumanoidConformanceCapability.Transitions), row, HumanoidConformanceCapability.Transitions, result);
                break;
            case HumanoidConformancePlaybackMode.InterruptedTransition:
                RequireCapability(hasStateMachine, row, HumanoidConformanceCapability.StateMachine, result);
                RequireCapability(capabilities.HasFlag(HumanoidConformanceCapability.Transitions), row, HumanoidConformanceCapability.Transitions, result);
                RequireCapability(capabilities.HasFlag(HumanoidConformanceCapability.InterruptedTransitions), row, HumanoidConformanceCapability.InterruptedTransitions, result);
                break;
            case HumanoidConformancePlaybackMode.BlendTree1D:
                RequireCapability(capabilities.HasFlag(HumanoidConformanceCapability.BlendTree1D), row, HumanoidConformanceCapability.BlendTree1D, result);
                break;
            case HumanoidConformancePlaybackMode.BlendTree2D:
                RequireCapability(capabilities.HasFlag(HumanoidConformanceCapability.BlendTree2D), row, HumanoidConformanceCapability.BlendTree2D, result);
                break;
            case HumanoidConformancePlaybackMode.DirectBlendTree:
                RequireCapability(capabilities.HasFlag(HumanoidConformanceCapability.DirectBlendTree), row, HumanoidConformanceCapability.DirectBlendTree, result);
                break;
        }
    }

    private static void RequireCapability(bool present, HumanoidConformanceMatrixCase row, HumanoidConformanceCapability capability, HumanoidConformanceValidationResult result)
    {
        if (!present)
            Add(result, "PlaybackModeInconsistent", $"Playback row '{row.Id}' must declare capability '{capability}'.");
    }

    private static void ValidateKnownAnswer(
        HumanoidConformanceMatrixCase row,
        HumanoidConformanceAvatar avatar,
        HumanoidConformanceClip clip,
        Dictionary<string, HumanoidConformanceSourceFile> files,
        Dictionary<string, HumanoidConformanceCaptureTool> captureTools,
        HumanoidConformanceValidationResult result)
    {
        HumanoidConformanceKnownAnswerProvenance? knownAnswer = row.KnownAnswer;
        if (knownAnswer is null)
        {
            Add(result, "KnownAnswerProvenanceMissing", $"Matrix case '{row.Id}' has no machine-readable known-answer provenance.");
            return;
        }

        Require(knownAnswer.CaptureToolId, $"CaptureToolId:{row.Id}", result);
        Require(knownAnswer.SourceUnityEditorVersion, $"SourceUnityEditorVersion:{row.Id}", result);
        Require(knownAnswer.SerializedClipVersion, $"SerializedClipVersion:{row.Id}", result);
        if (knownAnswer.ReferenceSchemaVersion <= 0)
            Add(result, "ReferenceSchemaVersionInvalid", $"Matrix case '{row.Id}' does not declare a positive reference schema version.");
        RequireHash(knownAnswer.SourceAvatarSha256, $"SourceAvatarSha256:{row.Id}", result);
        RequireHash(knownAnswer.SourceClipSha256, $"SourceClipSha256:{row.Id}", result);
        Require(knownAnswer.AvatarDefinitionSignature, $"KnownAnswerAvatarDefinitionSignature:{row.Id}", result);
        RequireHash(knownAnswer.AvatarImportSettingsSha256, $"KnownAnswerAvatarImportSettingsHash:{row.Id}", result);
        RequireHash(knownAnswer.ClipImportSettingsSha256, $"KnownAnswerClipImportSettingsHash:{row.Id}", result);
        Require(knownAnswer.CaptureToolIdentity, $"CaptureToolIdentity:{row.Id}", result);
        Require(knownAnswer.CaptureToolVersion, $"CaptureToolVersion:{row.Id}", result);
        RequireHash(knownAnswer.CaptureToolSha256, $"CaptureToolHash:{row.Id}", result);
        RejectPlaceholder(knownAnswer.CaptureToolId, $"CaptureToolId:{row.Id}", result);
        RejectPlaceholder(knownAnswer.SourceUnityEditorVersion, $"SourceUnityEditorVersion:{row.Id}", result);
        RejectPlaceholder(knownAnswer.SerializedClipVersion, $"SerializedClipVersion:{row.Id}", result);
        RejectPlaceholder(knownAnswer.AvatarDefinitionSignature, $"KnownAnswerAvatarDefinitionSignature:{row.Id}", result);
        RejectPlaceholder(knownAnswer.CaptureToolIdentity, $"CaptureToolIdentity:{row.Id}", result);
        RejectPlaceholder(knownAnswer.CaptureToolVersion, $"CaptureToolVersion:{row.Id}", result);
        ValidateSpaces(knownAnswer.CoordinateSpaces, $"KnownAnswer:{row.Id}", result);
        ValidateTolerances(knownAnswer.Tolerances, $"KnownAnswer:{row.Id}", result);

        if (files.TryGetValue(avatar.SourceFileId, out HumanoidConformanceSourceFile? avatarFile)
            && !string.Equals(knownAnswer.SourceAvatarSha256, avatarFile.Sha256, StringComparison.OrdinalIgnoreCase))
            Add(result, "StaleKnownAnswerAvatarHash", $"Known answer for '{row.Id}' does not match source avatar hash.");
        if (files.TryGetValue(clip.SourceFileId, out HumanoidConformanceSourceFile? clipFile)
            && !string.Equals(knownAnswer.SourceClipSha256, clipFile.Sha256, StringComparison.OrdinalIgnoreCase))
            Add(result, "StaleKnownAnswerClipHash", $"Known answer for '{row.Id}' does not match source clip hash.");
        if (!string.Equals(knownAnswer.AvatarDefinitionSignature, avatar.AvatarDefinitionSignature, StringComparison.Ordinal))
            Add(result, "StaleKnownAnswerAvatarSignature", $"Known answer for '{row.Id}' does not match avatar definition signature.");
        if (!string.Equals(knownAnswer.AvatarImportSettingsSha256, avatar.ImportSettingsHash, StringComparison.OrdinalIgnoreCase))
            Add(result, "StaleKnownAnswerAvatarImportSettings", $"Known answer for '{row.Id}' does not match avatar import settings hash.");
        if (!string.Equals(knownAnswer.ClipImportSettingsSha256, clip.ImportSettingsHash, StringComparison.OrdinalIgnoreCase))
            Add(result, "StaleKnownAnswerClipImportSettings", $"Known answer for '{row.Id}' does not match clip import settings hash.");
        if (!CoordinateSpacesMatch(knownAnswer.CoordinateSpaces, row.CoordinateSpaces))
            Add(result, "StaleKnownAnswerSpaces", $"Known answer for '{row.Id}' does not match matrix coordinate-space declarations.");
        if (!TolerancesMatch(knownAnswer.Tolerances, row.Tolerances))
            Add(result, "StaleKnownAnswerTolerances", $"Known answer for '{row.Id}' does not match matrix tolerances.");
        if (!captureTools.TryGetValue(knownAnswer.CaptureToolId, out HumanoidConformanceCaptureTool? captureTool))
            Add(result, "CaptureToolReferenceMissing", $"Known answer for '{row.Id}' references unknown capture tool '{knownAnswer.CaptureToolId}'.");
        else
        {
            if (!string.Equals(knownAnswer.CaptureToolIdentity, captureTool.Id, StringComparison.Ordinal))
                Add(result, "StaleCaptureToolIdentity", $"Known answer for '{row.Id}' does not match capture tool '{captureTool.Id}'.");
            if (!string.Equals(knownAnswer.CaptureToolVersion, captureTool.Version, StringComparison.Ordinal))
                Add(result, "StaleCaptureToolVersion", $"Known answer for '{row.Id}' does not match capture tool version.");
            if (!string.Equals(knownAnswer.CaptureToolSha256, captureTool.Sha256, StringComparison.OrdinalIgnoreCase))
                Add(result, "StaleCaptureToolHash", $"Known answer for '{row.Id}' does not match capture tool source identity.");
        }
    }

    private static bool CoordinateSpacesMatch(HumanoidConformanceCoordinateSpaces left, HumanoidConformanceCoordinateSpaces right)
        => string.Equals(left.RootTranslation, right.RootTranslation, StringComparison.Ordinal)
        && string.Equals(left.RootRotation, right.RootRotation, StringComparison.Ordinal)
        && string.Equals(left.Body, right.Body, StringComparison.Ordinal)
        && string.Equals(left.HipsLocal, right.HipsLocal, StringComparison.Ordinal)
        && string.Equals(left.HipsWorld, right.HipsWorld, StringComparison.Ordinal)
        && string.Equals(left.BoneLocalRotation, right.BoneLocalRotation, StringComparison.Ordinal)
        && string.Equals(left.Endpoint, right.Endpoint, StringComparison.Ordinal);

    private static bool TolerancesMatch(HumanoidConformanceTolerances left, HumanoidConformanceTolerances right)
        => left.RootTranslationMeters == right.RootTranslationMeters
        && left.RootRotationDegrees == right.RootRotationDegrees
        && left.EndpointMeters == right.EndpointMeters
        && left.BoneLocalRotationDegrees == right.BoneLocalRotationDegrees
        && left.TenLoopDriftMeters == right.TenLoopDriftMeters
        && left.TenLoopDriftDegrees == right.TenLoopDriftDegrees;

    private static void RequireCoverageCategory(bool present, string category, HumanoidConformanceValidationResult result)
    {
        if (!present)
            Add(result, "AvatarCoverageMissing", $"No public avatar declares required coverage category '{category}'.");
    }

    private static bool IsCompatible(HumanoidConformanceAvatar avatar, HumanoidConformanceClip clip)
    {
        IReadOnlyList<string> avatarCompatibleClips = avatar.CompatibleClipIds ?? [];
        IReadOnlyList<string> clipCompatibleAvatars = clip.CompatibleAvatarIds ?? [];
        bool avatarAllows = avatarCompatibleClips.Count == 0 || avatarCompatibleClips.Contains(clip.Id, StringComparer.Ordinal);
        bool clipAllows = clipCompatibleAvatars.Count == 0 || clipCompatibleAvatars.Contains(avatar.Id, StringComparer.Ordinal);
        return avatarAllows && clipAllows;
    }

    private static void ValidateSourceReference(Dictionary<string, HumanoidConformanceSourceFile> files, string sourceFileId, string owner, HumanoidConformanceValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(sourceFileId) || !files.ContainsKey(sourceFileId))
            Add(result, "SourceReferenceMissing", $"{owner} references unknown source file '{sourceFileId}'.");
    }

    private static void ValidateSpaces(HumanoidConformanceCoordinateSpaces? spaces, string owner, HumanoidConformanceValidationResult result)
    {
        if (spaces is null)
        {
            Add(result, "CoordinateSpacesMissing", $"{owner} does not declare coordinate spaces.");
            return;
        }

        Require(spaces.RootTranslation, $"RootTranslationSpace:{owner}", result);
        Require(spaces.RootRotation, $"RootRotationSpace:{owner}", result);
        Require(spaces.Body, $"BodySpace:{owner}", result);
        Require(spaces.HipsLocal, $"HipsLocalSpace:{owner}", result);
        Require(spaces.HipsWorld, $"HipsWorldSpace:{owner}", result);
        Require(spaces.BoneLocalRotation, $"BoneLocalRotationSpace:{owner}", result);
        Require(spaces.Endpoint, $"EndpointSpace:{owner}", result);
    }

    private static void ValidateTolerances(HumanoidConformanceTolerances? tolerances, string rowId, HumanoidConformanceValidationResult result)
    {
        if (tolerances is null)
        {
            Add(result, "TolerancesMissing", $"Matrix case '{rowId}' has no tolerances.");
            return;
        }

        ValidateTolerance(tolerances.RootTranslationMeters, "RootTranslationMeters", rowId, result);
        ValidateTolerance(tolerances.RootRotationDegrees, "RootRotationDegrees", rowId, result);
        ValidateTolerance(tolerances.EndpointMeters, "EndpointMeters", rowId, result);
        ValidateTolerance(tolerances.BoneLocalRotationDegrees, "BoneLocalRotationDegrees", rowId, result);
        ValidateTolerance(tolerances.TenLoopDriftMeters, "TenLoopDriftMeters", rowId, result);
        ValidateTolerance(tolerances.TenLoopDriftDegrees, "TenLoopDriftDegrees", rowId, result);
    }

    private static void ValidateTolerance(float value, string name, string rowId, HumanoidConformanceValidationResult result)
    {
        if (!float.IsFinite(value) || value < 0.0f)
            Add(result, "ToleranceInvalid", $"Matrix case '{rowId}' tolerance '{name}' must be finite and non-negative.");
    }

    private static void RequireHash(string value, string field, HumanoidConformanceValidationResult result)
    {
        Require(value, field, result);
        if (!string.IsNullOrWhiteSpace(value) && !IsSha256(value))
            Add(result, "HashInvalid", $"{field} must be a SHA-256 hash.");
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(static c => char.IsAsciiHexDigit(c));

    private static void Require(string? value, string field, HumanoidConformanceValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(value))
            Add(result, "RequiredFieldMissing", $"Required field '{field}' is missing.");
    }

    private static void RejectPlaceholder(string? value, string field, HumanoidConformanceValidationResult result)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && value.Contains("PENDING", StringComparison.OrdinalIgnoreCase))
            Add(result, "PlaceholderRejected", $"Required field '{field}' contains a PENDING placeholder.");
    }

    private static string FindRepositoryRoot(string startingPath)
    {
        for (DirectoryInfo? current = new(Path.GetFullPath(startingPath)); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
        throw new DirectoryNotFoundException("Could not locate repository root from humanoid conformance manifest.");
    }
    private static bool TryResolveContainedPath(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return false;

        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        string prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = candidate;
        return true;
    }

    private static void Add(HumanoidConformanceValidationResult result, string code, string message)
        => result.Issues.Add(new HumanoidConformanceValidationIssue { Code = code, Message = message });
}
