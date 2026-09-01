using System.Numerics;
using System.Security.Cryptography;
using Newtonsoft.Json;
using XREngine;
using XREngine.Animation;
using XREngine.Animation.IK;
using XREngine.Animation.Importers;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;

namespace HumanoidConformanceRunner;

/// <summary>Unity-free command-line executor for Phase 10 humanoid conformance corpus rows.</summary>
internal static class Program
{
    private sealed class RunSummary
    {
        public string ManifestPath { get; set; } = string.Empty;
        public bool ManifestValid { get; set; }
        public List<string> ManifestIssues { get; set; } = [];
        public List<string> ForbiddenFixtureIdentities { get; set; } = [];
        public bool IdentityProbeOnly { get; set; }
        public List<AvatarIdentitySummary> AvatarIdentities { get; set; } = [];
        public List<ClipIdentitySummary> ClipIdentities { get; set; } = [];
        public bool PartialSelection { get; set; }
        public List<HumanoidConformanceAssetCheckResult> AssetChecks { get; set; } = [];
        public List<CaseSummary> Cases { get; set; } = [];
        public HumanoidConformanceCoverageEvaluationResult? Coverage { get; set; }
    }

    private sealed class AvatarIdentitySummary
    {
        public string Id { get; set; } = string.Empty;
        public string SourceSha256 { get; set; } = string.Empty;
        public string AvatarDefinitionSignature { get; set; } = string.Empty;
        public string ImportSettingsHash { get; set; } = string.Empty;
        public string MappingSignature { get; set; } = string.Empty;
        public string DefinitionStatus { get; set; } = string.Empty;
        public float ProfileConfidence { get; set; }
        public bool IsFinalized { get; set; }
        public List<string> Issues { get; set; } = [];
    }

    private sealed class ClipIdentitySummary
    {
        public string Id { get; set; } = string.Empty;
        public string SourceSha256 { get; set; } = string.Empty;
        public string ClipSignature { get; set; } = string.Empty;
        public string ImportSettingsHash { get; set; } = string.Empty;
        public int SerializedVersion { get; set; }
        public bool IsExecutable { get; set; }
        public float DurationSeconds { get; set; }
        public int SampleRate { get; set; }
        public string WrapMode { get; set; } = string.Empty;
        public bool HasIKGoals { get; set; }
        public int EventCount { get; set; }
        public int ObjectReferenceBindingCount { get; set; }
        public ImportedHumanoidClipRootMotionSettings? RootMotionSettings { get; set; }
        public List<string> Domains { get; set; } = [];
        public List<string> BlockingDiagnostics { get; set; } = [];
    }

    private sealed class CaseSummary
    {
        public string Id { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public string? Error { get; set; }
        public string? ActualReportPath { get; set; }
        public HumanoidConformanceObservation? Observation { get; set; }
        public HumanoidPoseAuditComparisonReport? Comparison { get; set; }
        public HumanoidConformanceGateResult? Gates { get; set; }
    }

    private sealed record Arguments(
        string ManifestPath,
        string OutputDirectory,
        HashSet<string>? CaseIds,
        bool ProbeIdentities);

