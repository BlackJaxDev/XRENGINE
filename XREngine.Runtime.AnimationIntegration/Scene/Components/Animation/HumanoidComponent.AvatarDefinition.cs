using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

public partial class HumanoidComponent
{
    private const float MinimumAcceptedProfileConfidence = 0.6f;

    private HumanoidAvatarDefinitionMetadata _avatarDefinition = new();
    private readonly List<string> _persistedAvatarBindingDiagnostics = [];
    private string _observedSourceModelContentSha256 = string.Empty;
    private bool _sourceIdentityChangedSinceDefinition;

    /// <summary>
    /// Complete versioned persistent target-avatar definition. The component's
    /// settings and live <see cref="BoneDef"/> references feed authoring and
    /// migration; validated runtime playback consumes only its compiled form.
    /// </summary>
    public HumanoidAvatarDefinitionMetadata AvatarDefinition
    {
        get => _avatarDefinition;
        set
        {
            if (SetField(ref _avatarDefinition, NormalizeAvatarDefinition(value)))
                InvalidateCompiledAvatarDefinition();
        }
    }

    private static HumanoidAvatarDefinitionMetadata NormalizeAvatarDefinition(
        HumanoidAvatarDefinitionMetadata? value)
    {
        value ??= new HumanoidAvatarDefinitionMetadata();
        if (value.SchemaVersion < 3
            && value.SourceProvenance == EHumanoidAvatarSourceProvenance.Unknown
            && !string.IsNullOrWhiteSpace(value.SourceModelContentSha256))
        {
            value.SourceProvenance = EHumanoidAvatarSourceProvenance.ImportedModel;
        }
        if (value.SchemaVersion < HumanoidAvatarDefinitionMetadata.CurrentSchemaVersion)
        {
            value.SchemaVersion = HumanoidAvatarDefinitionMetadata.CurrentSchemaVersion;
            value.Status = EHumanoidAvatarDefinitionStatus.NeedsReview;
            value.EditorConfirmed = false;
        }
        value.Source ??= string.Empty;
        value.SkeletonContentSha256 ??= string.Empty;
        value.DefinitionContentSha256 ??= string.Empty;
        value.SourceModelContentSha256 ??= string.Empty;
        value.CoordinateContractId ??= string.Empty;
        value.SolverSettings ??= new HumanoidAvatarSolverSettings();
        value.BodyAxes ??= new HumanoidAvatarBodyAxes();
        value.Bones ??= [];
        value.MuscleLimits ??= [];
        value.TwistChains ??= [];
        value.AuxiliaryBones ??= [];
        if (value.LegacyCalibration is not null)
        {
            value.LegacyCalibration.Source ??= string.Empty;
            value.LegacyCalibration.AvatarName ??= string.Empty;
            value.LegacyCalibration.CalibrationClipName ??= string.Empty;
            value.LegacyCalibration.Bones ??= [];
        }
        for (int i = 0; i < value.Bones.Length; i++)
        {
            HumanoidAvatarBoneBinding binding = value.Bones[i] ?? new HumanoidAvatarBoneBinding();
            binding.NodePath ??= string.Empty;
            binding.NodeName ??= string.Empty;
            binding.StructuralAddress ??= string.Empty;
            binding.StructuralSha256 ??= string.Empty;
            binding.NeutralPoseSha256 ??= string.Empty;
            binding.MappingEvidence ??= string.Empty;
            binding.JointLimit ??= new HumanoidAvatarJointLimit();
            value.Bones[i] = binding;
        }
        for (int i = 0; i < value.TwistChains.Length; i++)
        {
            HumanoidAvatarTwistChain chain = value.TwistChains[i] ?? new HumanoidAvatarTwistChain();
            chain.Name ??= string.Empty;
            chain.AuxiliaryStructuralSha256 ??= [];
            for (int j = 0; j < chain.AuxiliaryStructuralSha256.Length; j++)
                chain.AuxiliaryStructuralSha256[j] ??= string.Empty;
            value.TwistChains[i] = chain;
        }
        for (int i = 0; i < value.AuxiliaryBones.Length; i++)
        {
            HumanoidAvatarAuxiliaryBoneBinding binding = value.AuxiliaryBones[i] ?? new HumanoidAvatarAuxiliaryBoneBinding();
            binding.NodePath ??= string.Empty;
            binding.NodeName ??= string.Empty;
            binding.StructuralAddress ??= string.Empty;
            binding.StructuralSha256 ??= string.Empty;
            value.AuxiliaryBones[i] = binding;
        }
        value.Diagnostics ??= [];
        return value;
    }

    private string _avatarDefinitionPlaybackDiagnostic = string.Empty;

    /// <summary>
    /// Last reason the canonical avatar definition was rejected before
    /// humanoid playback.
    /// </summary>
    public string AvatarDefinitionPlaybackDiagnostic
    {
        get => _avatarDefinitionPlaybackDiagnostic;
        private set => SetField(ref _avatarDefinitionPlaybackDiagnostic, value);
    }

    /// <summary>
    /// Rebuilds the persistent definition from the current semantic bone
    /// assignments and avatar settings.
    /// </summary>
    public void RefreshAvatarDefinition()
        => RefreshAvatarDefinition(profileResult: null);

    private void RefreshAvatarDefinition(AvatarHumanoidProfileBuilder.ProfileResult? profileResult)
    {
        HumanoidAvatarDefinitionMetadata previous = AvatarDefinition;
        HumanoidAvatarSolverSettings solverSettings = BuildSolverSettings(previous);
        HumanoidAvatarBodyAxes bodyAxes = BuildBodyAxes(previous);
        float modelUnitsPerMeter = ResolveModelUnitsPerMeter(previous);
        float humanScale = ResolveHumanScale(previous, modelUnitsPerMeter);
        float muscleInputScale = float.IsFinite(Settings.MuscleInputScale)
            ? Settings.MuscleInputScale
            : 1.0f;
        HumanoidAvatarBoneBinding[] bindings = BuildBoneBindings(previous, solverSettings, profileResult);
        HumanoidAvatarMuscleLimit[] muscleLimits = BuildMuscleLimits(previous);
        HumanoidAvatarTwistChain[] twistChains = BuildTwistChains(previous, solverSettings);
        HumanoidAvatarAuxiliaryBoneBinding[] auxiliaryBones = CopyAuxiliaryBones(previous.AuxiliaryBones);
        HumanoidAvatarLegacyCalibration? legacyCalibration = previous.LegacyCalibration;
        List<string> diagnostics = ValidateDefinition(
            bindings,
            muscleLimits,
            twistChains,
            auxiliaryBones,
            bodyAxes,
            solverSettings,
            humanScale,
            modelUnitsPerMeter,
            muscleInputScale,
            profileResult,
            legacyCalibration);

        string skeletonSignature = ComputeSkeletonSignature(bindings);
        EHumanoidAvatarSourceProvenance sourceProvenance = previous.SourceProvenance;
        string sourceModelSignature = sourceProvenance == EHumanoidAvatarSourceProvenance.ImportedModel
            && !string.IsNullOrEmpty(_observedSourceModelContentSha256)
                ? _observedSourceModelContentSha256
                : previous.SourceModelContentSha256;
        AppendSourceIdentityDiagnostics(sourceProvenance, sourceModelSignature, diagnostics);
        if (_sourceIdentityChangedSinceDefinition)
        {
            diagnostics.Add(
                "Review: the avatar source identity changed after this definition was finalized. " +
                "Review the refreshed mapping before confirming playback.");
        }
        string definitionSignature = ComputeDefinitionSignature(
            skeletonSignature,
            sourceProvenance,
            sourceModelSignature,
            humanScale,
            modelUnitsPerMeter,
            muscleInputScale,
            solverSettings,
            bodyAxes,
            bindings,
            muscleLimits,
            twistChains,
            auxiliaryBones,
            legacyCalibration);

        bool definitionChanged = !string.Equals(
            previous.DefinitionContentSha256,
            definitionSignature,
            StringComparison.Ordinal);
        bool editorConfirmed = previous.EditorConfirmed && !definitionChanged;
        bool hasErrors = HasDiagnosticPrefix(diagnostics, "Error:");
        bool needsReview = HasDiagnosticPrefix(diagnostics, "Review:");
        EHumanoidAvatarDefinitionStatus status = hasErrors
            ? EHumanoidAvatarDefinitionStatus.Invalid
            : needsReview && !editorConfirmed
                ? EHumanoidAvatarDefinitionStatus.NeedsReview
                : EHumanoidAvatarDefinitionStatus.Valid;

        AvatarDefinition = new HumanoidAvatarDefinitionMetadata
        {
            SchemaVersion = HumanoidAvatarDefinitionMetadata.CurrentSchemaVersion,
            AutoMappingAlgorithmVersion = HumanoidAvatarDefinitionMetadata.CurrentAutoMappingAlgorithmVersion,
            DefinitionRevision = definitionChanged
                ? Math.Max(1, previous.DefinitionRevision + 1)
                : Math.Max(1, previous.DefinitionRevision),
            Status = status,
            Source = ResolveDefinitionSource(previous),
            EditorConfirmed = editorConfirmed,
            SkeletonContentSha256 = skeletonSignature,
            DefinitionContentSha256 = definitionSignature,
            SourceProvenance = sourceProvenance,
            SourceModelContentSha256 = sourceModelSignature,
            CoordinateContractId = UnityAnimationCoordinateContract.CurrentContractId,
            HumanScale = humanScale,
            ModelUnitsPerMeter = modelUnitsPerMeter,
            MuscleInputScale = muscleInputScale,
            SolverSettings = solverSettings,
            BodyAxes = bodyAxes,
            Bones = bindings,
            MuscleLimits = muscleLimits,
            TwistChains = twistChains,
            AuxiliaryBones = auxiliaryBones,
            LegacyCalibration = legacyCalibration,
            Diagnostics = [.. diagnostics],
        };
        AvatarDefinitionPlaybackDiagnostic = string.Empty;
        if (status == EHumanoidAvatarDefinitionStatus.Valid)
            TryCompileAvatarDefinition(out _);
    }

