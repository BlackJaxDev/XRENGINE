using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using XREngine;
using XREngine.Animation;
using XREngine.Animation.IK;
using XREngine.Animation.Importers;
using XREngine.Components.Animation;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace HumanoidConformanceRunner;

/// <summary>Executes one content-addressed corpus asset through its production import/runtime path.</summary>
internal static class HumanoidConformanceAssetCheckExecutor
{
    private const HumanoidConformanceCapability SourceEncodingCapabilities =
        HumanoidConformanceCapability.Compressed |
        HumanoidConformanceCapability.Dense |
        HumanoidConformanceCapability.Streamed;

    public static HumanoidConformanceAssetCheckResult Run(
        HumanoidConformanceManifest manifest,
        string manifestRoot,
        HumanoidConformanceAssetCheck check)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestRoot);
        ArgumentNullException.ThrowIfNull(check);

        try
        {
            return check.Kind switch
            {
                HumanoidConformanceAssetCheckKind.HumanoidMatrixAvatar => RunHumanoidAvatar(manifest, manifestRoot, check),
                HumanoidConformanceAssetCheckKind.ValidModelImport => RunModelImport(manifest, manifestRoot, check),
                HumanoidConformanceAssetCheckKind.ExpectedMalformedModelImport => RunModelImport(manifest, manifestRoot, check),
                HumanoidConformanceAssetCheckKind.AnimationBehaviorAndImport => RunAnimation(manifest, manifestRoot, check),
                HumanoidConformanceAssetCheckKind.AnimationImport => RunAnimation(manifest, manifestRoot, check),
                _ => throw new InvalidDataException($"Unsupported asset-check kind '{check.Kind}'."),
            };
        }
        catch (Exception ex)
        {
            return new HumanoidConformanceAssetCheckResult
            {
                AssetCheckId = check.Id,
                Passed = false,
                Diagnostic = ex.Message,
            };
        }
    }

    private static HumanoidConformanceAssetCheckResult RunModelImport(
        HumanoidConformanceManifest manifest,
        string root,
        HumanoidConformanceAssetCheck check)
    {
        string path = ResolveSourcePath(manifest, root, check.SourceFileId);
        using var importer = new ModelAssetImporter(path, onCompleted: null, materialFactory: null)
        {
            ImportOptions = CreateModelImportOptions(),
        };
        SceneNode hierarchy = importer.Import(Assimp.PostProcessSteps.None, onProgress: null)
            ?? throw new InvalidOperationException($"Model import returned no hierarchy for '{path}'.");
        if (importer.LastBackendResolution is null)
            throw new InvalidOperationException($"Model import published no backend resolution for '{path}'.");

        return new HumanoidConformanceAssetCheckResult
        {
            AssetCheckId = check.Id,
            Passed = hierarchy.Transform is not null,
            Diagnostic = $"Imported '{Path.GetFileName(path)}' through " +
                $"[{string.Join(", ", importer.LastBackendResolution.Candidates.Select(static x => x.StableId))}].",
        };
    }

    private static HumanoidConformanceAssetCheckResult RunHumanoidAvatar(
        HumanoidConformanceManifest manifest,
        string root,
        HumanoidConformanceAssetCheck check)
    {
        HumanoidConformanceAvatar avatar = manifest.Avatars.Single(x => x.SourceFileId == check.SourceFileId);
        SceneNode hierarchy = ImportHumanoid(manifest, root, avatar, out HumanoidComponent humanoid);
        _ = hierarchy;
        if (!humanoid.AvatarDefinition.IsFinalized)
            throw new InvalidDataException($"Avatar '{avatar.Id}' did not produce a finalized definition.");
        if (!string.Equals(
                humanoid.AvatarDefinition.DefinitionContentSha256,
                avatar.AvatarDefinitionSignature,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Avatar '{avatar.Id}' definition signature changed: " +
                $"expected={avatar.AvatarDefinitionSignature} actual={humanoid.AvatarDefinition.DefinitionContentSha256}.");
        }

        return new HumanoidConformanceAssetCheckResult
        {
            AssetCheckId = check.Id,
            Passed = true,
            Diagnostic = $"Finalized humanoid definition '{avatar.AvatarDefinitionSignature}'.",
        };
    }

    private static HumanoidConformanceAssetCheckResult RunAnimation(
        HumanoidConformanceManifest manifest,
        string root,
        HumanoidConformanceAssetCheck check)
    {
        string path = ResolveSourcePath(manifest, root, check.SourceFileId);
        HumanoidConformanceClip clipDeclaration = manifest.Clips.Single(x => x.SourceFileId == check.SourceFileId);
        AnimationClip clip = AnimYamlImporter.Import(path);
        ValidateAnimationIdentity(clipDeclaration, clip, path);

        SceneNode animatedRoot;
        HumanoidComponent? humanoid = null;
        if (check.Kind == HumanoidConformanceAssetCheckKind.AnimationBehaviorAndImport)
        {
            HumanoidConformanceAvatar avatar = manifest.Avatars.First(
                static x => !x.IsIntegrationOnly
                    && x.MappingMode == HumanoidConformanceMappingMode.Automatic
                    && x.HasConventionalBoneNames);
            animatedRoot = ImportHumanoid(manifest, root, avatar, out humanoid);
        }
        else
        {
            animatedRoot = new SceneNode("ImportedAnimationConformance", new Transform());
        }

        var probe = animatedRoot.AddComponent<ImportedAnimationConformanceProbeComponent>()
            ?? throw new InvalidOperationException("Could not attach the imported-animation conformance probe.");
        var component = animatedRoot.AddComponent<AnimationClipComponent>()
            ?? throw new InvalidOperationException("Could not attach an animation component for the asset check.");
        component.Animation = clip;

        bool reservedScalar = clip.ImportedGenericBindings.Any(
            static x => string.Equals(
                x.Attribute,
                ImportedAnimationConformanceProbeComponent.ScalarAttribute,
                StringComparison.Ordinal));
        bool reservedObjectReference = clip.ImportedGenericBindings.Any(
            static x => string.Equals(
                x.Attribute,
                ImportedAnimationConformanceProbeComponent.ObjectReferenceAttribute,
                StringComparison.Ordinal));
        HumanoidConformanceCapability expectedEncoding = check.ExpectedCapabilities & SourceEncodingCapabilities;
        var options = new ImportedAnimationConformanceBehaviorCheckOptions
        {
            RequireScalarWrite = reservedScalar,
            RequireObjectReferenceTransition = reservedObjectReference,
            RequireEvents = clip.ImportedEvents.Length > 0,
            RequireSourceEncodingEvaluation = expectedEncoding != HumanoidConformanceCapability.None,
            ExpectedEventIds = clip.ImportedEvents.Select(static x => x.EventId).Distinct(StringComparer.Ordinal).ToArray(),
        };
        ImportedAnimationConformanceBehaviorCheckResult behavior =
            ImportedAnimationConformanceBehaviorChecks.Evaluate(component, probe, options);

        HumanoidConformanceCapability observed = HumanoidConformanceCapability.None;
        if (behavior.Passed && behavior.Events.Count > 0)
            observed |= HumanoidConformanceCapability.Events;
        if (behavior.Passed && behavior.ObservedNonNullThenNullObjectReference)
            observed |= HumanoidConformanceCapability.ObjectReferenceBindings;
        if (behavior.Passed && behavior.ObservedSourceEncodingEvaluation)
            observed |= expectedEncoding;
        if (humanoid is not null)
            observed |= ObserveHumanoidPlaybackCapabilities(component, humanoid, clip, path);

        return new HumanoidConformanceAssetCheckResult
        {
            AssetCheckId = check.Id,
            Passed = behavior.Passed,
            ObservedCapabilities = observed,
            Diagnostic = behavior.Passed
                ? $"Imported and evaluated '{Path.GetFileName(path)}'."
                : string.Join("; ", behavior.Failures),
        };
    }

    private static HumanoidConformanceCapability ObserveHumanoidPlaybackCapabilities(
        AnimationClipComponent component,
        HumanoidComponent humanoid,
        AnimationClip clip,
        string sourcePath)
    {
        var solver = humanoid.SceneNode.GetComponent<HumanoidIKSolverComponent>()
            ?? humanoid.SceneNode.AddComponent<HumanoidIKSolverComponent>()
            ?? throw new InvalidOperationException("Could not attach the humanoid IK observation solver.");
        float duration = Math.Max(clip.LengthInSeconds, 0.0001f);
        float[] times = [0.0f, duration * 0.25f, duration * 0.5f, duration * 0.75f, duration];
        Vector3 firstPosition = default;
        Quaternion firstRotation = Quaternion.Identity;
        bool hasFirst = false;
        bool translation = false;
        bool vertical = false;
        bool turn = false;
        bool ik = false;
        bool contact = false;
        for (int i = 0; i < times.Length; i++)
        {
            component.EvaluateAtTime(times[i]);
            HumanoidProjectedRootPose pose = humanoid.CurrentProjectedRootPose;
            if (!hasFirst)
            {
                firstPosition = pose.Position;
                firstRotation = pose.Rotation;
                hasFirst = true;
            }
            else
            {
                Vector3 delta = pose.Position - firstPosition;
                translation |= MathF.Abs(delta.X) > 1.0e-5f || MathF.Abs(delta.Z) > 1.0e-5f;
                vertical |= MathF.Abs(delta.Y) > 1.0e-5f;
                turn |= QuaternionAngleDegrees(firstRotation, pose.Rotation) > 0.001f;
            }

            EHumanoidIKGoalApplicationStatus[] statuses =
            [
                solver.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.LeftFoot).Status,
                solver.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.RightFoot).Status,
                solver.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.LeftHand).Status,
                solver.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.RightHand).Status,
            ];
            ik |= statuses.Any(IsAppliedIkStatus);
            contact |= statuses.Any(static status => status is
                EHumanoidIKGoalApplicationStatus.AppliedWithContactCompensation or
                EHumanoidIKGoalApplicationStatus.AppliedWithContactCompensationAndFeetSpacing);
        }

        HumanoidConformanceCapability observed = HumanoidConformanceCapability.None;
        if (!translation && !vertical && !turn)
            observed |= HumanoidConformanceCapability.InPlace;
        if (translation)
            observed |= HumanoidConformanceCapability.Translation;
        if (vertical)
            observed |= HumanoidConformanceCapability.VerticalMotion;
        if (turn)
            observed |= HumanoidConformanceCapability.Turn;
        if (clip.EffectiveSourceWrapMode is not EImportedAnimationWrapMode.Loop)
            observed |= HumanoidConformanceCapability.NonLooping;
        if (clip.ImportedHumanoidRootMotionSettings?.Mirror == true)
            observed |= HumanoidConformanceCapability.Mirroring;
        if (clip.ImportedHumanoidRootMotionSettings?.LoopPose == true)
            observed |= HumanoidConformanceCapability.LoopPose;
        if (clip.HasIKGoals && ik)
            observed |= HumanoidConformanceCapability.InverseKinematics;
        if (!clip.HasIKGoals)
            observed |= HumanoidConformanceCapability.NoInverseKinematics;
        if (contact)
            observed |= HumanoidConformanceCapability.FootContact;
        if (HasWeightedTangents(sourcePath))
            observed |= HumanoidConformanceCapability.Tangents;
        return observed;
    }

    private static bool IsAppliedIkStatus(EHumanoidIKGoalApplicationStatus status)
        => status is EHumanoidIKGoalApplicationStatus.AppliedAuthored
            or EHumanoidIKGoalApplicationStatus.AppliedWithContactCompensation
            or EHumanoidIKGoalApplicationStatus.AppliedWithFeetSpacing
            or EHumanoidIKGoalApplicationStatus.AppliedWithContactCompensationAndFeetSpacing;

    private static bool HasWeightedTangents(string sourcePath)
        => Regex.IsMatch(
            File.ReadAllText(sourcePath),
            @"\bweightedMode:\s*[1-3]\b",
            RegexOptions.CultureInvariant);

    private static SceneNode ImportHumanoid(
        HumanoidConformanceManifest manifest,
        string root,
        HumanoidConformanceAvatar avatar,
        out HumanoidComponent humanoid)
    {
        string modelPath = ResolveSourcePath(manifest, root, avatar.SourceFileId);
        using var importer = new ModelAssetImporter(modelPath, onCompleted: null, materialFactory: null)
        {
            ImportOptions = CreateModelImportOptions(),
        };
        SceneNode hierarchy = importer.Import(Assimp.PostProcessSteps.None, onProgress: null)
            ?? throw new InvalidOperationException($"Humanoid model import returned no hierarchy for '{modelPath}'.");
        using (HumanoidComponent.BeginDeferredSceneNodeInitialization())
            humanoid = hierarchy.AddComponent<HumanoidComponent>()!;
        humanoid.SetSourceModelContentSha256(ComputeSha256(modelPath));
        humanoid.InitializeSceneNodeBindings();
        humanoid.PosePreviewMode = EHumanoidPosePreviewMode.AnimatedPose;
        if (avatar.MappingMode == HumanoidConformanceMappingMode.PersistedCorrection)
        {
            string correctionPath = ResolveSourcePath(manifest, root, avatar.MappingCorrectionsSourceFileId);
            HumanoidConformanceMappingCorrectionResult correction =
                HumanoidConformanceMappingCorrectionLoader.LoadValidateAndApply(correctionPath, modelPath, humanoid);
            if (!correction.Applied)
            {
                throw new InvalidDataException(
                    $"Persisted mapping correction failed for '{avatar.Id}': " +
                    string.Join("; ", correction.Issues.Select(static x => $"{x.Code}: {x.Message}")));
            }
        }
        return hierarchy;
    }

    private static void ValidateAnimationIdentity(
        HumanoidConformanceClip declaration,
        AnimationClip clip,
        string path)
    {
        ImportedAnimationImportManifest imported = clip.SourceImportManifest
            ?? throw new InvalidDataException($"Animation '{path}' has no import manifest.");
        string sourceHash = ComputeSha256(path);
        if (!string.Equals(sourceHash, imported.SourceIdentity.SourceContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Animation '{declaration.Id}' source identity changed.");
        string signature = ImportedAnimationManifestSignature.ComputeSha256(imported);
        if (!string.Equals(signature, declaration.ClipSignature, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Animation '{declaration.Id}' semantic signature changed.");
        if (!string.Equals(
                imported.SourceIdentity.ImportSettingsSha256,
                declaration.ImportSettingsHash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Animation '{declaration.Id}' import settings changed.");
        if (!clip.TryValidateSourcePlaybackCapabilities(allowRuntimeAdapters: true, out string diagnostic))
            throw new InvalidDataException($"Animation '{declaration.Id}' failed capability preflight: {diagnostic}");
    }

    private static ModelImportOptions CreateModelImportOptions()
    {
        var options = new ModelImportOptions
        {
            FbxBackend = FbxImportBackend.Auto,
            FbxPivotPolicy = XREngine.Fbx.FbxPivotImportPolicy.PreservePivotSemantics,
            CollapseGeneratedFbxHelperNodes = true,
            ScaleConversion = 1.0f,
            ZUp = false,
            MultiThread = true,
            ProcessMeshesAsynchronously = false,
            BatchSubmeshAddsDuringAsyncImport = true,
        };
        options.LegacyPostProcessSteps = ModelImportSteps.None;
        return options;
    }

    private static string ResolveSourcePath(
        HumanoidConformanceManifest manifest,
        string root,
        string sourceFileId)
    {
        HumanoidConformanceSourceFile source = manifest.SourceFiles.Single(x => x.Id == sourceFileId);
        string containedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string path = Path.GetFullPath(Path.Combine(containedRoot, source.RelativePath));
        if (!path.StartsWith(containedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Source '{sourceFileId}' escapes the manifest root.");
        return path;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static float QuaternionAngleDegrees(Quaternion left, Quaternion right)
    {
        float dot = Math.Clamp(
            MathF.Abs(Quaternion.Dot(Quaternion.Normalize(left), Quaternion.Normalize(right))),
            0.0f,
            1.0f);
        return MathF.Acos(dot) * (360.0f / MathF.PI);
    }
}
