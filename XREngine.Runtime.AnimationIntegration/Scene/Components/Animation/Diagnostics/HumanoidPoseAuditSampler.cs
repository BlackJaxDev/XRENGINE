using System.Numerics;
using XREngine.Animation;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    public static class HumanoidPoseAuditSampler
    {
        private sealed record BoneDefinition(string Name, Func<HumanoidComponent, SceneNode?> ResolveNode);

        private static readonly BoneDefinition[] BoneDefinitions =
        [
            new("Hips", static h => h.Hips.Node),
            new("Spine", static h => h.Spine.Node),
            new("Chest", static h => h.Chest.Node),
            new("UpperChest", static h => h.UpperChest.Node),
            new("Neck", static h => h.Neck.Node),
            new("Head", static h => h.Head.Node),
            new("Jaw", static h => h.Jaw.Node),
            new("LeftEye", static h => h.Left.Eye.Node),
            new("RightEye", static h => h.Right.Eye.Node),
            new("LeftShoulder", static h => h.Left.Shoulder.Node),
            new("LeftUpperArm", static h => h.Left.Arm.Node),
            new("LeftLowerArm", static h => h.Left.Elbow.Node),
            new("LeftHand", static h => h.Left.Wrist.Node),
            new("RightShoulder", static h => h.Right.Shoulder.Node),
            new("RightUpperArm", static h => h.Right.Arm.Node),
            new("RightLowerArm", static h => h.Right.Elbow.Node),
            new("RightHand", static h => h.Right.Wrist.Node),
            new("LeftUpperLeg", static h => h.Left.Leg.Node),
            new("LeftLowerLeg", static h => h.Left.Knee.Node),
            new("LeftFoot", static h => h.Left.Foot.Node),
            new("LeftToes", static h => h.Left.Toes.Node),
            new("RightUpperLeg", static h => h.Right.Leg.Node),
            new("RightLowerLeg", static h => h.Right.Knee.Node),
            new("RightFoot", static h => h.Right.Foot.Node),
            new("RightToes", static h => h.Right.Toes.Node),
        ];

        public static HumanoidPoseAuditReport Sample(AnimationClipComponent clipComponent, HumanoidComponent humanoid, int sampleRateOverride = 0)
        {
            ArgumentNullException.ThrowIfNull(clipComponent);
            ArgumentNullException.ThrowIfNull(humanoid);

            // Evaluating a clip mutates the live local pose even though capture itself reads only
            // local matrices. Serialize the complete sample/restore transaction with rendering so
            // skinning can never consume an intermediate diagnostic pose. Headless hosts execute
            // this inline through the uninstalled scheduling service.
            return RuntimeRenderingHostServices.Scheduling.InvokeRenderThreadTask(
                () => SampleCore(clipComponent, humanoid, sampleRateOverride),
                "HumanoidPoseAuditSampler.Sample");
        }

        private static HumanoidPoseAuditReport SampleCore(AnimationClipComponent clipComponent, HumanoidComponent humanoid, int sampleRateOverride)
        {
            var clip = clipComponent.Animation ?? throw new InvalidOperationException("AnimationClipComponent has no assigned clip.");
            int sampleRate = ResolveSampleRate(clip, sampleRateOverride);
            float duration = Math.Max(0.0f, clip.LengthInSeconds);
            int sampleCount = Math.Max(1, (int)Math.Ceiling(duration * sampleRate) + 1);

            var report = new HumanoidPoseAuditReport
            {
                Source = "XREngine",
                ClipName = clip.Name ?? string.Empty,
                AvatarName = humanoid.SceneNode.Name ?? string.Empty,
                DurationSeconds = duration,
                SampleRate = sampleRate,
                SampleCount = sampleCount,
            };

            using TransformDiagnosticEvaluationScope diagnosticScope = TransformBase.BeginDiagnosticEvaluation();
            long previousTimeTicks = clipComponent.CaptureDiagnosticPlaybackTimeTicks();
            bool wasClipInitialized = clipComponent.CaptureDiagnosticInitializationState();
            HumanoidDiagnosticState humanoidState = humanoid.CaptureDiagnosticState();
            List<(TransformBase Transform, TransformState? FrameState, Matrix4x4 LocalMatrix, TransformDiagnosticInvalidationState InvalidationState)> transformStates =
                CaptureTransformStates(humanoid.SceneNode.Transform);
            try
            {
                CaptureMuscleCalibration(clipComponent, humanoid, report);

                for (int i = 0; i < sampleCount; i++)
                {
                    float sampleTime = sampleCount == 1
                        ? 0.0f
                        : Math.Min(i / (float)sampleRate, duration);

                    clipComponent.EvaluateAtTime(sampleTime);
                    report.Samples.Add(CaptureSample(humanoid, sampleTime, i));
                }
            }
            finally
            {
                try
                {
                    clipComponent.RestoreDiagnosticPlaybackTimeTicks(previousTimeTicks);
                }
                finally
                {
                    try
                    {
                        clipComponent.RestoreDiagnosticInitializationState(wasClipInitialized);
                    }
                    finally
                    {
                        try
                        {
                            humanoid.RestoreDiagnosticState(humanoidState);
                        }
                        finally
                        {
                            RestoreTransformStates(transformStates);
                            ValidateRestoredState(
                                clipComponent,
                                humanoid,
                                previousTimeTicks,
                                wasClipInitialized,
                                humanoidState,
                                transformStates);
                        }
                    }
                }
            }

            return report;
        }

        private static void CaptureMuscleCalibration(
            AnimationClipComponent clipComponent,
            HumanoidComponent humanoid,
            HumanoidPoseAuditReport report)
        {
            clipComponent.EvaluateAtTime(0.0f);
            SetAllMuscles(humanoid, 0.0f);
            humanoid.ApplyCurrentMusclePose();

            report.DefaultMusclePose = CaptureSample(humanoid, 0.0f, -1);
            Dictionary<string, Quaternion> neutralRotations = CaptureBoneLocalRotations(humanoid);

            int muscleIndex = 0;
            foreach (UnityHumanoidMuscleMap.MuscleEntry entry in UnityHumanoidMuscleMap.OrderedMuscleEntries)
            {
                humanoid.SetImportedRawValue(entry.Value, -1.0f);
                humanoid.ApplyCurrentMusclePose();
                Dictionary<string, Quaternion> negativeRotations = CaptureBoneLocalRotations(humanoid);

                humanoid.SetImportedRawValue(entry.Value, 1.0f);
                humanoid.ApplyCurrentMusclePose();
                Dictionary<string, Quaternion> positiveRotations = CaptureBoneLocalRotations(humanoid);

                humanoid.SetImportedRawValue(entry.Value, 0.0f);

                var probe = new HumanoidPoseAuditMuscleProbe
                {
                    Index = muscleIndex++,
                    Name = entry.HumanTraitName,
                };

                foreach (BoneDefinition bone in BoneDefinitions)
                {
                    if (!neutralRotations.TryGetValue(bone.Name, out Quaternion neutralRotation)
                        || !negativeRotations.TryGetValue(bone.Name, out Quaternion negativeRotation)
                        || !positiveRotations.TryGetValue(bone.Name, out Quaternion positiveRotation))
                        continue;

                    Quaternion negativeDelta = NormalizeDelta(neutralRotation, negativeRotation);
                    Quaternion positiveDelta = NormalizeDelta(neutralRotation, positiveRotation);
                    if (QuaternionAngleDegrees(negativeDelta) <= 0.001f
                        && QuaternionAngleDegrees(positiveDelta) <= 0.001f)
                        continue;

                    probe.Bones.Add(new HumanoidPoseAuditMuscleProbeBone
                    {
                        Name = bone.Name,
                        NegativePoseDeltaFromNeutralRotation = HumanoidPoseAuditQuaternion.From(negativeDelta),
                        PositivePoseDeltaFromNeutralRotation = HumanoidPoseAuditQuaternion.From(positiveDelta),
                    });
                }

                report.MuscleProbes.Add(probe);
            }

            humanoid.ApplyCurrentMusclePose();
        }

        private static void SetAllMuscles(HumanoidComponent humanoid, float amount)
        {
            foreach (UnityHumanoidMuscleMap.MuscleEntry entry in UnityHumanoidMuscleMap.OrderedMuscleEntries)
                humanoid.SetImportedRawValue(entry.Value, amount);
        }

        private static Dictionary<string, Quaternion> CaptureBoneLocalRotations(HumanoidComponent humanoid)
        {
            var rotations = new Dictionary<string, Quaternion>(BoneDefinitions.Length, StringComparer.Ordinal);
            foreach (BoneDefinition bone in BoneDefinitions)
            {
                TransformBase? transform = bone.ResolveNode(humanoid)?.Transform;
                if (transform is null)
                    continue;

                rotations[bone.Name] = DecomposeRotation(ReadCurrentLocalMatrix(transform), bone.Name);
            }

            return rotations;
        }

        private static Quaternion NormalizeDelta(Quaternion neutralRotation, Quaternion poseRotation)
            => Quaternion.Normalize(Quaternion.Inverse(neutralRotation) * poseRotation);

        private static float QuaternionAngleDegrees(Quaternion rotation)
        {
            Quaternion normalized = Quaternion.Normalize(rotation);
            float dot = Math.Clamp(MathF.Abs(normalized.W), 0.0f, 1.0f);
            return 2.0f * MathF.Acos(dot) * (180.0f / MathF.PI);
        }

        private static List<(TransformBase Transform, TransformState? FrameState, Matrix4x4 LocalMatrix, TransformDiagnosticInvalidationState InvalidationState)> CaptureTransformStates(TransformBase root)
        {
            var states = new List<(TransformBase Transform, TransformState? FrameState, Matrix4x4 LocalMatrix, TransformDiagnosticInvalidationState InvalidationState)>();
            CaptureTransformStatesRecursive(root, states);
            return states;
        }

        private static void CaptureTransformStatesRecursive(
            TransformBase transform,
            List<(TransformBase Transform, TransformState? FrameState, Matrix4x4 LocalMatrix, TransformDiagnosticInvalidationState InvalidationState)> states)
        {
            TransformDiagnosticInvalidationState invalidationState = transform.CaptureDiagnosticInvalidationState();
            if (transform.IsLocalMatrixDirty)
                transform.RecalcLocal();

            states.Add((
                transform,
                transform is Transform concrete ? concrete.FrameState : null,
                transform.LocalMatrix,
                invalidationState));

            foreach (TransformBase child in transform.Children)
                CaptureTransformStatesRecursive(child, states);
        }

        private static void RestoreTransformStates(
            List<(TransformBase Transform, TransformState? FrameState, Matrix4x4 LocalMatrix, TransformDiagnosticInvalidationState InvalidationState)> states)
        {
            foreach ((TransformBase transform, TransformState? frameState, Matrix4x4 localMatrix, _) in states)
            {
                if (transform is Transform concrete && frameState.HasValue)
                    concrete.SetFrameState(frameState.Value);
                else
                    transform.DeriveLocalMatrix(localMatrix);
            }

            foreach ((TransformBase transform, _, _, TransformDiagnosticInvalidationState invalidationState) in states)
                transform.RestoreDiagnosticInvalidationState(invalidationState);
        }

        private static void ValidateRestoredState(
            AnimationClipComponent clipComponent,
            HumanoidComponent humanoid,
            long playbackTimeTicks,
            bool wasClipInitialized,
            HumanoidDiagnosticState humanoidState,
            List<(TransformBase Transform, TransformState? FrameState, Matrix4x4 LocalMatrix, TransformDiagnosticInvalidationState InvalidationState)> transformStates)
        {
            if (clipComponent.CaptureDiagnosticPlaybackTimeTicks() != playbackTimeTicks
                || clipComponent.CaptureDiagnosticInitializationState() != wasClipInitialized)
                throw new InvalidOperationException("Humanoid pose audit did not restore the animation component clock and initialization state exactly.");

            if (!humanoid.DiagnosticStateMatches(humanoidState))
                throw new InvalidOperationException("Humanoid pose audit did not restore the humanoid value caches exactly.");

            foreach ((TransformBase transform, TransformState? frameState, Matrix4x4 localMatrix, TransformDiagnosticInvalidationState invalidationState) in transformStates)
            {
                bool poseMatches = transform is Transform concrete && frameState.HasValue
                    ? concrete.FrameState.Equals(frameState.Value)
                    : transform.LocalMatrix.Equals(localMatrix);
                TransformDiagnosticInvalidationState currentInvalidationState = transform.CaptureDiagnosticInvalidationState();
                bool invalidationMatches = currentInvalidationState.IsLocalMatrixDirty == invalidationState.IsLocalMatrixDirty
                    && currentInvalidationState.IsWorldMatrixDirty == invalidationState.IsWorldMatrixDirty
                    && currentInvalidationState.HasChanged == invalidationState.HasChanged;
                if (!poseMatches || !invalidationMatches)
                    throw new InvalidOperationException(
                        $"Humanoid pose audit did not restore transform '{transform.SceneNode?.Name ?? transform.Name}' exactly.");
            }
        }

        private static HumanoidPoseAuditSample CaptureSample(HumanoidComponent humanoid, float sampleTime, int index)
        {
            HumanoidImportedBodySample currentBody = humanoid.CurrentImportedMappedBodySample;
            HumanoidImportedBodySample canonicalBody = humanoid.CanonicalImportedBodySample;
            var sample = new HumanoidPoseAuditSample
            {
                Index = index,
                TimeSeconds = sampleTime,
                BodyPosition = CaptureBodyPosition(humanoid),
                BodyRotation = CaptureBodyRotation(humanoid),
                ImportedMappedBodyPosition = HumanoidPoseAuditVector3.From(currentBody.Position),
                ImportedMappedBodyRotation = HumanoidPoseAuditQuaternion.FromRaw(humanoid.CurrentRawBodyRotation),
                ImportedMappedBodyChannels = (int)currentBody.Channels,
                CanonicalImportedMappedBodyPosition = HumanoidPoseAuditVector3.From(canonicalBody.Position),
                CanonicalImportedMappedBodyRotation = HumanoidPoseAuditQuaternion.FromRaw(canonicalBody.Rotation),
                CanonicalImportedMappedBodyChannels = (int)canonicalBody.Channels,
                ConvertedBodyTranslationDelta = HumanoidPoseAuditVector3.From(humanoid.CurrentConvertedBodyTranslationDelta),
                ConvertedBodyRotationDelta = HumanoidPoseAuditQuaternion.From(humanoid.CurrentConvertedBodyRotationDelta),
            };

            CaptureComposedTransforms(humanoid, sample);

            foreach (UnityHumanoidMuscleMap.MuscleEntry entry in UnityHumanoidMuscleMap.OrderedMuscleEntries)
            {
                humanoid.TryGetMuscleValue(entry.Value, out float amount);
                sample.Muscles.Add(new HumanoidPoseAuditNamedFloat
                {
                    Name = entry.HumanTraitName,
                    Value = amount,
                });

                if (!humanoid.TryGetRawHumanoidValue(entry.Value, out float rawAmount))
                    continue;

                sample.RawCurves.Add(new HumanoidPoseAuditRawCurveSample
                {
                    Path = string.Empty,
                    TypeName = typeof(HumanoidComponent).FullName ?? nameof(HumanoidComponent),
                    PropertyName = entry.CurveAttributeName,
                    Value = rawAmount,
                });
            }

            TransformBase humanoidRoot = humanoid.SceneNode.Transform;
            Matrix4x4 rootWorld = ComposeWorldFromLocals(humanoidRoot);
            foreach (var bone in BoneDefinitions)
            {
                var node = bone.ResolveNode(humanoid);
                TransformBase? transform = node?.Transform;
                if (transform is null)
                    continue;

                Matrix4x4 local = ReadCurrentLocalMatrix(transform);
                Matrix4x4 rootSpace = ComposeRelativeToAncestor(transform, humanoidRoot);
                Matrix4x4 world = rootSpace * rootWorld;
                Quaternion localRotation = DecomposeRotation(local, bone.Name);
                Quaternion bindRelativeRotation = humanoid.TryGetDiagnosticBindLocalRotation(node!, out Quaternion bindLocalRotation)
                    ? Quaternion.Normalize(Quaternion.Inverse(bindLocalRotation) * localRotation)
                    : Quaternion.Identity;
                Quaternion neutralBindRelativeRotation = humanoid.GetDiagnosticNeutralBindRelativeRotation(node!);
                Quaternion poseDeltaFromNeutralRotation = Quaternion.Normalize(
                    Quaternion.Inverse(neutralBindRelativeRotation) * bindRelativeRotation);

                sample.Bones.Add(new HumanoidPoseAuditBoneSample
                {
                    Name = bone.Name,
                    LocalPosition = HumanoidPoseAuditVector3.From(local.Translation),
                    LocalRotation = HumanoidPoseAuditQuaternion.From(localRotation),
                    BindRelativeRotation = HumanoidPoseAuditQuaternion.From(bindRelativeRotation),
                    NeutralBindRelativeRotation = HumanoidPoseAuditQuaternion.From(neutralBindRelativeRotation),
                    PoseDeltaFromNeutralRotation = HumanoidPoseAuditQuaternion.From(poseDeltaFromNeutralRotation),
                    RootSpacePosition = HumanoidPoseAuditVector3.From(rootSpace.Translation),
                    WorldPosition = HumanoidPoseAuditVector3.From(world.Translation),
                });
            }

            return sample;
        }

        private static void CaptureComposedTransforms(HumanoidComponent humanoid, HumanoidPoseAuditSample sample)
        {
            TransformBase characterRoot = humanoid.SceneNode.Transform;
            Matrix4x4 characterLocal = ReadCurrentLocalMatrix(characterRoot);
            Matrix4x4 characterWorld = ComposeWorldFromLocals(characterRoot);
            sample.CharacterRootLocalPosition = HumanoidPoseAuditVector3.From(characterLocal.Translation);
            sample.CharacterRootLocalRotation = HumanoidPoseAuditQuaternion.From(DecomposeRotation(characterLocal, humanoid.SceneNode.Name));
            sample.CharacterRootWorldPosition = HumanoidPoseAuditVector3.From(characterWorld.Translation);
            sample.CharacterRootWorldRotation = HumanoidPoseAuditQuaternion.From(DecomposeRotation(characterWorld, humanoid.SceneNode.Name));

            TransformBase? hips = humanoid.Hips.Node?.Transform;
            if (hips is null)
                return;

            Matrix4x4 hipsLocal = ReadCurrentLocalMatrix(hips);
            sample.ComposedHipsLocalPosition = HumanoidPoseAuditVector3.From(hipsLocal.Translation);
            sample.ComposedHipsLocalRotation = HumanoidPoseAuditQuaternion.From(DecomposeRotation(hipsLocal, "Hips"));
        }

        private static Matrix4x4 ComposeRelativeToAncestor(TransformBase transform, TransformBase ancestor)
        {
            if (ReferenceEquals(transform, ancestor))
                return Matrix4x4.Identity;

            Matrix4x4 result = ReadCurrentLocalMatrix(transform);
            TransformBase? parent = transform.Parent;
            while (parent is not null && !ReferenceEquals(parent, ancestor))
            {
                result *= ReadCurrentLocalMatrix(parent);
                parent = parent.Parent;
            }

            if (parent is null)
                throw new InvalidOperationException(
                    $"Transform '{transform.SceneNode?.Name ?? transform.Name}' is not a descendant of humanoid root '{ancestor.SceneNode?.Name ?? ancestor.Name}'.");

            return result;
        }

        private static Matrix4x4 ComposeWorldFromLocals(TransformBase transform)
        {
            Matrix4x4 result = ReadCurrentLocalMatrix(transform);
            for (TransformBase? parent = transform.Parent; parent is not null; parent = parent.Parent)
                result *= ReadCurrentLocalMatrix(parent);

            return result;
        }

        private static Matrix4x4 ReadCurrentLocalMatrix(TransformBase transform)
        {
            if (transform.IsLocalMatrixDirty)
                throw new InvalidOperationException(
                    $"Humanoid pose audit cannot read dirty local matrix for '{transform.SceneNode?.Name ?? transform.Name}'. " +
                    "The sampled transform type must publish its local pose before side-effect-free audit capture.");

            return transform.LocalMatrix;
        }

        private static Quaternion DecomposeRotation(Matrix4x4 matrix, string? transformName)
        {
            if (Matrix4x4.Decompose(matrix, out _, out Quaternion rotation, out _))
                return Quaternion.Normalize(rotation);

            throw new InvalidOperationException($"Could not decompose transform matrix for '{transformName ?? "<unnamed>"}'.");
        }

        private static HumanoidPoseAuditVector3 CaptureBodyPosition(HumanoidComponent humanoid)
            => HumanoidPoseAuditVector3.From(humanoid.CurrentRawBodyPosition);

        private static HumanoidPoseAuditQuaternion CaptureBodyRotation(HumanoidComponent humanoid)
            => HumanoidPoseAuditQuaternion.From(humanoid.CurrentRawBodyRotation);

        private static int ResolveSampleRate(AnimationClip clip, int sampleRateOverride)
        {
            if (sampleRateOverride > 0)
                return sampleRateOverride;

            if (clip.SampleRate > 0)
                return clip.SampleRate;

            return 30;
        }
    }
}