    /// <summary>
    /// Explicitly accepts review-level mapping diagnostics. Structural errors
    /// and missing required roles cannot be confirmed away.
    /// </summary>
    public bool ConfirmAvatarDefinition(out string diagnostic)
    {
        RefreshAvatarDefinition();
        HumanoidAvatarDefinitionMetadata definition = AvatarDefinition;
        if (definition.Status == EHumanoidAvatarDefinitionStatus.Invalid)
        {
            diagnostic = definition.Diagnostics.FirstOrDefault() ?? "The avatar definition is invalid.";
            return false;
        }

        definition.EditorConfirmed = true;
        definition.Status = EHumanoidAvatarDefinitionStatus.Valid;
        definition.DefinitionRevision++;
        AvatarDefinitionPlaybackDiagnostic = string.Empty;
        InvalidateCompiledAvatarDefinition();
        bool compiled = TryCompileAvatarDefinition(out diagnostic);
        if (compiled)
            _sourceIdentityChangedSinceDefinition = false;
        return compiled;
    }

    /// <summary>
    /// Assigns a semantic role through the canonical editor/runtime workflow.
    /// An explicit editor assignment is locked against ordinary automatic fill.
    /// </summary>
    public void SetAvatarBoneMapping(
        EHumanoidAvatarBoneRole role,
        SceneNode? node,
        bool lockEditorCorrection = true)
    {
        BoneDef bone = GetBoneDefinition(role);
        bone.Node = node;
        if (node is not null)
            RefreshBoneBindPose(bone);

        Settings.ProfileSource = "manual";
        AvatarHumanoidProfileBuilder.ProfileResult profileResult =
            AvatarHumanoidProfileBuilder.BuildProfile(this);
        RefreshAvatarDefinition(profileResult);

        HumanoidAvatarBoneBinding? binding = FindBinding(AvatarDefinition.Bones, role);
        if (binding is null)
            return;

        binding.MappingSource = EHumanoidAvatarMappingSource.EditorCorrection;
        binding.Confidence = 1.0f;
        binding.ImportedMetadataScore = 0.0f;
        binding.TopologyScore = 1.0f;
        binding.GeometryScore = 1.0f;
        binding.AxisScore = 1.0f;
        binding.SymmetryScore = 1.0f;
        binding.AliasScore = 0.0f;
        binding.MappingEvidence = "Locked editor correction.";
        binding.Locked = lockEditorCorrection;
        RehashDefinitionAfterEditorChange();
    }

    /// <summary>
    /// Changes whether an explicit role binding survives future automatic-map
    /// passes. Unlocking does not discard the current assignment.
    /// </summary>
    public void SetAvatarBoneLock(EHumanoidAvatarBoneRole role, bool locked)
    {
        HumanoidAvatarBoneBinding? binding = FindBinding(AvatarDefinition.Bones, role);
        if (binding is null || binding.Locked == locked)
            return;

        binding.Locked = locked;
        if (locked)
        {
            binding.MappingSource = EHumanoidAvatarMappingSource.EditorCorrection;
            binding.Confidence = 1.0f;
            binding.MappingEvidence = "Locked editor correction.";
        }
        RehashDefinitionAfterEditorChange();
    }

    /// <summary>Returns the live compiled/editor transform for a semantic role.</summary>
    public SceneNode? GetAvatarBoneNode(EHumanoidAvatarBoneRole role)
        => GetBoneDefinition(role).Node;

    /// <summary>
    /// Records a model-source content digest supplied by the generic model
    /// importer. Paths are deliberately not retained as compatibility input.
    /// </summary>
    public void SetSourceModelContentSha256(string sha256)
    {
        string normalized = NormalizeSha256(sha256);
        _observedSourceModelContentSha256 = normalized;

        HumanoidAvatarDefinitionMetadata definition = AvatarDefinition;
        bool hasFinalizedDefinition = definition.DefinitionRevision > 0
            && definition.Bones is { Length: > 0 };
        bool changesFinalizedSource = hasFinalizedDefinition
            && (definition.SourceProvenance != EHumanoidAvatarSourceProvenance.ImportedModel
                || !string.Equals(
                    definition.SourceModelContentSha256,
                    normalized,
                    StringComparison.Ordinal));
        if (changesFinalizedSource)
        {
            _sourceIdentityChangedSinceDefinition = true;
            definition.SourceProvenance = EHumanoidAvatarSourceProvenance.ImportedModel;
            definition.Status = EHumanoidAvatarDefinitionStatus.SourceMismatch;
            definition.EditorConfirmed = false;
            definition.Diagnostics =
            [
                "Error: the imported model source fingerprint changed after the avatar definition was finalized. " +
                "Rerun automatic mapping, review any preserved editor corrections, and confirm the definition.",
            ];
            InvalidateCompiledAvatarDefinition();
            return;
        }

        if (definition.SourceProvenance == EHumanoidAvatarSourceProvenance.ImportedModel
            && string.Equals(definition.SourceModelContentSha256, normalized, StringComparison.Ordinal))
            return;

        definition.SourceProvenance = EHumanoidAvatarSourceProvenance.ImportedModel;
        definition.SourceModelContentSha256 = normalized;
        if (_sceneNodeInitializationComplete)
            RefreshAvatarDefinition();
    }

    /// <summary>
    /// Explicitly declares that the skeleton was authored in XRENGINE and has
    /// no imported source artifact whose content must be fingerprinted.
    /// </summary>
    public void SetRuntimeAuthoredAvatarSource()
    {
        HumanoidAvatarDefinitionMetadata definition = AvatarDefinition;
        bool changed = definition.SourceProvenance != EHumanoidAvatarSourceProvenance.RuntimeAuthoredSkeleton
            || !string.IsNullOrEmpty(definition.SourceModelContentSha256);
        if (!changed)
            return;

        _observedSourceModelContentSha256 = string.Empty;
        _sourceIdentityChangedSinceDefinition = definition.DefinitionRevision > 0
            && definition.Bones is { Length: > 0 };
        definition.SourceProvenance = EHumanoidAvatarSourceProvenance.RuntimeAuthoredSkeleton;
        definition.SourceModelContentSha256 = string.Empty;
        if (_sceneNodeInitializationComplete)
            RefreshAvatarDefinition();
    }

    /// <summary>
    /// Resolves the serialized role bindings into live scene-node references.
    /// Relative names are tried first; a unique structural match permits safe
    /// rebinding after a hierarchy-preserving rename.
    /// </summary>
    private void TryApplyPersistedAvatarDefinitionBindings()
    {
        _persistedAvatarBindingDiagnostics.Clear();
        HumanoidAvatarDefinitionMetadata definition = AvatarDefinition;
        if (definition.SchemaVersion != HumanoidAvatarDefinitionMetadata.CurrentSchemaVersion
            || definition.Bones is not { Length: > 0 })
            return;

        for (int i = 0; i < definition.Bones.Length; i++)
        {
            HumanoidAvatarBoneBinding binding = definition.Bones[i];
            if (string.IsNullOrEmpty(binding.NodePath)
                && string.IsNullOrEmpty(binding.StructuralSha256))
                continue;

            SceneNode? node = ResolveRelativeNodePath(binding.NodePath);
            if (node is not null && !StructuralBindingMatches(binding, node))
                node = null;

            node ??= FindUniqueStructuralMatch(binding);
            if (node is null)
            {
                if (binding.Locked || binding.MappingSource == EHumanoidAvatarMappingSource.ImportedSemanticMetadata)
                {
                    string severity = binding.Required ? "Error" : "Review";
                    _persistedAvatarBindingDiagnostics.Add(
                        $"{severity}: persisted {binding.Role} binding could not be resolved by path or structural identity. " +
                        "The skeleton changed or the correction is now ambiguous.");
                }
                continue;
            }

            BoneDef bone = GetBoneDefinition(binding.Role);
            bone.Node = node;
            if (binding.HasAxisMapping)
                Settings.BoneAxisMappings[node.Name ?? string.Empty] = binding.AxisMapping;
        }
    }

