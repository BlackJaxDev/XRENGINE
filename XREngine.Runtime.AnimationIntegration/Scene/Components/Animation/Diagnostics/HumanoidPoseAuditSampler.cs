using System.Numerics;
using XREngine.Animation;
using XREngine.Scene;
using Transform = XREngine.Scene.Transforms.Transform;

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

            float previousTime = clipComponent.PlaybackTime;
            for (int i = 0; i < sampleCount; i++)
            {
                float sampleTime = sampleCount == 1
                    ? 0.0f
                    : Math.Min(i / (float)sampleRate, duration);

                clipComponent.EvaluateAtTime(sampleTime);
                report.Samples.Add(CaptureSample(humanoid, sampleTime, i));
            }

            clipComponent.EvaluateAtTime(previousTime);
            return report;
        }

        private static HumanoidPoseAuditSample CaptureSample(HumanoidComponent humanoid, float sampleTime, int index)
        {
            var sample = new HumanoidPoseAuditSample
            {
                Index = index,
                TimeSeconds = sampleTime,
                BodyPosition = CaptureBodyPosition(humanoid),
                BodyRotation = CaptureBodyRotation(humanoid),
            };

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

            Matrix4x4 rootInverse = humanoid.SceneNode.Transform.InverseWorldMatrix;
            foreach (var bone in BoneDefinitions)
            {
                var node = bone.ResolveNode(humanoid);
                var transform = node?.GetTransformAs<Transform>(true);
                if (transform is null)
                    continue;

                sample.Bones.Add(new HumanoidPoseAuditBoneSample
                {
                    Name = bone.Name,
                    LocalRotation = HumanoidPoseAuditQuaternion.From(transform.Rotation),
                    RootSpacePosition = HumanoidPoseAuditVector3.From(Vector3.Transform(transform.WorldTranslation, rootInverse)),
                    WorldPosition = HumanoidPoseAuditVector3.From(transform.WorldTranslation),
                });
            }

            return sample;
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