    private static int Main(string[] args)
    {
        try
        {
            // Use the production headless composition profile: it installs the backend-neutral
            // rendering scheduler plus the asset and shader services model/animation import needs,
            // without registering a desktop renderer, local devices, or requiring Unity.
            using IDisposable runtimeServices = RuntimeRenderingBootstrap.InstallEngineHostServices(
                RuntimeApplicationProfile.HeadlessServer);
            Arguments arguments = ParseArguments(args);
            Directory.CreateDirectory(arguments.OutputDirectory);
            HumanoidConformanceValidationResult validation = HumanoidConformanceManifestLoader.LoadAndValidate(arguments.ManifestPath);
            var summary = new RunSummary
            {
                ManifestPath = validation.ManifestPath,
                ManifestValid = validation.IsValid,
                ManifestIssues = validation.Issues.Select(static x => $"{x.Code}: {x.Message}").ToList(),
                IdentityProbeOnly = arguments.ProbeIdentities,
                PartialSelection = arguments.CaseIds is not null,
            };

            if (validation.IsValid && validation.Manifest is not null)
            {
                string root = Path.GetDirectoryName(Path.GetFullPath(arguments.ManifestPath))!;
                summary.ForbiddenFixtureIdentities = BuildForbiddenFixtureIdentities(validation.Manifest);
                HumanoidConformanceDependencyScanResult dependencyScan = ScanProductionDependencies(root, validation.Manifest);
                if (!dependencyScan.Passed)
                {
                    summary.ManifestValid = false;
                    summary.ManifestIssues.AddRange(dependencyScan.Errors.Select(
                        static error => $"ProductionFixtureDependencyScan: {error}"));
                    summary.ManifestIssues.AddRange(dependencyScan.Findings.Select(
                        static x => $"ProductionFixtureDependency: {x.FilePath}:{x.Line} references '{x.Identity}'."));
                    if (dependencyScan.ScannedRoots.Count == 0 || dependencyScan.ScannedFileCount == 0)
                        summary.ManifestIssues.Add($"ProductionFixtureDependencyScan: fail-closed roots={dependencyScan.ScannedRoots.Count} files={dependencyScan.ScannedFileCount}.");
                }
                if (!summary.ManifestValid)
                    goto WriteSummary;
                if (arguments.ProbeIdentities)
                {
                    ProbeIdentities(validation.Manifest, root, summary);
                    goto WriteSummary;
                }
                if (!summary.PartialSelection)
                {
                    foreach (HumanoidConformanceAssetCheck assetCheck in validation.Manifest.AssetChecks)
                        summary.AssetChecks.Add(HumanoidConformanceAssetCheckExecutor.Run(validation.Manifest, root, assetCheck));
                }
                foreach (HumanoidConformanceMatrixCase matrixCase in validation.Manifest.Matrix)
                {
                    if (arguments.CaseIds is not null && !arguments.CaseIds.Contains(matrixCase.Id))
                        continue;
                    summary.Cases.Add(RunCase(validation.Manifest, root, matrixCase, arguments.OutputDirectory));
                }
                if (!summary.PartialSelection)
                {
                    summary.Coverage = HumanoidConformanceCoverageEvaluator.Evaluate(
                        validation.Manifest,
                        summary.AssetChecks,
                        summary.Cases.Select(ToMatrixCheckResult));
                }
            }

        WriteSummary:
            string summaryPath = Path.Combine(arguments.OutputDirectory, "humanoid-conformance-summary.json");
            File.WriteAllText(summaryPath, JsonConvert.SerializeObject(summary, Formatting.Indented));
            bool passed = summary.ManifestValid
                && (summary.IdentityProbeOnly
                    ? summary.AvatarIdentities.Count > 0
                        && summary.ClipIdentities.Count > 0
                        && summary.AvatarIdentities.All(static x => x.Issues.Count == 0)
                        && summary.ClipIdentities.All(static x => x.BlockingDiagnostics.Count == 0)
                    : summary.Cases.Count > 0
                        && summary.Cases.All(static x => x.Passed)
                        && (summary.PartialSelection
                            || summary.AssetChecks.Count > 0
                                && summary.Coverage?.Passed == true));
            Console.WriteLine($"Humanoid conformance summary: {summaryPath}");
            return passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static HumanoidConformanceMatrixCheckResult ToMatrixCheckResult(CaseSummary summary)
        => new()
        {
            MatrixCaseId = summary.Id,
            Passed = summary.Passed,
            ObservedCapabilities = summary.Observation?.ObservedCapabilities
                ?? HumanoidConformanceCapability.None,
            Diagnostic = summary.Error
                ?? (summary.Gates is null
                    ? "The matrix case produced no gate result."
                    : string.Join("; ", summary.Gates.Failures.Select(static x => $"{x.Gate}: {x.Message}"))),
        };

    private static void ProbeIdentities(HumanoidConformanceManifest manifest, string root, RunSummary summary)
    {
        foreach (HumanoidConformanceAvatar avatar in manifest.Avatars)
        {
            var identity = new AvatarIdentitySummary { Id = avatar.Id };
            summary.AvatarIdentities.Add(identity);
            try
            {
                string avatarPath = ResolveSourcePath(manifest, root, avatar.SourceFileId);
                identity.SourceSha256 = ComputeSha256(avatarPath);
                ModelImportOptions importOptions = CreateConformanceModelImportOptions();
                using var importer = new ModelAssetImporter(avatarPath, onCompleted: null, materialFactory: null)
                {
                    ImportOptions = importOptions,
                };
                SceneNode rootNode = importer.Import(Assimp.PostProcessSteps.None, onProgress: null)
                    ?? throw new InvalidOperationException($"FBX import returned no hierarchy for '{avatarPath}'.");
                HumanoidComponent humanoid;
                using (HumanoidComponent.BeginDeferredSceneNodeInitialization())
                    humanoid = rootNode.AddComponent<HumanoidComponent>()!;
                humanoid.SetSourceModelContentSha256(identity.SourceSha256);
                humanoid.InitializeSceneNodeBindings();

                if (avatar.MappingMode == HumanoidConformanceMappingMode.PersistedCorrection)
                {
                    string correctionPath = ResolveSourcePath(manifest, root, avatar.MappingCorrectionsSourceFileId);
                    HumanoidConformanceMappingCorrectionResult correction = HumanoidConformanceMappingCorrectionLoader.LoadValidateAndApply(
                        correctionPath,
                        avatarPath,
                        humanoid);
                    identity.MappingSignature = correction.MappingSignature;
                    foreach (HumanoidConformanceMappingCorrectionIssue issue in correction.Issues)
                        if (!string.Equals(issue.Code, "AvatarDefinitionSignatureMismatch", StringComparison.Ordinal))
                            identity.Issues.Add($"{issue.Code}: {issue.Message}");
                }

                identity.AvatarDefinitionSignature = humanoid.AvatarDefinition.DefinitionContentSha256;
                identity.ImportSettingsHash = ComputeAvatarImportSettingsHash(importer, importOptions, avatarPath);
                identity.DefinitionStatus = humanoid.AvatarDefinition.Status.ToString();
                identity.ProfileConfidence = humanoid.Settings.ProfileConfidence;
                identity.IsFinalized = humanoid.AvatarDefinition.IsFinalized;
            }
            catch (Exception ex)
            {
                identity.Issues.Add(ex.Message);
            }
        }

        foreach (HumanoidConformanceClip clipDefinition in manifest.Clips)
        {
            var identity = new ClipIdentitySummary { Id = clipDefinition.Id };
            summary.ClipIdentities.Add(identity);
            try
            {
                string clipPath = ResolveSourcePath(manifest, root, clipDefinition.SourceFileId);
                identity.SourceSha256 = ComputeSha256(clipPath);
                AnimationClip clip = AnimYamlImporter.Import(clipPath);
                ImportedAnimationImportManifest manifestIdentity = clip.SourceImportManifest
                    ?? throw new InvalidDataException("The imported clip has no source import manifest.");
                identity.ClipSignature = ImportedAnimationManifestSignature.ComputeSha256(manifestIdentity);
                identity.ImportSettingsHash = manifestIdentity.SourceIdentity.ImportSettingsSha256;
                identity.SerializedVersion = manifestIdentity.SourceIdentity.SerializedVersion;
                identity.IsExecutable = manifestIdentity.IsExecutable;
                identity.DurationSeconds = clip.LengthInSeconds;
                identity.SampleRate = clip.SampleRate;
                identity.WrapMode = clip.EffectiveSourceWrapMode.ToString();
                identity.HasIKGoals = clip.HasIKGoals;
                identity.EventCount = clip.ImportedEvents.Length;
                identity.ObjectReferenceBindingCount = clip.ImportedGenericBindings.Count(
                    static binding => binding.ValueKind == EImportedAnimationBindingValueKind.ObjectReference);
                identity.RootMotionSettings = clip.ImportedHumanoidRootMotionSettings;
                identity.Domains.AddRange(manifestIdentity.Domains.Select(static domain =>
                    $"{domain.Domain}:{domain.State}:{domain.SourceItemCount}:{domain.AppliedItemCount}:{domain.DiscardedItemCount}:{domain.PreservedItemCount}"));
                if (!clip.TryValidateSourcePlaybackCapabilities(allowRuntimeAdapters: true, out string diagnostic))
                    identity.BlockingDiagnostics.Add(diagnostic);
            }
            catch (Exception ex)
            {
                identity.BlockingDiagnostics.Add(ex.Message);
            }
        }
    }

    private static CaseSummary RunCase(HumanoidConformanceManifest manifest, string root, HumanoidConformanceMatrixCase matrixCase, string outputDirectory)
    {
        var summary = new CaseSummary { Id = matrixCase.Id };
        try
        {
            HumanoidConformanceAvatar avatar = manifest.Avatars.Single(x => x.Id == matrixCase.AvatarId);
            HumanoidConformanceClip clipDefinition = manifest.Clips.Single(x => x.Id == matrixCase.ClipId);

            string avatarPath = ResolveSourcePath(manifest, root, avatar.SourceFileId);
            string clipPath = ResolveSourcePath(manifest, root, clipDefinition.SourceFileId);
            string? correctionPath = avatar.MappingMode == HumanoidConformanceMappingMode.PersistedCorrection
                ? ResolveSourcePath(manifest, root, avatar.MappingCorrectionsSourceFileId)
                : null;
            string referencePath = ResolveSourcePath(manifest, root, matrixCase.ReferenceFileId);
            HumanoidPoseAuditReport reference = HumanoidPoseAuditIO.LoadReport(referencePath);

            ModelImportOptions importOptions = CreateConformanceModelImportOptions();
            using var importer = new ModelAssetImporter(avatarPath, onCompleted: null, materialFactory: null)
            {
                ImportOptions = importOptions,
            };
            SceneNode rootNode = importer.Import(Assimp.PostProcessSteps.None, onProgress: null)
                ?? throw new InvalidOperationException($"FBX import returned no hierarchy for '{avatarPath}'.");
            HumanoidComponent humanoid;
            using (HumanoidComponent.BeginDeferredSceneNodeInitialization())
                humanoid = rootNode.AddComponent<HumanoidComponent>()!;
            humanoid.SetSourceModelContentSha256(ComputeSha256(avatarPath));
            humanoid.InitializeSceneNodeBindings();
            humanoid.PosePreviewMode = EHumanoidPosePreviewMode.AnimatedPose;
            if (avatar.MappingMode == HumanoidConformanceMappingMode.PersistedCorrection)
            {
                HumanoidConformanceMappingCorrectionResult correction = HumanoidConformanceMappingCorrectionLoader.LoadValidateAndApply(
                    correctionPath!, avatarPath, humanoid);
                if (!correction.Applied)
                    throw new InvalidDataException($"Persisted mapping correction failed: {string.Join("; ", correction.Issues.Select(x => x.Code + ": " + x.Message))}");
            }

            AnimationClip clip = AnimYamlImporter.Import(clipPath);
            ValidateImportedIdentities(
                avatar,
                clipDefinition,
                humanoid,
                clip,
                avatarPath,
                clipPath,
                ComputeAvatarImportSettingsHash(importer, importOptions, avatarPath));
            AnimationClipComponent clipComponent = rootNode.AddComponent<AnimationClipComponent>()!;
            clipComponent.Animation = clip;
            HumanoidPoseAuditReport actual = HumanoidPoseAuditSampler.Sample(
                clipComponent,
                humanoid,
                reference.SampleRate);
            string actualReportPath = Path.Combine(outputDirectory, $"{SanitizeFileName(matrixCase.Id)}.actual.json");
            File.WriteAllText(actualReportPath, JsonConvert.SerializeObject(actual, Formatting.Indented));

            HumanoidConformanceObservation observation = ObserveDirectPlayback(
                clipComponent,
                humanoid,
                clip,
                actual.EngineUnitsPerSourceMeter,
                matrixCase.ExpectedCapabilities);
            ObserveImportedCapabilities(clip, rootNode, clipComponent, observation);
            ObservePlaybackRoute(matrixCase.PlaybackMode, rootNode, humanoid, clipComponent, clip, clipPath, observation);
            ValidateRenameMoveInvariance(avatar, avatarPath, correctionPath, clipPath, clip, humanoid, clipComponent, outputDirectory, observation);
            HumanoidPoseAuditComparisonReport comparison = HumanoidPoseAuditComparer.Compare(reference, actual, referencePath, actualReportPath);
            HumanoidConformanceGateResult gates = HumanoidConformanceGateEvaluator.Evaluate(matrixCase, comparison, observation);
            summary.ActualReportPath = actualReportPath;
            summary.Observation = observation;
            summary.Comparison = comparison;
            summary.Gates = gates;
            summary.Passed = gates.Passed;
        }
        catch (Exception ex)
        {
            summary.Error = ex.Message;
            summary.Passed = false;
        }

        return summary;
    }

    private static HumanoidConformanceObservation ObserveDirectPlayback(
        AnimationClipComponent clipComponent,
        HumanoidComponent humanoid,
        AnimationClip clip,
        float engineUnitsPerMeter,
        HumanoidConformanceCapability expected)
    {
        var observation = new HumanoidConformanceObservation
        {
            EngineUnitsPerMeter = engineUnitsPerMeter,
            ObservedCapabilities = HumanoidConformanceCapability.None,
        };
        if (!float.IsFinite(observation.EngineUnitsPerMeter) || observation.EngineUnitsPerMeter <= 0.0f)
            observation.ExplicitFailures.Add("The imported avatar did not provide a finite positive engine-units-per-meter scale.");

        float duration = Math.Max(clip.LengthInSeconds, 0.0001f);
        try
        {
            // Exercise clamped direct seeks at authored endpoints, exact frames, half frames,
            // and a deterministic non-frame phase. Each has to agree with an equivalent
            // unwrapped evaluation across the complete mapped pose, not only root motion.
            float frame = Math.Min(duration, 1.0f / 60.0f);
            float halfFrame = frame * 0.5f;
            float randomPhase = duration * 0.61803398875f;
            float[] exactProbes = [0.0f, frame, halfFrame, duration * 0.5f, randomPhase, duration];
            foreach (float probe in exactProbes)
            {
                clipComponent.EvaluateAtTime(probe);
                HumanoidPoseSnapshot clamped = CapturePoseSnapshot(humanoid);
                clipComponent.EvaluateAtUnwrappedTime(probe);
                RequirePoseEquivalent(clamped, CapturePoseSnapshot(humanoid), $"exact/unwrapped seek at {probe:G6}s");
            }

            // Reverse seeks must reproduce the same full pose after a later sample has been
            // evaluated. This catches stateful curve/root ownership that root-only checks miss.
            float reverseProbe = duration * 0.25f;
            clipComponent.EvaluateAtUnwrappedTime(duration * 0.75f);
            clipComponent.EvaluateAtUnwrappedTime(reverseProbe);
            HumanoidPoseSnapshot reverse = CapturePoseSnapshot(humanoid);
            clipComponent.EvaluateAtTime(reverseProbe);
            RequirePoseEquivalent(reverse, CapturePoseSnapshot(humanoid), "reverse direct seek");
            observation.ObservedCapabilities |= HumanoidConformanceCapability.ExactSeek | HumanoidConformanceCapability.ReversePlayback;

            // Probe both sides of every signed loop epoch, including seams. An exact
            // unwrapped seek deliberately starts a new root-motion epoch, so its first
            // delta must be identity while its reported signed cycle stays intact.
            const float seamEpsilon = 0.0001f;
            foreach (int epoch in Enumerable.Range(-10, 21))
            {
                foreach (float phase in new[] { seamEpsilon, duration * 0.5f, Math.Max(seamEpsilon, duration - seamEpsilon) })
                {
                    clipComponent.EvaluateAtUnwrappedTime(epoch * (double)duration + phase);
                    if (clip.EffectiveSourceWrapMode == EImportedAnimationWrapMode.Loop
                        && clipComponent.RootMotionLoopCycle != epoch)
                        throw new InvalidOperationException($"Signed loop epoch {epoch} was not retained by direct evaluation.");
                    if (clipComponent.AppliedRootMotionDelta.Translation.LengthSquared() > 0.00000001f
                        || QuaternionAngleDegrees(Quaternion.Identity, clipComponent.AppliedRootMotionDelta.Rotation) > 0.001f)
                        throw new InvalidOperationException($"Signed loop epoch {epoch} published a non-identity seek delta.");
                }
            }

            clipComponent.EvaluateAtUnwrappedTime(duration * 10.0f);
            Vector3 forwardEpochPosition = humanoid.CurrentProjectedRootPose.Position;
            Quaternion forwardEpochRotation = humanoid.CurrentProjectedRootPose.Rotation;
            clipComponent.EvaluateAtUnwrappedTime(-duration * 10.0f);
            Vector3 reverseEpochPosition = humanoid.CurrentProjectedRootPose.Position;
            Quaternion reverseEpochRotation = humanoid.CurrentProjectedRootPose.Rotation;
            clipComponent.EvaluateAtUnwrappedTime(0.0);
            Vector3 startPosition = humanoid.CurrentProjectedRootPose.Position;
            Quaternion startRotation = humanoid.CurrentProjectedRootPose.Rotation;
            observation.TenLoopDriftEngineUnits = Math.Max(
                Vector3.Distance(startPosition, forwardEpochPosition),
                Vector3.Distance(startPosition, reverseEpochPosition));
            observation.TenLoopDriftDegrees = Math.Max(
                QuaternionAngleDegrees(startRotation, forwardEpochRotation),
                QuaternionAngleDegrees(startRotation, reverseEpochRotation));
            observation.ObservedCapabilities |= HumanoidConformanceCapability.SignedLoopEpochs;
        }
        catch (Exception ex)
        {
            observation.ExplicitFailures.Add($"Direct playback observation failed: {ex.Message}");
        }

        return observation;
    }

    private sealed record HumanoidPoseSnapshot(
        Vector3 ProjectedRootPosition,
        Quaternion ProjectedRootRotation,
        Dictionary<string, (Vector3 Position, Quaternion Rotation)> Bones,
        Dictionary<string, float> Muscles);

    private static HumanoidPoseSnapshot CapturePoseSnapshot(HumanoidComponent humanoid)
    {
        var bones = new Dictionary<string, (Vector3, Quaternion)>(StringComparer.Ordinal);
        foreach ((string role, SceneNode? node) in EnumerateHumanoidBoneNodes(humanoid))
        {
            if (node is not null)
            {
                Matrix4x4.Decompose(node.Transform.LocalMatrix, out _, out Quaternion rotation, out Vector3 position);
                bones[role] = (position, rotation);
            }
        }

        var muscles = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (ImportedHumanoidMuscleMap.MuscleEntry entry in ImportedHumanoidMuscleMap.OrderedMuscleEntries)
            if (humanoid.TryGetMuscleValue(entry.Value, out float value))
                muscles[entry.HumanTraitName] = value;
        HumanoidProjectedRootPose root = humanoid.CurrentProjectedRootPose;
        return new HumanoidPoseSnapshot(root.Position, root.Rotation, bones, muscles);
    }

    private static IEnumerable<(string Role, SceneNode? Node)> EnumerateHumanoidBoneNodes(HumanoidComponent humanoid)
    {
        yield return ("Hips", humanoid.Hips.Node); yield return ("Spine", humanoid.Spine.Node);
        yield return ("Chest", humanoid.Chest.Node); yield return ("UpperChest", humanoid.UpperChest.Node);
        yield return ("Neck", humanoid.Neck.Node); yield return ("Head", humanoid.Head.Node);
        yield return ("LeftUpperArm", humanoid.Left.Arm.Node); yield return ("LeftLowerArm", humanoid.Left.Elbow.Node);
        yield return ("LeftHand", humanoid.Left.Wrist.Node); yield return ("RightUpperArm", humanoid.Right.Arm.Node);
        yield return ("RightLowerArm", humanoid.Right.Elbow.Node); yield return ("RightHand", humanoid.Right.Wrist.Node);
        yield return ("LeftUpperLeg", humanoid.Left.Leg.Node); yield return ("LeftLowerLeg", humanoid.Left.Knee.Node);
        yield return ("LeftFoot", humanoid.Left.Foot.Node); yield return ("LeftToes", humanoid.Left.Toes.Node);
        yield return ("RightUpperLeg", humanoid.Right.Leg.Node); yield return ("RightLowerLeg", humanoid.Right.Knee.Node);
        yield return ("RightFoot", humanoid.Right.Foot.Node); yield return ("RightToes", humanoid.Right.Toes.Node);
    }

    private static void RequirePoseEquivalent(HumanoidPoseSnapshot expected, HumanoidPoseSnapshot actual, string context)
    {
        const float positionTolerance = 0.00001f;
        const float rotationTolerance = 0.001f;
        float rootPositionError = Vector3.Distance(expected.ProjectedRootPosition, actual.ProjectedRootPosition);
        float rootRotationError = QuaternionAngleDegrees(expected.ProjectedRootRotation, actual.ProjectedRootRotation);
        if (rootPositionError > positionTolerance || rootRotationError > rotationTolerance)
            throw new InvalidOperationException(
                $"Projected root mismatched during {context}: position={rootPositionError:G6}, rotation={rootRotationError:G6}deg.");
        if (expected.Bones.Count != actual.Bones.Count)
            throw new InvalidOperationException(
                $"Mapped bone count mismatched during {context}: expected={expected.Bones.Count}, actual={actual.Bones.Count}.");
        if (expected.Muscles.Count != actual.Muscles.Count)
            throw new InvalidOperationException(
                $"Mapped muscle count mismatched during {context}: expected={expected.Muscles.Count}, actual={actual.Muscles.Count}.");
        foreach ((string role, (Vector3 position, Quaternion rotation)) in expected.Bones)
        {
            if (!actual.Bones.TryGetValue(role, out var actualBone)
                || Vector3.Distance(position, actualBone.Position) > positionTolerance
                || QuaternionAngleDegrees(rotation, actualBone.Rotation) > rotationTolerance)
                throw new InvalidOperationException($"Bone '{role}' mismatched during {context}.");
        }
        foreach ((string muscle, float value) in expected.Muscles)
            if (!actual.Muscles.TryGetValue(muscle, out float actualValue)
                || MathF.Abs(value - actualValue) > 0.00001f)
                throw new InvalidOperationException($"Muscle '{muscle}' mismatched during {context}.");
    }

    private static void ObservePlaybackRoute(
        HumanoidConformancePlaybackMode mode,
        SceneNode rootNode,
        HumanoidComponent humanoid,
        AnimationClipComponent directComponent,
        AnimationClip clip,
        string clipPath,
        HumanoidConformanceObservation observation)
    {
        float sampleTime = Math.Max(clip.LengthInSeconds * 0.5f, 0.0f);
        directComponent.EvaluateAtTime(sampleTime);
        Vector3 directPosition = humanoid.CurrentProjectedRootPose.Position;
        Quaternion directRotation = humanoid.CurrentProjectedRootPose.Rotation;

        var controller = rootNode.AddComponent<AnimStateMachineComponent>()!;
        controller.Humanoid = humanoid;
        // Import a distinct compatible motion occurrence. Child cycle offsets below make
        // its evaluation observably different from the primary clip while keeping the
        // same imported avatar contract and bindings.
        AnimationClip alternateClip = AnimYamlImporter.Import(clipPath);
        controller.StateMachine = CreateRoute(mode, clip, alternateClip);
        controller.EvaluateAtTime(0.0f);

        if (mode is HumanoidConformancePlaybackMode.BlendTree1D
            or HumanoidConformancePlaybackMode.BlendTree2D
            or HumanoidConformancePlaybackMode.DirectBlendTree)
            RequireObservableBlendRange(controller, humanoid, sampleTime, mode);

        // Drive the graph's public evaluator for state-transition routes before the exact seek.
        // This is deliberately not a private tick or a synthetic state assignment.
        switch (mode)
        {
            case HumanoidConformancePlaybackMode.Transition:
                controller.StateMachine.EvaluationTick(controller, 0.1f);
                RequireRouteProgress(controller, mode);
                break;
            case HumanoidConformancePlaybackMode.InterruptedTransition:
                controller.StateMachine.EvaluationTick(controller, 0.1f);
                controller.StateMachine.EvaluationTick(controller, 0.1f);
                RequireRouteProgress(controller, mode);
                break;
        }

        controller.EvaluateAtTime(sampleTime);
        Vector3 routePosition = humanoid.CurrentProjectedRootPose.Position;
        Quaternion routeRotation = humanoid.CurrentProjectedRootPose.Rotation;
        const float positionTolerance = 0.00001f;
        const float rotationTolerance = 0.001f;
        if (mode == HumanoidConformancePlaybackMode.StateMachine
            && (Vector3.Distance(directPosition, routePosition) > positionTolerance
                || QuaternionAngleDegrees(directRotation, routeRotation) > rotationTolerance))
        {
            observation.ExplicitFailures.Add(
                $"{mode} did not agree with an equivalent direct-clip solve at {sampleTime:G6}s.");
            return;
        }

        observation.ObservedCapabilities |= mode switch
        {
            HumanoidConformancePlaybackMode.StateMachine => HumanoidConformanceCapability.StateMachine,
            HumanoidConformancePlaybackMode.Transition => HumanoidConformanceCapability.StateMachine | HumanoidConformanceCapability.Transitions,
            HumanoidConformancePlaybackMode.InterruptedTransition => HumanoidConformanceCapability.StateMachine | HumanoidConformanceCapability.Transitions | HumanoidConformanceCapability.InterruptedTransitions,
            HumanoidConformancePlaybackMode.BlendTree1D => HumanoidConformanceCapability.BlendTree1D,
            HumanoidConformancePlaybackMode.BlendTree2D => HumanoidConformanceCapability.BlendTree2D,
            HumanoidConformancePlaybackMode.DirectBlendTree => HumanoidConformanceCapability.DirectBlendTree,
            _ => HumanoidConformanceCapability.None,
        };
    }

    private static AnimStateMachine CreateRoute(HumanoidConformancePlaybackMode mode, AnimationClip clip, AnimationClip alternateClip)
    {
        MotionBase motion = mode switch
        {
            HumanoidConformancePlaybackMode.BlendTree1D => CreateBlendTree1D(clip, alternateClip),
            HumanoidConformancePlaybackMode.BlendTree2D => CreateBlendTree2D(clip, alternateClip),
            HumanoidConformancePlaybackMode.DirectBlendTree => CreateDirectBlendTree(clip, alternateClip),
            _ => clip,
        };
        var first = new AnimState(motion, "ConformanceA");
        var layer = new AnimLayer(first) { InitialState = first };
        var machine = new AnimStateMachine { Layers = [layer] };

        if (mode is HumanoidConformancePlaybackMode.BlendTree1D)
            machine.NewFloat("ConformanceBlend1D", 0.5f);
        else if (mode is HumanoidConformancePlaybackMode.BlendTree2D)
        {
            machine.NewFloat("ConformanceBlendX", 0.5f);
            machine.NewFloat("ConformanceBlendY", 0.5f);
        }
        else if (mode is HumanoidConformancePlaybackMode.DirectBlendTree)
        {
            machine.NewFloat("ConformanceWeightPrimary", 1.0f);
            machine.NewFloat("ConformanceWeightAlternate", 0.0f);
        }

        if (mode is HumanoidConformancePlaybackMode.Transition or HumanoidConformancePlaybackMode.InterruptedTransition)
        {
            var second = new AnimState(alternateClip, "ConformanceB");
            layer.States.Add(second);
            AnimStateTransition firstTransition = first.AddTransitionTo(
                second, [], exitTime: 0.02f, hasExitTime: true, transitionDuration: 0.5f);
            if (mode == HumanoidConformancePlaybackMode.InterruptedTransition)
            {
                var third = new AnimState(clip, "ConformanceC");
                layer.States.Add(third);
                firstTransition.InterruptionSource = ETransitionInterruptionSource.NextThenCurrent;
                second.AddTransitionTo(third, [], exitTime: 0.02f, hasExitTime: true, transitionDuration: 0.25f,
                    interruptionSource: ETransitionInterruptionSource.NextThenCurrent);
            }
        }
        return machine;
    }

    private static BlendTree1D CreateBlendTree1D(AnimationClip clip, AnimationClip alternateClip)
        => new()
        {
            ParameterName = "ConformanceBlend1D",
            Children =
            [
                new BlendTree1D.Child { Motion = clip, Threshold = 0.0f },
                new BlendTree1D.Child { Motion = alternateClip, Threshold = 1.0f, CycleOffset = 0.37f },
            ],
        };

    private static BlendTree2D CreateBlendTree2D(AnimationClip clip, AnimationClip alternateClip)
        => new()
        {
            XParameterName = "ConformanceBlendX",
            YParameterName = "ConformanceBlendY",
            BlendType = BlendTree2D.EBlendType.Cartesian,
            Children =
            [
                new BlendTree2D.Child { Motion = clip, PositionX = 0.0f, PositionY = 0.0f },
                new BlendTree2D.Child { Motion = alternateClip, PositionX = 1.0f, PositionY = 0.0f, CycleOffset = 0.17f },
                new BlendTree2D.Child { Motion = alternateClip, PositionX = 0.0f, PositionY = 1.0f, CycleOffset = 0.37f },
                new BlendTree2D.Child { Motion = alternateClip, PositionX = 1.0f, PositionY = 1.0f, CycleOffset = 0.61f },
            ],
        };

    private static BlendTreeDirect CreateDirectBlendTree(AnimationClip clip, AnimationClip alternateClip)
        => new()
        {
            Children =
            [
                new BlendTreeDirect.Child { Motion = clip, WeightParameterName = "ConformanceWeightPrimary" },
                new BlendTreeDirect.Child { Motion = alternateClip, WeightParameterName = "ConformanceWeightAlternate", CycleOffset = 0.37f },
            ],
        };

    private static void RequireObservableBlendRange(
        AnimStateMachineComponent controller,
        HumanoidComponent humanoid,
        float sampleTime,
        HumanoidConformancePlaybackMode mode)
    {
        HumanoidPoseSnapshot? previous = null;
        bool observedChange = false;
        foreach (float weight in new[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f })
        {
            SetRouteWeight(controller.StateMachine, mode, weight);
            controller.EvaluateAtTime(sampleTime);
            HumanoidPoseSnapshot current = CapturePoseSnapshot(humanoid);
            if (previous is not null && !PoseSnapshotsMatch(previous, current))
                observedChange = true;
            previous = current;
        }
        if (!observedChange)
            throw new InvalidOperationException($"{mode} produced no observable change at 0/25/50/75/100 percent input.");
    }

    private static void SetRouteWeight(AnimStateMachine machine, HumanoidConformancePlaybackMode mode, float weight)
    {
        switch (mode)
        {
            case HumanoidConformancePlaybackMode.BlendTree1D:
                ((AnimFloat)machine.Variables["ConformanceBlend1D"]).Value = weight;
                break;
            case HumanoidConformancePlaybackMode.BlendTree2D:
                ((AnimFloat)machine.Variables["ConformanceBlendX"]).Value = weight;
                ((AnimFloat)machine.Variables["ConformanceBlendY"]).Value = weight;
                break;
            case HumanoidConformancePlaybackMode.DirectBlendTree:
                ((AnimFloat)machine.Variables["ConformanceWeightPrimary"]).Value = 1.0f - weight;
                ((AnimFloat)machine.Variables["ConformanceWeightAlternate"]).Value = weight;
                break;
        }
    }

    private static bool PoseSnapshotsMatch(HumanoidPoseSnapshot expected, HumanoidPoseSnapshot actual)
    {
        try
        {
            RequirePoseEquivalent(expected, actual, "blend probe");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void RequireRouteProgress(AnimStateMachineComponent controller, HumanoidConformancePlaybackMode mode)
    {
        if (controller.StateMachine.GetCurrentTransition(0) is null
            && controller.StateMachine.Layers[0].CurrentState?.Name == "ConformanceA")
        {
            throw new InvalidOperationException($"{mode} graph did not leave its initial state through the public evaluator.");
        }
    }

    private static void ValidateImportedIdentities(
        HumanoidConformanceAvatar avatar,
        HumanoidConformanceClip clipDefinition,
        HumanoidComponent humanoid,
        AnimationClip clip,
        string avatarPath,
        string clipPath,
        string avatarImportSettingsHash)
    {
        var mismatches = new List<string>();
        string avatarHash = ComputeSha256(avatarPath);
        if (!string.Equals(avatarHash, humanoid.AvatarDefinition.SourceModelContentSha256, StringComparison.OrdinalIgnoreCase))
            mismatches.Add($"avatar source expected={avatarHash} actual={humanoid.AvatarDefinition.SourceModelContentSha256}");
        if (!string.Equals(avatar.AvatarDefinitionSignature, humanoid.AvatarDefinition.DefinitionContentSha256, StringComparison.OrdinalIgnoreCase))
            mismatches.Add($"avatar definition expected={avatar.AvatarDefinitionSignature} actual={humanoid.AvatarDefinition.DefinitionContentSha256}");
        if (!string.Equals(avatar.ImportSettingsHash, avatarImportSettingsHash, StringComparison.OrdinalIgnoreCase))
            mismatches.Add($"avatar import settings expected={avatar.ImportSettingsHash} actual={avatarImportSettingsHash}");

        string clipHash = ComputeSha256(clipPath);
        string? importedHash = clip.SourceImportManifest?.SourceIdentity?.SourceContentSha256;
        if (!string.Equals(clipHash, importedHash, StringComparison.OrdinalIgnoreCase))
            mismatches.Add($"clip source expected={clipHash} actual={importedHash ?? "<missing>"}");

        ImportedAnimationImportManifest? clipManifest = clip.SourceImportManifest;
        if (clipManifest is null)
        {
            mismatches.Add("clip import manifest actual=<missing>");
        }
        else
        {
            string clipSignature = ImportedAnimationManifestSignature.ComputeSha256(clipManifest);
            if (!string.Equals(clipDefinition.ClipSignature, clipSignature, StringComparison.OrdinalIgnoreCase))
                mismatches.Add($"clip signature expected={clipDefinition.ClipSignature} actual={clipSignature}");
            if (!string.Equals(clipDefinition.ImportSettingsHash, clipManifest.SourceIdentity.ImportSettingsSha256, StringComparison.OrdinalIgnoreCase))
                mismatches.Add($"clip import settings expected={clipDefinition.ImportSettingsHash} actual={clipManifest.SourceIdentity.ImportSettingsSha256}");
        }

        if (!clip.TryValidateSourcePlaybackCapabilities(allowRuntimeAdapters: true, out string diagnostic))
            mismatches.Add($"clip capability contract rejected playback: {diagnostic}");

        if (mismatches.Count > 0)
            throw new InvalidDataException($"Imported identity mismatch for avatar '{avatar.Id}' and clip '{clipDefinition.Id}': {string.Join("; ", mismatches)}.");
    }

    private static ModelImportOptions CreateConformanceModelImportOptions()
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

    private static string ComputeAvatarImportSettingsHash(
        ModelAssetImporter importer,
        ModelImportOptions options,
        string avatarPath)
    {
        ModelImportBackendResolution resolution = importer.LastBackendResolution
            ?? throw new InvalidOperationException("The model importer did not publish a backend resolution.");
        return ModelCacheVariantFingerprintBuilder.Compute(avatarPath, options, resolution).FullHash.ToUpperInvariant();
    }

    private static void ObserveImportedCapabilities(
        AnimationClip clip,
        SceneNode rootNode,
        AnimationClipComponent clipComponent,
        HumanoidConformanceObservation observation)
    {
        observation.ObservedEventCount = clip.ImportedEvents.Length;
        observation.ObservedObjectReferenceBindingCount = clip.ImportedGenericBindings.Count(
            static binding => binding.ValueKind == EImportedAnimationBindingValueKind.ObjectReference);
        if (!clip.HasIKGoals)
        {
            observation.InverseKinematicsDisabled = true;
            return;
        }

        HumanoidIKSolverComponent solver = rootNode.AddComponent<HumanoidIKSolverComponent>()!;
        clipComponent.EvaluateAtTime(Math.Max(clip.LengthInSeconds * 0.5f, 0.0f));
        EHumanoidIKGoalApplicationStatus[] statuses =
        [
            solver.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.LeftFoot).Status,
            solver.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.RightFoot).Status,
            solver.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.LeftHand).Status,
            solver.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.RightHand).Status,
        ];
        observation.InverseKinematicsApplied = statuses.Any(static status => status is
            EHumanoidIKGoalApplicationStatus.AppliedAuthored or
            EHumanoidIKGoalApplicationStatus.AppliedWithContactCompensation or
            EHumanoidIKGoalApplicationStatus.AppliedWithFeetSpacing or
            EHumanoidIKGoalApplicationStatus.AppliedWithContactCompensationAndFeetSpacing);
        observation.ObservedFootContactCount = statuses.Count(static status => status is
            EHumanoidIKGoalApplicationStatus.AppliedWithContactCompensation or
            EHumanoidIKGoalApplicationStatus.AppliedWithContactCompensationAndFeetSpacing);
    }