    /// <summary>
    /// Verifies schema, review state, live skeleton identity, settings identity,
    /// and coordinate convention before humanoid data is evaluated.
    /// </summary>
    public bool TryValidateAvatarDefinitionForPlayback(out string diagnostic)
    {
        HumanoidAvatarDefinitionMetadata definition = AvatarDefinition;
        if (definition.SchemaVersion != HumanoidAvatarDefinitionMetadata.CurrentSchemaVersion)
            return RejectAvatarDefinition(
                $"Avatar definition schema {definition.SchemaVersion} is not supported; expected {HumanoidAvatarDefinitionMetadata.CurrentSchemaVersion}.",
                out diagnostic);

        if (definition.Status != EHumanoidAvatarDefinitionStatus.Valid)
        {
            string detail = definition.Diagnostics.FirstOrDefault() ?? "Refresh and validate the humanoid mapping in the editor.";
            return RejectAvatarDefinition(
                $"Avatar definition status is {definition.Status}: {detail}",
                out diagnostic);
        }

        var sourceDiagnostics = new List<string>(1);
        AppendSourceIdentityDiagnostics(
            definition.SourceProvenance,
            definition.SourceModelContentSha256,
            sourceDiagnostics);
        if (sourceDiagnostics.Count > 0)
        {
            definition.Status = EHumanoidAvatarDefinitionStatus.Invalid;
            definition.Diagnostics = [.. sourceDiagnostics];
            return RejectAvatarDefinition(sourceDiagnostics[0], out diagnostic);
        }
        if (definition.SourceProvenance == EHumanoidAvatarSourceProvenance.ImportedModel
            && !string.IsNullOrEmpty(_observedSourceModelContentSha256)
            && !string.Equals(
                definition.SourceModelContentSha256,
                _observedSourceModelContentSha256,
                StringComparison.Ordinal))
        {
            definition.Status = EHumanoidAvatarDefinitionStatus.SourceMismatch;
            return RejectAvatarDefinition(
                "The current imported model fingerprint does not match the finalized avatar definition. " +
                "Rerun mapping and review the definition before playback.",
                out diagnostic);
        }

        if (definition.AutoMappingAlgorithmVersion
                != HumanoidAvatarDefinitionMetadata.CurrentAutoMappingAlgorithmVersion
            && HasAutomaticRoleBindings(definition.Bones))
        {
            definition.Status = EHumanoidAvatarDefinitionStatus.NeedsReview;
            return RejectAvatarDefinition(
                $"Avatar definition uses automatic mapper version {definition.AutoMappingAlgorithmVersion}; " +
                $"rerun mapping with version {HumanoidAvatarDefinitionMetadata.CurrentAutoMappingAlgorithmVersion} and review the result.",
                out diagnostic);
        }

        if (!string.Equals(
            definition.CoordinateContractId,
            UnityAnimationCoordinateContract.CurrentContractId,
            StringComparison.Ordinal))
            return RejectAvatarDefinition(
                $"Avatar coordinate contract '{definition.CoordinateContractId}' does not match runtime contract '{UnityAnimationCoordinateContract.CurrentContractId}'.",
                out diagnostic);

        HumanoidAvatarSolverSettings liveSolverSettings = CopySolverSettings(definition.SolverSettings);
        HumanoidAvatarBoneBinding[] liveBindings = BuildBoneBindings(definition, liveSolverSettings, profileResult: null);
        List<string> liveDiagnostics = ValidateDefinition(
            liveBindings,
            definition.MuscleLimits,
            definition.TwistChains,
            definition.AuxiliaryBones,
            definition.BodyAxes,
            liveSolverSettings,
            definition.HumanScale,
            definition.ModelUnitsPerMeter,
            definition.MuscleInputScale,
            profileResult: null,
            definition.LegacyCalibration);
        if (HasDiagnosticPrefix(liveDiagnostics, "Error:"))
        {
            definition.Status = EHumanoidAvatarDefinitionStatus.Invalid;
            definition.Diagnostics = [.. liveDiagnostics];
            return RejectAvatarDefinition(liveDiagnostics[0], out diagnostic);
        }

        string skeletonSignature = ComputeSkeletonSignature(liveBindings);
        if (!string.Equals(
            skeletonSignature,
            definition.SkeletonContentSha256,
            StringComparison.Ordinal))
        {
            definition.Status = EHumanoidAvatarDefinitionStatus.SkeletonMismatch;
            return RejectAvatarDefinition(
                "The live skeleton no longer matches the finalized avatar definition. Refresh the mapping and review the reported changes.",
                out diagnostic);
        }

        string definitionSignature = ComputeDefinitionSignature(
            skeletonSignature,
            definition.SourceProvenance,
            definition.SourceModelContentSha256,
            definition.HumanScale,
            definition.ModelUnitsPerMeter,
            definition.MuscleInputScale,
            liveSolverSettings,
            definition.BodyAxes,
            liveBindings,
            definition.MuscleLimits,
            definition.TwistChains,
            definition.AuxiliaryBones,
            definition.LegacyCalibration);
        if (!string.Equals(
            definitionSignature,
            definition.DefinitionContentSha256,
            StringComparison.Ordinal))
        {
            definition.Status = EHumanoidAvatarDefinitionStatus.Invalid;
            return RejectAvatarDefinition(
                "Humanoid settings changed after the avatar definition was finalized. Refresh and confirm the definition before playback.",
                out diagnostic);
        }

        AvatarDefinitionPlaybackDiagnostic = string.Empty;
        return TryGetCompiledAvatarDefinition(out _)
            ? AcceptCompiledAvatarDefinition(out diagnostic)
            : TryCompileAvatarDefinition(out diagnostic);
    }

    private static bool AcceptCompiledAvatarDefinition(out string diagnostic)
    {
        diagnostic = string.Empty;
        return true;
    }

    /// <summary>
    /// Produces a path-independent key for cached Unity clip/avatar compilation.
    /// </summary>
    public string CreateAnimationCompatibilityKey(AnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        UnityAnimationImportManifest? manifest = clip.UnityImportManifest;
        if (manifest is null)
            return string.Empty;

        var canonical = new StringBuilder(384);
        AppendCanonical(canonical, manifest.SourceIdentity.SourceContentSha256);
        AppendCanonical(canonical, manifest.SourceIdentity.ImportSettingsSha256);
        AppendCanonical(canonical, manifest.SourceIdentity.SerializedVersion);
        AppendCanonical(canonical, manifest.CoordinateContract.ContractId);
        AppendCanonical(canonical, (int)AvatarDefinition.SourceProvenance);
        AppendCanonical(canonical, AvatarDefinition.SourceModelContentSha256);
        AppendCanonical(canonical, AvatarDefinition.SkeletonContentSha256);
        AppendCanonical(canonical, AvatarDefinition.DefinitionContentSha256);
        AppendCanonical(canonical, AvatarDefinition.DefinitionRevision);
        return ComputeSha256(canonical);
    }

    private HumanoidAvatarBoneBinding[] BuildBoneBindings(
        HumanoidAvatarDefinitionMetadata previous,
        HumanoidAvatarSolverSettings solverSettings,
        AvatarHumanoidProfileBuilder.ProfileResult? profileResult)
    {
        EHumanoidAvatarBoneRole[] roles = Enum.GetValues<EHumanoidAvatarBoneRole>();
        var bindings = new HumanoidAvatarBoneBinding[roles.Length];
        for (int i = 0; i < roles.Length; i++)
        {
            EHumanoidAvatarBoneRole role = roles[i];
            BoneDef bone = GetBoneDefinition(role);
            SceneNode? node = bone.Node;
            HumanoidAvatarBoneBinding? oldBinding = FindBinding(previous.Bones, role);
            string structuralAddress = node is not null && TryGetStructuralAddress(node, out string address)
                ? address
                : string.Empty;
            string structuralHash = node is null
                ? string.Empty
                : ComputeBoneStructuralHash(role, node);
            bool preservesPriorBinding = oldBinding is not null
                && string.Equals(oldBinding.StructuralSha256, structuralHash, StringComparison.Ordinal);
            bool preservesEditorBinding = preservesPriorBinding && oldBinding!.Locked;
            BoneAxisMapping axisMapping = BoneAxisMapping.Default;
            bool hasAxisMapping = preservesPriorBinding && oldBinding!.HasAxisMapping;
            if (hasAxisMapping)
                axisMapping = oldBinding!.AxisMapping;
            else if (node is not null)
                hasAxisMapping = Settings.TryGetBoneAxisMapping(node.Name ?? string.Empty, out axisMapping);

            Matrix4x4 neutralLocal = node is null ? Matrix4x4.Identity : bone.LocalBindPose;
            Matrix4x4 neutralWorld = node is null
                ? Matrix4x4.Identity
                : GetSkeletonRootRelativeBindTransform(bone.WorldBindPose);
            DecomposeNeutralTransform(
                neutralLocal,
                out Vector3 neutralScale,
                out Quaternion neutralRotation,
                out Vector3 neutralPosition);

            HumanoidAvatarRoleMappingEvidence? evidence = _autoMappingEvidence[(int)role];
            bool evidenceMatches = evidence is not null && node is not null;
            EHumanoidAvatarMappingSource mappingSource = preservesPriorBinding
                ? oldBinding!.MappingSource
                : evidenceMatches
                    ? evidence!.Source
                    : EHumanoidAvatarMappingSource.Automatic;
            float confidence = preservesEditorBinding
                ? 1.0f
                : evidenceMatches
                    ? Math.Clamp(evidence!.Confidence, 0.0f, 1.0f)
                    : ResolveBoneConfidence(node, oldBinding, profileResult);

            Quaternion canonicalCorrection = preservesPriorBinding
                ? NormalizeFiniteQuaternion(oldBinding!.CanonicalPoseCorrection)
                : node is not null && TryGetNeutralPoseStoredRotation(node, out Quaternion storedCorrection)
                    ? NormalizeFiniteQuaternion(storedCorrection)
                    : Quaternion.Identity;

            bindings[i] = new HumanoidAvatarBoneBinding
            {
                Role = role,
                Required = IsRequiredRole(role),
                ParentRole = GetParentRole(role),
                NodePath = node is null ? string.Empty : GetRelativeNodePath(node),
                NodeName = node?.Name ?? string.Empty,
                StructuralAddress = structuralAddress,
                StructuralSha256 = structuralHash,
                NeutralPoseSha256 = node is null ? string.Empty : ComputeNeutralPoseHash(neutralLocal),
                NeutralLocalTransform = neutralLocal,
                NeutralWorldTransform = neutralWorld,
                NeutralLocalPosition = neutralPosition,
                NeutralLocalRotation = neutralRotation,
                NeutralLocalScale = neutralScale,
                CanonicalPoseCorrection = canonicalCorrection,
                PreRotation = preservesPriorBinding
                    ? NormalizeFiniteQuaternion(oldBinding!.PreRotation)
                    : Quaternion.Identity,
                PostRotation = preservesPriorBinding
                    ? NormalizeFiniteQuaternion(oldBinding!.PostRotation)
                    : Quaternion.Identity,
                RotationOrder = preservesPriorBinding
                    ? oldBinding!.RotationOrder
                    : EHumanoidAvatarRotationOrder.ZXY,
                HasTranslationDoF = solverSettings.HasTranslationDoF
                    && role == EHumanoidAvatarBoneRole.Hips,
                JointLimit = preservesPriorBinding
                    ? CopyJointLimit(oldBinding!.JointLimit)
                    : CreateDefaultJointLimit(role, node),
                AxisMapping = hasAxisMapping ? axisMapping : BoneAxisMapping.Default,
                HasAxisMapping = hasAxisMapping,
                MappingSource = mappingSource,
                Confidence = confidence,
                ImportedMetadataScore = evidenceMatches
                    ? evidence!.ImportedMetadataScore
                    : oldBinding?.ImportedMetadataScore ?? 0.0f,
                TopologyScore = evidenceMatches ? evidence!.TopologyScore : oldBinding?.TopologyScore ?? 0.0f,
                GeometryScore = evidenceMatches ? evidence!.GeometryScore : oldBinding?.GeometryScore ?? 0.0f,
                AxisScore = evidenceMatches ? evidence!.AxisScore : oldBinding?.AxisScore ?? 0.0f,
                SymmetryScore = evidenceMatches ? evidence!.SymmetryScore : oldBinding?.SymmetryScore ?? 0.0f,
                AliasScore = evidenceMatches ? evidence!.AliasScore : oldBinding?.AliasScore ?? 0.0f,
                MappingEvidence = evidenceMatches
                    ? evidence!.Summary
                    : oldBinding?.MappingEvidence ?? string.Empty,
                Locked = preservesEditorBinding,
            };
        }

        return bindings;
    }

    private List<string> ValidateDefinition(
        HumanoidAvatarBoneBinding[] bindings,
        HumanoidAvatarMuscleLimit[] muscleLimits,
        HumanoidAvatarTwistChain[] twistChains,
        HumanoidAvatarAuxiliaryBoneBinding[] auxiliaryBones,
        HumanoidAvatarBodyAxes bodyAxes,
        HumanoidAvatarSolverSettings solverSettings,
        float humanScale,
        float modelUnitsPerMeter,
        float muscleInputScale,
        AvatarHumanoidProfileBuilder.ProfileResult? profileResult,
        HumanoidAvatarLegacyCalibration? legacyCalibration)
    {
        List<string> diagnostics = [];
        var assignedNodes = new HashSet<SceneNode>(ReferenceEqualityComparer.Instance);
        var nodesByRole = new SceneNode?[CompiledHumanoidAvatarDefinition.RoleCount];
        for (int i = 0; i < bindings.Length; i++)
        {
            HumanoidAvatarBoneBinding binding = bindings[i];
            BoneDef bone = GetBoneDefinition(binding.Role);
            SceneNode? node = bone.Node;
            if (node is null)
            {
                if (binding.Required)
                    diagnostics.Add($"Error: required humanoid role {binding.Role} is not mapped.");
                continue;
            }

            nodesByRole[(int)binding.Role] = node;

            if (!IsDescendantOrSelf(SceneNode, node))
                diagnostics.Add($"Error: role {binding.Role} maps outside the HumanoidComponent hierarchy.");
            if (!assignedNodes.Add(node))
                diagnostics.Add($"Error: scene node '{node.Name}' is assigned to more than one humanoid role.");
            if (!IsFiniteInvertible(binding.NeutralLocalTransform))
                diagnostics.Add($"Error: role {binding.Role} has a non-finite or non-invertible neutral transform.");
            if (!IsFiniteInvertible(binding.NeutralWorldTransform))
                diagnostics.Add($"Error: role {binding.Role} has a non-finite or non-invertible neutral world transform.");
            if (!IsFiniteNonZero(binding.NeutralLocalRotation)
                || !IsFiniteNonZero(binding.CanonicalPoseCorrection)
                || !IsFiniteNonZero(binding.PreRotation)
                || !IsFiniteNonZero(binding.PostRotation))
                diagnostics.Add($"Error: role {binding.Role} has an invalid neutral or joint-basis rotation.");
            if (!IsFiniteJointLimit(binding.JointLimit))
                diagnostics.Add($"Error: role {binding.Role} has invalid joint limits.");
            if (binding.HasAxisMapping && !IsValidAxisMapping(binding.AxisMapping))
                diagnostics.Add($"Error: role {binding.Role} has an invalid local-axis mapping.");
            if (binding.Required && binding.Role != EHumanoidAvatarBoneRole.Hips && !binding.HasAxisMapping)
                diagnostics.Add($"Error: required humanoid role {binding.Role} has no validated local-axis mapping.");

            if (binding.Required
                && binding.MappingSource == EHumanoidAvatarMappingSource.Automatic
                && binding.Confidence < 0.55f)
                diagnostics.Add(
                    $"Error: required role {binding.Role} is ambiguous ({binding.Confidence:P0} confidence): " +
                    $"{binding.MappingEvidence}");
            else if (binding.Required
                && binding.MappingSource == EHumanoidAvatarMappingSource.Automatic
                && binding.Confidence < 0.75f)
                diagnostics.Add(
                    $"Review: role {binding.Role} has only {binding.Confidence:P0} confidence: {binding.MappingEvidence}");
        }

        ValidateRequiredChainOrder(nodesByRole, diagnostics);
        ValidateOptionalDependencies(nodesByRole, diagnostics);
        ValidateAuxiliaryBones(nodesByRole, auxiliaryBones, diagnostics);
        ValidateTwistChains(nodesByRole, twistChains, auxiliaryBones, diagnostics);
        ValidateBilateralSymmetry(nodesByRole, bodyAxes, diagnostics);
        ValidateCanonicalPoseQuality(nodesByRole, bindings, bodyAxes, diagnostics);

        if (!bodyAxes.IsFiniteOrthonormal())
            diagnostics.Add("Error: avatar body axes are not finite and orthonormal.");
        if (!AreFiniteSolverSettings(solverSettings))
            diagnostics.Add("Error: one or more humanoid solver settings are non-finite or outside their valid range.");
        if (!float.IsFinite(humanScale) || humanScale <= 1e-5f)
            diagnostics.Add("Error: avatar human scale is missing or implausible.");
        if (!float.IsFinite(modelUnitsPerMeter) || modelUnitsPerMeter <= 1e-5f)
            diagnostics.Add("Error: avatar model-units-per-meter is missing or implausible.");
        if (!float.IsFinite(muscleInputScale) || MathF.Abs(muscleInputScale) <= 1e-6f)
            diagnostics.Add("Error: avatar muscle input scale is missing or non-finite.");

        ValidateMuscleLimits(muscleLimits, diagnostics);
        ValidateLegacyCalibration(legacyCalibration, diagnostics);

        float confidence = profileResult?.OverallConfidence ?? Settings.ProfileConfidence;
        if (!float.IsFinite(confidence) || confidence < MinimumAcceptedProfileConfidence)
            diagnostics.Add(
                $"Review: automatic avatar mapping confidence is {confidence:P0}; inspect the role mapping and confirm it explicitly.");

        for (int i = 0; i < _avatarMigrationDiagnostics.Count; i++)
            diagnostics.Add(_avatarMigrationDiagnostics[i]);
        for (int i = 0; i < _persistedAvatarBindingDiagnostics.Count; i++)
            diagnostics.Add(_persistedAvatarBindingDiagnostics[i]);

        return diagnostics;
    }

    private HumanoidAvatarSolverSettings BuildSolverSettings(HumanoidAvatarDefinitionMetadata previous)
    {
        HumanoidAvatarSolverSettings settings = CopySolverSettings(previous.SolverSettings);
        settings.UpperArmTwist = Settings.ArmTwistDistribution;
        settings.LowerArmTwist = Settings.ForearmTwistDistribution;
        settings.UpperLegTwist = Settings.UpperLegTwistDistribution;
        settings.LowerLegTwist = Settings.LowerLegTwistDistribution;
        return settings;
    }