    private static void ValidateRenameMoveInvariance(
        HumanoidConformanceAvatar avatar,
        string avatarPath,
        string? correctionPath,
        string clipPath,
        AnimationClip clip,
        HumanoidComponent originalHumanoid,
        AnimationClipComponent originalComponent,
        string outputDirectory,
        HumanoidConformanceObservation observation)
    {
        HumanoidAvatarDefinitionMetadata persistedDefinition = CookedBinarySerializer.Deserialize(
            typeof(HumanoidAvatarDefinitionMetadata),
            CookedBinarySerializer.Serialize(originalHumanoid.AvatarDefinition)) as HumanoidAvatarDefinitionMetadata
            ?? throw new InvalidOperationException("Persisted avatar definition did not deserialize.");
        if (persistedDefinition.SchemaVersion != originalHumanoid.AvatarDefinition.SchemaVersion
            || persistedDefinition.Status != originalHumanoid.AvatarDefinition.Status
            || persistedDefinition.DefinitionRevision != originalHumanoid.AvatarDefinition.DefinitionRevision
            || !string.Equals(
                persistedDefinition.DefinitionContentSha256,
                originalHumanoid.AvatarDefinition.DefinitionContentSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Avatar-definition serialization changed persistence state: " +
                $"schema {originalHumanoid.AvatarDefinition.SchemaVersion}->{persistedDefinition.SchemaVersion}, " +
                $"status {originalHumanoid.AvatarDefinition.Status}->{persistedDefinition.Status}, " +
                $"revision {originalHumanoid.AvatarDefinition.DefinitionRevision}->{persistedDefinition.DefinitionRevision}.");
        }
        string relocationRoot = Path.Combine(outputDirectory, "relocation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(relocationRoot);
        string relocatedAvatar = Path.Combine(relocationRoot, avatar.MappingMode == HumanoidConformanceMappingMode.PersistedCorrection
            ? Path.GetFileName(avatarPath)
            : "renamed-" + Path.GetFileName(avatarPath));
        string relocatedClip = Path.Combine(relocationRoot, "renamed-" + Path.GetFileName(clipPath));
        File.Copy(avatarPath, relocatedAvatar, overwrite: true);
        File.Copy(clipPath, relocatedClip, overwrite: true);
        float sampleTime = Math.Max(clip.LengthInSeconds * 0.5f, 0.0f);
        originalComponent.EvaluateAtTime(sampleTime);
        HumanoidPoseSnapshot expectedPose = CapturePoseSnapshot(originalHumanoid);
        using var importer = new ModelAssetImporter(relocatedAvatar, onCompleted: null, materialFactory: null)
        {
            ImportOptions = CreateConformanceModelImportOptions(),
        };
        SceneNode relocatedRoot = importer.Import(Assimp.PostProcessSteps.None, onProgress: null)
            ?? throw new InvalidOperationException("Relocated FBX import returned no hierarchy.");
        HumanoidComponent relocatedHumanoid;
        using (HumanoidComponent.BeginDeferredSceneNodeInitialization())
            relocatedHumanoid = relocatedRoot.AddComponent<HumanoidComponent>()!;
        // Persistence must stand on its own after rename/move. Do not reopen the
        // mapping sidecar: deserialize the saved definition, bind it to the newly
        // imported hierarchy, and let the production validation path accept/reject it.
        relocatedHumanoid.AvatarDefinition = persistedDefinition;
        relocatedHumanoid.SetSourceModelContentSha256(ComputeSha256(relocatedAvatar));
        relocatedHumanoid.InitializeSceneNodeBindings();
        relocatedHumanoid.PosePreviewMode = EHumanoidPosePreviewMode.AnimatedPose;
        if (!relocatedHumanoid.TryValidateAvatarDefinitionForPlayback(out string relocatedDiagnostic))
            throw new InvalidOperationException(
                $"Rename/move persisted definition was rejected: {relocatedDiagnostic}");
        if (!string.Equals(originalHumanoid.AvatarDefinition.DefinitionContentSha256,
                relocatedHumanoid.AvatarDefinition.DefinitionContentSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ComputePersistedMappingSignature(originalHumanoid.AvatarDefinition),
                ComputePersistedMappingSignature(relocatedHumanoid.AvatarDefinition), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Rename/move invariance failed: persisted avatar definition or mapping signature changed.");
        var relocatedComponent = relocatedRoot.AddComponent<AnimationClipComponent>()!;
        relocatedComponent.Animation = AnimYamlImporter.Import(relocatedClip);
        relocatedComponent.EvaluateAtTime(sampleTime);
        RequirePoseEquivalent(expectedPose, CapturePoseSnapshot(relocatedHumanoid), "rename/move persisted-definition solve");
        observation.ObservedCapabilities |= HumanoidConformanceCapability.RenameMoveInvariance;
    }

    private static string ComputePersistedMappingSignature(HumanoidAvatarDefinitionMetadata definition)
        => Convert.ToHexString(SHA256.HashData(CookedBinarySerializer.Serialize(definition.Bones)));

    private static HumanoidConformanceDependencyScanResult ScanProductionDependencies(string manifestRoot, HumanoidConformanceManifest manifest)
    {
        string repositoryRoot = FindRepositoryRoot(manifestRoot);
        string[] shippedProjects =
        [
            "XREngine",
            "XREngine.Runtime.AnimationIntegration",
            "XREngine.Editor",
            "XREngine.Server",
            "XREngine.VRClient",
        ];
        string[] productionRoots = shippedProjects
            .Select(project => Path.Combine(repositoryRoot, project))
            .ToArray();
        return HumanoidConformanceSourceDependencyScanner.Scan(
            productionRoots,
            [manifestRoot],
            BuildForbiddenFixtureIdentities(manifest));
    }

    private static List<string> BuildForbiddenFixtureIdentities(HumanoidConformanceManifest manifest)
    {
        var relevantFileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (HumanoidConformanceAvatar avatar in manifest.Avatars)
        {
            relevantFileIds.Add(avatar.SourceFileId);
            if (avatar.MappingMode == HumanoidConformanceMappingMode.PersistedCorrection)
                relevantFileIds.Add(avatar.MappingCorrectionsSourceFileId);
        }
        foreach (HumanoidConformanceMatrixCase matrixCase in manifest.Matrix)
            relevantFileIds.Add(matrixCase.ReferenceFileId);

        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HumanoidConformanceSourceFile source in manifest.SourceFiles)
        {
            if (!relevantFileIds.Contains(source.Id) || string.IsNullOrWhiteSpace(source.RelativePath))
                continue;

            // Keep path identity whole: a basename such as "Basic Walk.anim" can legitimately
            // exist in production while the corpus-relative fixture path must not.
            identities.Add(source.RelativePath.Replace('\\', '/'));
            identities.Add(source.RelativePath.Replace('/', '\\'));
            string stem = Path.GetFileNameWithoutExtension(source.RelativePath);
            if (!string.IsNullOrWhiteSpace(stem))
                identities.Add(stem);
            if (!string.IsNullOrWhiteSpace(source.Signature))
                identities.Add(source.Signature);
        }
        return identities.Order(StringComparer.Ordinal).ToList();
    }

    private static string FindRepositoryRoot(string startingPath)
    {
        for (DirectoryInfo? current = new(Path.GetFullPath(startingPath)); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
        throw new DirectoryNotFoundException("Could not locate repository root from manifest.");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ResolveSourcePath(HumanoidConformanceManifest manifest, string root, string sourceId)
    {
        HumanoidConformanceSourceFile file = manifest.SourceFiles.Single(x => x.Id == sourceId);
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string path = Path.GetFullPath(Path.Combine(fullRoot, file.RelativePath));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Source '{sourceId}' escapes the manifest root.");
        return path;
    }

    private static float QuaternionAngleDegrees(Quaternion left, Quaternion right)
    {
        float dot = Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(left), Quaternion.Normalize(right))), 0.0f, 1.0f);
        return MathF.Acos(dot) * (360.0f / MathF.PI);
    }

    private static string SanitizeFileName(string value)
        => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static Arguments ParseArguments(string[] args)
    {
        string? manifest = null;
        string? output = null;
        bool probeIdentities = false;
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--manifest" && ++i < args.Length) manifest = args[i];
            else if (arg == "--output" && ++i < args.Length) output = args[i];
            else if (arg == "--case" && ++i < args.Length) caseIds.Add(args[i]);
            else if (arg == "--probe-identities") probeIdentities = true;
            else throw new ArgumentException("Usage: --manifest <path> --output <directory> [--case <id>]... [--probe-identities]");
        }
        if (string.IsNullOrWhiteSpace(manifest) || string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("Usage: --manifest <path> --output <directory> [--case <id>]... [--probe-identities]");
        return new Arguments(
            Path.GetFullPath(manifest),
            Path.GetFullPath(output),
            caseIds.Count == 0 ? null : caseIds,
            probeIdentities);
    }
}