    private HumanoidAvatarBodyAxes BuildBodyAxes(HumanoidAvatarDefinitionMetadata previous)
    {
        if (previous.Status != EHumanoidAvatarDefinitionStatus.Uninitialized
            && previous.BodyAxes.IsFiniteOrthonormal())
            return CopyBodyAxes(previous.BodyAxes);

        GetBindBodyBasis(out Vector3 bodyLeft, out Vector3 bodyUp, out Vector3 bodyForward);
        return new HumanoidAvatarBodyAxes
        {
            Right = -bodyLeft,
            Up = bodyUp,
            Forward = bodyForward,
        };
    }

    private static HumanoidAvatarSolverSettings CopySolverSettings(HumanoidAvatarSolverSettings? source)
    {
        source ??= new HumanoidAvatarSolverSettings();
        return new HumanoidAvatarSolverSettings
        {
            UpperArmTwist = source.UpperArmTwist,
            LowerArmTwist = source.LowerArmTwist,
            UpperLegTwist = source.UpperLegTwist,
            LowerLegTwist = source.LowerLegTwist,
            ArmStretch = source.ArmStretch,
            LegStretch = source.LegStretch,
            FeetSpacing = source.FeetSpacing,
            HasTranslationDoF = source.HasTranslationDoF,
        };
    }

    private static HumanoidAvatarBodyAxes CopyBodyAxes(HumanoidAvatarBodyAxes? source)
    {
        source ??= new HumanoidAvatarBodyAxes();
        return new HumanoidAvatarBodyAxes
        {
            Right = source.Right,
            Up = source.Up,
            Forward = source.Forward,
        };
    }

    private float ResolveHumanScale(
        HumanoidAvatarDefinitionMetadata previous,
        float modelUnitsPerMeter)
    {
        if (float.IsFinite(previous.HumanScale) && previous.HumanScale > 1e-5f)
            return previous.HumanScale;

        float units = float.IsFinite(modelUnitsPerMeter) && modelUnitsPerMeter > 1e-5f
            ? modelUnitsPerMeter
            : 1.0f;
        Vector3 hipsPosition = Hips.WorldBindPose.Translation;
        float legLength = 0.0f;
        int legCount = 0;
        if (Left.Foot.Node is not null)
        {
            legLength += Vector3.Distance(hipsPosition, Left.Foot.WorldBindPose.Translation);
            legCount++;
        }
        if (Right.Foot.Node is not null)
        {
            legLength += Vector3.Distance(hipsPosition, Right.Foot.WorldBindPose.Translation);
            legCount++;
        }
        if (legCount > 0 && legLength > 1e-5f)
            return (legLength / legCount) / units;

        return 1.0f / units;
    }

    private float ResolveModelUnitsPerMeter(HumanoidAvatarDefinitionMetadata previous)
    {
        return float.IsFinite(previous.ModelUnitsPerMeter) && previous.ModelUnitsPerMeter > 0.0f
            ? previous.ModelUnitsPerMeter
            : float.IsFinite(_unityProfileUnitsPerMeter) && _unityProfileUnitsPerMeter > 0.0f
                ? _unityProfileUnitsPerMeter
                : 1.0f;
    }

    private string ResolveDefinitionSource(HumanoidAvatarDefinitionMetadata previous)
    {
        if (string.Equals(Settings.ProfileSource, "manual", StringComparison.OrdinalIgnoreCase))
            return "EditorCorrection";
        if (previous.LegacyCalibration is { } legacy)
            return string.IsNullOrWhiteSpace(legacy.Source) ? "LegacyCalibrationProfile" : legacy.Source;
        return string.IsNullOrWhiteSpace(Settings.ProfileSource) ? "Automatic" : Settings.ProfileSource;
    }

    private HumanoidAvatarMuscleLimit[] BuildMuscleLimits(HumanoidAvatarDefinitionMetadata previous)
    {
        List<HumanoidAvatarMuscleLimit> limits = [];
        EHumanoidValue[] values = Enum.GetValues<EHumanoidValue>();
        for (int i = 0; i < values.Length; i++)
        {
            EHumanoidValue value = values[i];
            Vector2 range;
            try
            {
                range = Settings.GetResolvedMuscleRotationDegRange(value);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            HumanoidAvatarMuscleLimit? old = FindMuscleLimit(previous.MuscleLimits, value);
            if (old is not null
                && previous.Status != EHumanoidAvatarDefinitionStatus.Uninitialized
                && !Settings.MuscleRotationDegRanges.ContainsKey(value))
                range = new Vector2(old.NegativeDegrees, old.PositiveDegrees);

            limits.Add(new HumanoidAvatarMuscleLimit
            {
                Muscle = value,
                NegativeDegrees = range.X,
                PositiveDegrees = range.Y,
            });
        }
        return [.. limits];
    }

    private static HumanoidAvatarTwistChain[] BuildTwistChains(
        HumanoidAvatarDefinitionMetadata previous,
        HumanoidAvatarSolverSettings solverSettings)
    {
        if (previous.TwistChains is { Length: > 0 })
        {
            var result = new HumanoidAvatarTwistChain[previous.TwistChains.Length];
            for (int i = 0; i < result.Length; i++)
            {
                HumanoidAvatarTwistChain source = previous.TwistChains[i];
                result[i] = new HumanoidAvatarTwistChain
                {
                    Name = source.Name ?? string.Empty,
                    ProximalRole = source.ProximalRole,
                    DistalRole = source.DistalRole,
                    EndRole = source.EndRole,
                    ProximalDistribution = source.ProximalDistribution,
                    DistalDistribution = source.DistalDistribution,
                    AuxiliaryStructuralSha256 = source.AuxiliaryStructuralSha256 ?? [],
                };
            }
            return result;
        }

        return
        [
            CreateTwistChain("LeftArm", EHumanoidAvatarBoneRole.LeftUpperArm, EHumanoidAvatarBoneRole.LeftLowerArm, EHumanoidAvatarBoneRole.LeftHand, solverSettings.UpperArmTwist, solverSettings.LowerArmTwist),
            CreateTwistChain("RightArm", EHumanoidAvatarBoneRole.RightUpperArm, EHumanoidAvatarBoneRole.RightLowerArm, EHumanoidAvatarBoneRole.RightHand, solverSettings.UpperArmTwist, solverSettings.LowerArmTwist),
            CreateTwistChain("LeftLeg", EHumanoidAvatarBoneRole.LeftUpperLeg, EHumanoidAvatarBoneRole.LeftLowerLeg, EHumanoidAvatarBoneRole.LeftFoot, solverSettings.UpperLegTwist, solverSettings.LowerLegTwist),
            CreateTwistChain("RightLeg", EHumanoidAvatarBoneRole.RightUpperLeg, EHumanoidAvatarBoneRole.RightLowerLeg, EHumanoidAvatarBoneRole.RightFoot, solverSettings.UpperLegTwist, solverSettings.LowerLegTwist),
        ];
    }

    private static HumanoidAvatarTwistChain CreateTwistChain(
        string name,
        EHumanoidAvatarBoneRole proximal,
        EHumanoidAvatarBoneRole distal,
        EHumanoidAvatarBoneRole end,
        float proximalDistribution,
        float distalDistribution)
        => new()
        {
            Name = name,
            ProximalRole = proximal,
            DistalRole = distal,
            EndRole = end,
            ProximalDistribution = proximalDistribution,
            DistalDistribution = distalDistribution,
        };

    private static HumanoidAvatarAuxiliaryBoneBinding[] CopyAuxiliaryBones(
        HumanoidAvatarAuxiliaryBoneBinding[]? source)
    {
        if (source is not { Length: > 0 })
            return [];

        var result = new HumanoidAvatarAuxiliaryBoneBinding[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            HumanoidAvatarAuxiliaryBoneBinding item = source[i];
            result[i] = new HumanoidAvatarAuxiliaryBoneBinding
            {
                Kind = item.Kind,
                ParentRole = item.ParentRole,
                NodePath = item.NodePath ?? string.Empty,
                NodeName = item.NodeName ?? string.Empty,
                StructuralAddress = item.StructuralAddress ?? string.Empty,
                StructuralSha256 = item.StructuralSha256 ?? string.Empty,
                NeutralLocalTransform = item.NeutralLocalTransform,
                LocalAxis = item.LocalAxis,
                DistributionWeight = item.DistributionWeight,
                Locked = item.Locked,
            };
        }
        return result;
    }

    private float ResolveBoneConfidence(
        SceneNode? node,
        HumanoidAvatarBoneBinding? previous,
        AvatarHumanoidProfileBuilder.ProfileResult? profileResult)
    {
        if (node is null)
            return 0.0f;
        if (profileResult is not null
            && profileResult.BoneEntries.TryGetValue(node, out var entry))
            return Math.Clamp(entry.Confidence, 0.0f, 1.0f);
        if (previous is not null
            && string.Equals(previous.NodePath, GetRelativeNodePath(node), StringComparison.Ordinal))
            return previous.Confidence;
        return 0.0f;
    }

    private Matrix4x4 GetSkeletonRootRelativeBindTransform(Matrix4x4 worldBindTransform)
    {
        Matrix4x4 rootBind = GetHumanoidBindWorldPose(SceneNode);
        return Matrix4x4.Invert(rootBind, out Matrix4x4 inverseRoot)
            ? worldBindTransform * inverseRoot
            : worldBindTransform;
    }

    private void RehashDefinitionAfterEditorChange()
    {
        HumanoidAvatarDefinitionMetadata definition = AvatarDefinition;
        definition.SkeletonContentSha256 = ComputeSkeletonSignature(definition.Bones);
        definition.DefinitionContentSha256 = ComputeDefinitionSignature(
            definition.SkeletonContentSha256,
            definition.SourceProvenance,
            definition.SourceModelContentSha256,
            definition.HumanScale,
            definition.ModelUnitsPerMeter,
            definition.MuscleInputScale,
            definition.SolverSettings,
            definition.BodyAxes,
            definition.Bones,
            definition.MuscleLimits,
            definition.TwistChains,
            definition.AuxiliaryBones,
            definition.LegacyCalibration);
        definition.DefinitionRevision++;
        definition.EditorConfirmed = false;
        if (definition.Status == EHumanoidAvatarDefinitionStatus.Valid)
            definition.Status = EHumanoidAvatarDefinitionStatus.NeedsReview;
        InvalidateCompiledAvatarDefinition();
    }

    private bool RejectAvatarDefinition(string message, out string diagnostic)
    {
        AvatarDefinitionPlaybackDiagnostic = message;
        diagnostic = message;
        return false;
    }

    private string ComputeDefinitionSignature(
        string skeletonSignature,
        EHumanoidAvatarSourceProvenance sourceProvenance,
        string sourceModelSignature,
        float humanScale,
        float modelUnitsPerMeter,
        float muscleInputScale,
        HumanoidAvatarSolverSettings solverSettings,
        HumanoidAvatarBodyAxes bodyAxes,
        HumanoidAvatarBoneBinding[] bindings,
        HumanoidAvatarMuscleLimit[] muscleLimits,
        HumanoidAvatarTwistChain[] twistChains,
        HumanoidAvatarAuxiliaryBoneBinding[] auxiliaryBones,
        HumanoidAvatarLegacyCalibration? legacyCalibration)
    {
        var canonical = new StringBuilder(16384);
        AppendCanonical(canonical, HumanoidAvatarDefinitionMetadata.CurrentSchemaVersion);
        AppendCanonical(canonical, HumanoidAvatarDefinitionMetadata.CurrentAutoMappingAlgorithmVersion);
        AppendCanonical(canonical, UnityAnimationCoordinateContract.CurrentContractId);
        AppendCanonical(canonical, skeletonSignature);
        AppendCanonical(canonical, (int)sourceProvenance);
        AppendCanonical(canonical, sourceModelSignature);
        AppendCanonical(canonical, humanScale);
        AppendCanonical(canonical, modelUnitsPerMeter);
        AppendCanonical(canonical, muscleInputScale);
        AppendSolverSettings(canonical, solverSettings);
        AppendVector(canonical, bodyAxes.Right);
        AppendVector(canonical, bodyAxes.Up);
        AppendVector(canonical, bodyAxes.Forward);

        for (int i = 0; i < bindings.Length; i++)
        {
            HumanoidAvatarBoneBinding binding = bindings[i];
            AppendCanonical(canonical, (int)binding.Role);
            AppendCanonical(canonical, binding.StructuralSha256);
            AppendCanonical(canonical, binding.Required);
            AppendCanonical(canonical, binding.ParentRole.HasValue ? (int)binding.ParentRole.Value : -1);
            AppendMatrix(canonical, binding.NeutralLocalTransform);
            AppendMatrix(canonical, binding.NeutralWorldTransform);
            AppendQuaternion(canonical, binding.CanonicalPoseCorrection);
            AppendQuaternion(canonical, binding.PreRotation);
            AppendQuaternion(canonical, binding.PostRotation);
            AppendCanonical(canonical, (int)binding.RotationOrder);
            AppendCanonical(canonical, binding.HasTranslationDoF);
            AppendJointLimit(canonical, binding.JointLimit);
            AppendCanonical(canonical, binding.HasAxisMapping);
            if (binding.HasAxisMapping)
                AppendAxisMapping(canonical, binding.AxisMapping);
            AppendCanonical(canonical, (int)binding.MappingSource);
            AppendCanonical(canonical, binding.Locked);
            AppendCanonical(canonical, binding.Confidence);
            AppendCanonical(canonical, binding.ImportedMetadataScore);
            AppendCanonical(canonical, binding.TopologyScore);
            AppendCanonical(canonical, binding.GeometryScore);
            AppendCanonical(canonical, binding.AxisScore);
            AppendCanonical(canonical, binding.SymmetryScore);
            AppendCanonical(canonical, binding.AliasScore);
        }

        for (int i = 0; i < muscleLimits.Length; i++)
        {
            HumanoidAvatarMuscleLimit limit = muscleLimits[i];
            AppendCanonical(canonical, (int)limit.Muscle);
            AppendCanonical(canonical, limit.NegativeDegrees);
            AppendCanonical(canonical, limit.PositiveDegrees);
        }

        for (int i = 0; i < twistChains.Length; i++)
        {
            HumanoidAvatarTwistChain chain = twistChains[i];
            AppendCanonical(canonical, chain.Name);
            AppendCanonical(canonical, (int)chain.ProximalRole);
            AppendCanonical(canonical, (int)chain.DistalRole);
            AppendCanonical(canonical, (int)chain.EndRole);
            AppendCanonical(canonical, chain.ProximalDistribution);
            AppendCanonical(canonical, chain.DistalDistribution);
            string[] auxiliaryHashes = chain.AuxiliaryStructuralSha256 ?? [];
            for (int j = 0; j < auxiliaryHashes.Length; j++)
                AppendCanonical(canonical, auxiliaryHashes[j]);
        }

        for (int i = 0; i < auxiliaryBones.Length; i++)
        {
            HumanoidAvatarAuxiliaryBoneBinding auxiliary = auxiliaryBones[i];
            AppendCanonical(canonical, (int)auxiliary.Kind);
            AppendCanonical(canonical, (int)auxiliary.ParentRole);
            AppendCanonical(canonical, auxiliary.StructuralSha256);
            AppendMatrix(canonical, auxiliary.NeutralLocalTransform);
            AppendVector(canonical, auxiliary.LocalAxis);
            AppendCanonical(canonical, auxiliary.DistributionWeight);
            AppendCanonical(canonical, auxiliary.Locked);
        }

        AppendLegacyCalibration(canonical, legacyCalibration);
        return ComputeSha256(canonical);
    }

    private static string ComputeSkeletonSignature(HumanoidAvatarBoneBinding[] bindings)
    {
        var canonical = new StringBuilder(bindings.Length * 96);
        for (int i = 0; i < bindings.Length; i++)
        {
            HumanoidAvatarBoneBinding binding = bindings[i];
            AppendCanonical(canonical, (int)binding.Role);
            AppendCanonical(canonical, binding.Required);
            AppendCanonical(canonical, binding.StructuralSha256);
        }
        return ComputeSha256(canonical);
    }

    private string ComputeBoneStructuralHash(
        EHumanoidAvatarBoneRole role,
        SceneNode node)
    {
        if (!TryGetStructuralAddress(node, out string structuralAddress))
            return string.Empty;

        var canonical = new StringBuilder(320);
        AppendCanonical(canonical, (int)role);
        AppendCanonical(canonical, structuralAddress);
        AppendCanonical(canonical, GetHierarchyDepth(node));
        SceneNode? current = node;
        while (current is not null && !ReferenceEquals(current, SceneNode))
        {
            AppendCanonical(canonical, current.Transform.Children.Count);
            current = current.Parent;
        }
        AppendSubtreeTopology(canonical, node, remainingDepth: 3);
        return ComputeSha256(canonical);
    }

    private string ComputeAuxiliaryStructuralHash(
        EHumanoidAvatarAuxiliaryBoneKind kind,
        EHumanoidAvatarBoneRole parentRole,
        SceneNode node)
    {
        if (!TryGetStructuralAddress(node, out string structuralAddress))
            return string.Empty;

        var canonical = new StringBuilder(320);
        AppendCanonical(canonical, (int)kind);
        AppendCanonical(canonical, (int)parentRole);
        AppendCanonical(canonical, structuralAddress);
        AppendCanonical(canonical, GetHierarchyDepth(node));
        SceneNode? current = node;
        while (current is not null && !ReferenceEquals(current, SceneNode))
        {
            AppendCanonical(canonical, current.Transform.Children.Count);
            current = current.Parent;
        }
        AppendSubtreeTopology(canonical, node, remainingDepth: 3);
        return ComputeSha256(canonical);
    }

    private static string ComputeNeutralPoseHash(Matrix4x4 neutralLocalTransform)
    {
        var canonical = new StringBuilder(256);
        AppendMatrix(canonical, neutralLocalTransform);
        return ComputeSha256(canonical);
    }

    private bool StructuralBindingMatches(HumanoidAvatarBoneBinding binding, SceneNode node)
        => string.Equals(
            binding.StructuralSha256,
            ComputeBoneStructuralHash(binding.Role, node),
            StringComparison.Ordinal);

    private int GetHierarchyDepth(SceneNode node)
    {
        int depth = 0;
        SceneNode? current = node;
        while (current is not null && !ReferenceEquals(current, SceneNode))
        {
            depth++;
            current = current.Parent;
        }
        return current is null ? -1 : depth;
    }

    private static void AppendSubtreeTopology(StringBuilder canonical, SceneNode node, int remainingDepth)
    {
        int childCount = node.Transform.Children.Count;
        AppendCanonical(canonical, childCount);
        if (remainingDepth <= 0 || childCount == 0)
            return;

        Span<int> descendantCounts = childCount <= 32
            ? stackalloc int[childCount]
            : new int[childCount];
        for (int i = 0; i < childCount; i++)
        {
            SceneNode? child = node.Transform.Children[i].SceneNode;
            descendantCounts[i] = child?.Transform.Children.Count ?? -1;
        }
        descendantCounts.Sort();
        for (int i = 0; i < descendantCounts.Length; i++)
            AppendCanonical(canonical, descendantCounts[i]);
    }

    private SceneNode? FindUniqueStructuralMatch(HumanoidAvatarBoneBinding binding)
    {
        SceneNode? match = null;
        int matchCount = 0;
        SceneNode.IterateHierarchy(node =>
        {
            if (StructuralBindingMatches(binding, node))
            {
                match = node;
                matchCount++;
            }
        });
        return matchCount == 1 ? match : null;
    }

    private SceneNode? ResolveAuxiliaryBoneNode(HumanoidAvatarAuxiliaryBoneBinding binding)
    {
        SceneNode? node = ResolveRelativeNodePath(binding.NodePath);
        if (node is not null && !AuxiliaryStructuralBindingMatches(binding, node))
            node = null;
        return node ?? FindUniqueAuxiliaryStructuralMatch(binding);
    }

    private SceneNode? FindUniqueAuxiliaryStructuralMatch(HumanoidAvatarAuxiliaryBoneBinding binding)
    {
        SceneNode? match = null;
        int matchCount = 0;
        SceneNode.IterateHierarchy(node =>
        {
            if (!AuxiliaryStructuralBindingMatches(binding, node))
                return;
            match = node;
            matchCount++;
        });
        return matchCount == 1 ? match : null;
    }

    private bool AuxiliaryStructuralBindingMatches(
        HumanoidAvatarAuxiliaryBoneBinding binding,
        SceneNode node)
        => !string.IsNullOrWhiteSpace(binding.StructuralSha256)
        && string.Equals(
            binding.StructuralSha256,
            ComputeAuxiliaryStructuralHash(binding.Kind, binding.ParentRole, node),
            StringComparison.Ordinal);

    private string GetRelativeNodePath(SceneNode node)
    {
        var segments = new Stack<string>();
        SceneNode? current = node;
        while (current is not null && !ReferenceEquals(current, SceneNode))
        {
            segments.Push(Uri.EscapeDataString(current.Name ?? string.Empty));
            current = current.Parent;
        }
        return current is null ? string.Empty : string.Join('/', segments);
    }

    private SceneNode? ResolveRelativeNodePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return SceneNode;

        SceneNode current = SceneNode;
        string[] segments = path.Split('/', StringSplitOptions.None);
        for (int i = 0; i < segments.Length; i++)
        {
            string name = Uri.UnescapeDataString(segments[i]);
            SceneNode? next = null;
            int matches = 0;
            foreach (TransformBase childTransform in current.Transform.Children)
            {
                SceneNode? child = childTransform.SceneNode;
                if (child is null || !string.Equals(child.Name, name, StringComparison.Ordinal))
                    continue;
                next = child;
                matches++;
            }

            if (matches != 1 || next is null)
                return null;
            current = next;
        }
        return current;
    }

    private bool TryGetStructuralAddress(SceneNode node, out string address)
    {
        var indices = new Stack<int>();
        SceneNode? current = node;
        while (current is not null && !ReferenceEquals(current, SceneNode))
        {
            SceneNode? parent = current.Parent;
            if (parent is null)
            {
                address = string.Empty;
                return false;
            }

            int childIndex = -1;
            for (int i = 0; i < parent.Transform.Children.Count; i++)
            {
                if (ReferenceEquals(parent.Transform.Children[i].SceneNode, current))
                {
                    childIndex = i;
                    break;
                }
            }
            if (childIndex < 0)
            {
                address = string.Empty;
                return false;
            }

            indices.Push(childIndex);
            current = parent;
        }

        if (current is null)
        {
            address = string.Empty;
            return false;
        }

        address = indices.Count == 0 ? "root" : string.Join('.', indices);
        return true;
    }

    private static bool IsDescendantOrSelf(SceneNode root, SceneNode node)
    {
        SceneNode? current = node;
        while (current is not null)
        {
            if (ReferenceEquals(current, root))
                return true;
            current = current.Parent;
        }
        return false;
    }

    private static bool IsFiniteInvertible(Matrix4x4 value)
    {
        if (!IsFinite(value))
            return false;
        float determinant = value.GetDeterminant();
        return float.IsFinite(determinant) && MathF.Abs(determinant) > 1e-8f;
    }

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
        && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
        && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private static bool IsValidAxisMapping(BoneAxisMapping mapping)
        => mapping.TwistAxis is >= 0 and <= 2
        && mapping.FrontBackAxis is >= 0 and <= 2
        && mapping.LeftRightAxis is >= 0 and <= 2
        && mapping.TwistAxis != mapping.FrontBackAxis
        && mapping.TwistAxis != mapping.LeftRightAxis
        && mapping.FrontBackAxis != mapping.LeftRightAxis
        && Math.Abs(mapping.TwistSign) == 1
        && Math.Abs(mapping.FrontBackSign) == 1
        && Math.Abs(mapping.LeftRightSign) == 1;

    private static bool AreFiniteSolverSettings(HumanoidAvatarSolverSettings settings)
        => IsNormalized(settings.UpperArmTwist)
        && IsNormalized(settings.LowerArmTwist)
        && IsNormalized(settings.UpperLegTwist)
        && IsNormalized(settings.LowerLegTwist)
        && IsNonNegativeFinite(settings.ArmStretch)
        && IsNonNegativeFinite(settings.LegStretch)
        && IsNonNegativeFinite(settings.FeetSpacing);

    private static bool IsNormalized(float value)
        => float.IsFinite(value) && value is >= 0.0f and <= 1.0f;

    private static bool IsNonNegativeFinite(float value)
        => float.IsFinite(value) && value >= 0.0f;

    private static bool HasDiagnosticPrefix(List<string> diagnostics, string prefix)
    {
        for (int i = 0; i < diagnostics.Count; i++)
            if (diagnostics[i].StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static HumanoidAvatarBoneBinding? FindBinding(
        HumanoidAvatarBoneBinding[]? bindings,
        EHumanoidAvatarBoneRole role)
    {
        if (bindings is null)
            return null;
        for (int i = 0; i < bindings.Length; i++)
            if (bindings[i].Role == role)
                return bindings[i];
        return null;
    }

    private static bool HasAutomaticRoleBindings(HumanoidAvatarBoneBinding[] bindings)
    {
        for (int i = 0; i < bindings.Length; i++)
            if (bindings[i].MappingSource == EHumanoidAvatarMappingSource.Automatic)
                return true;
        return false;
    }

    private static void AppendSourceIdentityDiagnostics(
        EHumanoidAvatarSourceProvenance provenance,
        string sourceModelContentSha256,
        List<string> diagnostics)
    {
        switch (provenance)
        {
            case EHumanoidAvatarSourceProvenance.RuntimeAuthoredSkeleton:
                if (!string.IsNullOrEmpty(sourceModelContentSha256))
                {
                    diagnostics.Add(
                        "Error: a runtime-authored avatar source must not carry an imported model fingerprint. " +
                        "Choose the imported-model source contract or clear the digest.");
                }
                return;

            case EHumanoidAvatarSourceProvenance.ImportedModel:
                if (!IsSha256Digest(sourceModelContentSha256))
                {
                    diagnostics.Add(
                        "Error: an imported avatar definition requires the current model-source SHA-256 fingerprint. " +
                        "Reimport the model through a source-aware avatar setup path before playback.");
                }
                return;

            case EHumanoidAvatarSourceProvenance.Unknown:
                diagnostics.Add(
                    "Error: avatar source provenance is unknown. The importer must supply a model fingerprint, " +
                    "or the author must explicitly mark this as a runtime-authored skeleton.");
                return;

            default:
                diagnostics.Add($"Error: avatar source provenance value {(int)provenance} is not supported.");
                return;
        }
    }

    private static bool IsSha256Digest(string value)
    {
        if (value.Length != 64)
            return false;
        for (int i = 0; i < value.Length; i++)
            if (!char.IsAsciiHexDigit(value[i]))
                return false;
        return true;
    }

    private static string NormalizeSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 64)
            throw new ArgumentException("A SHA-256 digest must contain exactly 64 hexadecimal characters.", nameof(value));
        try
        {
            _ = Convert.FromHexString(normalized);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The supplied SHA-256 digest is not hexadecimal.", nameof(value), exception);
        }
        return normalized;
    }

    private static string ComputeSha256(StringBuilder canonical)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));

    private static void AppendCanonical(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length).Append(':').Append(value).Append(';');
    }

    private static void AppendCanonical(StringBuilder builder, int value)
        => AppendCanonical(builder, value.ToString(CultureInfo.InvariantCulture));

    private static void AppendCanonical(StringBuilder builder, bool value)
        => AppendCanonical(builder, value ? "1" : "0");

    private static void AppendCanonical(StringBuilder builder, float value)
        => AppendCanonical(builder, value.ToString("R", CultureInfo.InvariantCulture));

    private static void AppendVector(StringBuilder builder, Vector3 value)
    {
        AppendCanonical(builder, value.X);
        AppendCanonical(builder, value.Y);
        AppendCanonical(builder, value.Z);
    }

    private static void AppendVector2(StringBuilder builder, Vector2 value)
    {
        AppendCanonical(builder, value.X);
        AppendCanonical(builder, value.Y);
    }

    private static void AppendQuaternion(StringBuilder builder, Quaternion value)
    {
        AppendCanonical(builder, value.X);
        AppendCanonical(builder, value.Y);
        AppendCanonical(builder, value.Z);
        AppendCanonical(builder, value.W);
    }

    private static void AppendMatrix(StringBuilder builder, Matrix4x4 value)
    {
        AppendCanonical(builder, value.M11); AppendCanonical(builder, value.M12); AppendCanonical(builder, value.M13); AppendCanonical(builder, value.M14);
        AppendCanonical(builder, value.M21); AppendCanonical(builder, value.M22); AppendCanonical(builder, value.M23); AppendCanonical(builder, value.M24);
        AppendCanonical(builder, value.M31); AppendCanonical(builder, value.M32); AppendCanonical(builder, value.M33); AppendCanonical(builder, value.M34);
        AppendCanonical(builder, value.M41); AppendCanonical(builder, value.M42); AppendCanonical(builder, value.M43); AppendCanonical(builder, value.M44);
    }

    private static void AppendAxisMapping(StringBuilder builder, BoneAxisMapping mapping)
    {
        AppendCanonical(builder, mapping.TwistAxis);
        AppendCanonical(builder, mapping.TwistSign);
        AppendCanonical(builder, mapping.FrontBackAxis);
        AppendCanonical(builder, mapping.FrontBackSign);
        AppendCanonical(builder, mapping.LeftRightAxis);
        AppendCanonical(builder, mapping.LeftRightSign);
    }

    private static void AppendSolverSettings(StringBuilder builder, HumanoidAvatarSolverSettings settings)
    {
        AppendCanonical(builder, settings.UpperArmTwist);
        AppendCanonical(builder, settings.LowerArmTwist);
        AppendCanonical(builder, settings.UpperLegTwist);
        AppendCanonical(builder, settings.LowerLegTwist);
        AppendCanonical(builder, settings.ArmStretch);
        AppendCanonical(builder, settings.LegStretch);
        AppendCanonical(builder, settings.FeetSpacing);
        AppendCanonical(builder, settings.HasTranslationDoF);
    }

    private BoneDef GetBoneDefinition(EHumanoidAvatarBoneRole role)
        => role switch
        {
            EHumanoidAvatarBoneRole.Hips => Hips,
            EHumanoidAvatarBoneRole.Spine => Spine,
            EHumanoidAvatarBoneRole.Chest => Chest,
            EHumanoidAvatarBoneRole.UpperChest => UpperChest,
            EHumanoidAvatarBoneRole.Neck => Neck,
            EHumanoidAvatarBoneRole.Head => Head,
            EHumanoidAvatarBoneRole.Jaw => Jaw,
            EHumanoidAvatarBoneRole.LeftEye => Left.Eye,
            EHumanoidAvatarBoneRole.RightEye => Right.Eye,
            EHumanoidAvatarBoneRole.LeftShoulder => Left.Shoulder,
            EHumanoidAvatarBoneRole.LeftUpperArm => Left.Arm,
            EHumanoidAvatarBoneRole.LeftLowerArm => Left.Elbow,
            EHumanoidAvatarBoneRole.LeftHand => Left.Wrist,
            EHumanoidAvatarBoneRole.RightShoulder => Right.Shoulder,
            EHumanoidAvatarBoneRole.RightUpperArm => Right.Arm,
            EHumanoidAvatarBoneRole.RightLowerArm => Right.Elbow,
            EHumanoidAvatarBoneRole.RightHand => Right.Wrist,
            EHumanoidAvatarBoneRole.LeftUpperLeg => Left.Leg,
            EHumanoidAvatarBoneRole.LeftLowerLeg => Left.Knee,
            EHumanoidAvatarBoneRole.LeftFoot => Left.Foot,
            EHumanoidAvatarBoneRole.LeftToes => Left.Toes,
            EHumanoidAvatarBoneRole.RightUpperLeg => Right.Leg,
            EHumanoidAvatarBoneRole.RightLowerLeg => Right.Knee,
            EHumanoidAvatarBoneRole.RightFoot => Right.Foot,
            EHumanoidAvatarBoneRole.RightToes => Right.Toes,
            EHumanoidAvatarBoneRole.LeftThumbProximal => Left.Hand.Thumb.Proximal,
            EHumanoidAvatarBoneRole.LeftThumbIntermediate => Left.Hand.Thumb.Intermediate,
            EHumanoidAvatarBoneRole.LeftThumbDistal => Left.Hand.Thumb.Distal,
            EHumanoidAvatarBoneRole.LeftIndexProximal => Left.Hand.Index.Proximal,
            EHumanoidAvatarBoneRole.LeftIndexIntermediate => Left.Hand.Index.Intermediate,
            EHumanoidAvatarBoneRole.LeftIndexDistal => Left.Hand.Index.Distal,
            EHumanoidAvatarBoneRole.LeftMiddleProximal => Left.Hand.Middle.Proximal,
            EHumanoidAvatarBoneRole.LeftMiddleIntermediate => Left.Hand.Middle.Intermediate,
            EHumanoidAvatarBoneRole.LeftMiddleDistal => Left.Hand.Middle.Distal,
            EHumanoidAvatarBoneRole.LeftRingProximal => Left.Hand.Ring.Proximal,
            EHumanoidAvatarBoneRole.LeftRingIntermediate => Left.Hand.Ring.Intermediate,
            EHumanoidAvatarBoneRole.LeftRingDistal => Left.Hand.Ring.Distal,
            EHumanoidAvatarBoneRole.LeftLittleProximal => Left.Hand.Pinky.Proximal,
            EHumanoidAvatarBoneRole.LeftLittleIntermediate => Left.Hand.Pinky.Intermediate,
            EHumanoidAvatarBoneRole.LeftLittleDistal => Left.Hand.Pinky.Distal,
            EHumanoidAvatarBoneRole.RightThumbProximal => Right.Hand.Thumb.Proximal,
            EHumanoidAvatarBoneRole.RightThumbIntermediate => Right.Hand.Thumb.Intermediate,
            EHumanoidAvatarBoneRole.RightThumbDistal => Right.Hand.Thumb.Distal,
            EHumanoidAvatarBoneRole.RightIndexProximal => Right.Hand.Index.Proximal,
            EHumanoidAvatarBoneRole.RightIndexIntermediate => Right.Hand.Index.Intermediate,
            EHumanoidAvatarBoneRole.RightIndexDistal => Right.Hand.Index.Distal,
            EHumanoidAvatarBoneRole.RightMiddleProximal => Right.Hand.Middle.Proximal,
            EHumanoidAvatarBoneRole.RightMiddleIntermediate => Right.Hand.Middle.Intermediate,
            EHumanoidAvatarBoneRole.RightMiddleDistal => Right.Hand.Middle.Distal,
            EHumanoidAvatarBoneRole.RightRingProximal => Right.Hand.Ring.Proximal,
            EHumanoidAvatarBoneRole.RightRingIntermediate => Right.Hand.Ring.Intermediate,
            EHumanoidAvatarBoneRole.RightRingDistal => Right.Hand.Ring.Distal,
            EHumanoidAvatarBoneRole.RightLittleProximal => Right.Hand.Pinky.Proximal,
            EHumanoidAvatarBoneRole.RightLittleIntermediate => Right.Hand.Pinky.Intermediate,
            EHumanoidAvatarBoneRole.RightLittleDistal => Right.Hand.Pinky.Distal,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown humanoid role."),
        };

    private static bool IsRequiredRole(EHumanoidAvatarBoneRole role)
        => role is EHumanoidAvatarBoneRole.Hips
            or EHumanoidAvatarBoneRole.Spine
            or EHumanoidAvatarBoneRole.Head
            or EHumanoidAvatarBoneRole.LeftUpperArm
            or EHumanoidAvatarBoneRole.LeftLowerArm
            or EHumanoidAvatarBoneRole.LeftHand
            or EHumanoidAvatarBoneRole.RightUpperArm
            or EHumanoidAvatarBoneRole.RightLowerArm
            or EHumanoidAvatarBoneRole.RightHand
            or EHumanoidAvatarBoneRole.LeftUpperLeg
            or EHumanoidAvatarBoneRole.LeftLowerLeg
            or EHumanoidAvatarBoneRole.LeftFoot
            or EHumanoidAvatarBoneRole.RightUpperLeg
            or EHumanoidAvatarBoneRole.RightLowerLeg
            or EHumanoidAvatarBoneRole.RightFoot;
}
