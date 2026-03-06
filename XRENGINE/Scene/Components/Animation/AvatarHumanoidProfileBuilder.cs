using System.Numerics;
using XREngine.Scene;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Derives a complete <see cref="HumanoidSettings"/> profile from a mapped skeleton,
    /// including per-bone axis mappings and a per-bone confidence score.
    /// <para>
    /// Designed to be called once after bone discovery (<c>SetFromNode</c>) and cached
    /// per avatar/model. The profile includes everything the muscle-application pipeline
    /// needs to produce correct rotations without manual per-rig tuning.
    /// </para>
    /// </summary>
    public static class AvatarHumanoidProfileBuilder
    {
        /// <summary>
        /// Result of building an avatar profile. Contains the overall confidence
        /// and per-bone detail entries.
        /// </summary>
        public sealed class ProfileResult
        {
            /// <summary>
            /// Overall calibration confidence in [0, 1]. Aggregated from per-bone scores.
            /// </summary>
            public float OverallConfidence { get; init; }

            /// <summary>
            /// Per-bone detail entries keyed by bone name.
            /// </summary>
            public IReadOnlyDictionary<string, BoneProfileEntry> BoneEntries { get; init; } = new Dictionary<string, BoneProfileEntry>();

            /// <summary>
            /// Number of bones that were successfully profiled.
            /// </summary>
            public int ProfiledBoneCount { get; init; }

            /// <summary>
            /// Number of bones that fell back to default axis mapping.
            /// </summary>
            public int FallbackBoneCount { get; init; }
        }

        /// <summary>
        /// Per-bone profiling detail.
        /// </summary>
        public readonly struct BoneProfileEntry
        {
            public required string BoneName { get; init; }
            public required BoneAxisMapping Mapping { get; init; }
            /// <summary>
            /// Confidence in [0, 1] for this bone's axis mapping.
            /// 1.0 = geometry-detected with strong dominant axis.
            /// 0.5 = inherited from parent or weak axis dominance.
            /// 0.0 = pure default fallback.
            /// </summary>
            public required float Confidence { get; init; }
            /// <summary>
            /// Human-readable reason for the confidence level.
            /// </summary>
            public required string Reason { get; init; }
        }

        // Minimum dominance ratio: e.g. if largest axis component is >= 2x the next,
        // we consider it a strong detection.
        private const float StrongDominanceThreshold = 0.7f;
        private const float WeakDominanceThreshold = 0.4f;

        /// <summary>
        /// Builds a complete humanoid profile for the given component's skeleton.
        /// Populates <see cref="HumanoidSettings.BoneAxisMappings"/> and sets
        /// <see cref="HumanoidSettings.ProfileConfidence"/>.
        /// </summary>
        /// <param name="component">The humanoid component whose skeleton has already been mapped.</param>
        /// <returns>A <see cref="ProfileResult"/> describing the profiling outcome.</returns>
        public static ProfileResult BuildProfile(HumanoidComponent component)
        {
            var settings = component.Settings;
            var entries = new Dictionary<string, BoneProfileEntry>(StringComparer.OrdinalIgnoreCase);

            // ── Spine chain ─────────────────────────────────────────────
            ProfileBone(entries, settings, component.Hips, component.Spine, null);
            ProfileBone(entries, settings, component.Spine, component.Chest, component.Hips);
            var chestChild = component.UpperChest.Node is not null ? component.UpperChest : component.Neck;
            ProfileBone(entries, settings, component.Chest, chestChild, component.Spine);
            if (component.UpperChest.Node is not null)
                ProfileBone(entries, settings, component.UpperChest, component.Neck, component.Chest);
            ProfileBone(entries, settings, component.Neck, component.Head, component.Chest);

            // ── Left side ───────────────────────────────────────────────
            ProfileLimbs(entries, settings, component.Left, component);

            // ── Right side ──────────────────────────────────────────────
            ProfileLimbs(entries, settings, component.Right, component);

            // ── Aggregate confidence ────────────────────────────────────
            int totalBones = entries.Count;
            int fallbackCount = 0;
            float sumConfidence = 0.0f;
            foreach (var e in entries.Values)
            {
                sumConfidence += e.Confidence;
                if (e.Confidence < 0.3f)
                    fallbackCount++;
            }

            float overall = totalBones > 0 ? sumConfidence / totalBones : 0.0f;

            // Apply results to settings
            settings.ProfileConfidence = overall;

            // Mark IK as calibrated if overall confidence is high
            if (overall >= 0.6f)
                settings.IsIKCalibrated = true;

            return new ProfileResult
            {
                OverallConfidence = overall,
                BoneEntries = entries,
                ProfiledBoneCount = totalBones,
                FallbackBoneCount = fallbackCount,
            };
        }

        private static void ProfileLimbs(
            Dictionary<string, BoneProfileEntry> entries,
            HumanoidSettings settings,
            HumanoidComponent.BodySide side,
            HumanoidComponent component)
        {
            // Arm chain
            ProfileBone(entries, settings, side.Shoulder, side.Arm, null);
            ProfileBone(entries, settings, side.Arm, side.Elbow, side.Shoulder);
            ProfileBone(entries, settings, side.Elbow, side.Wrist, side.Arm);

            // Leg chain
            ProfileBone(entries, settings, side.Leg, side.Knee, component.Hips);
            ProfileBone(entries, settings, side.Knee, side.Foot, side.Leg);
            ProfileBone(entries, settings, side.Foot, side.Toes, side.Knee);

            // Fingers  
            ProfileFingerChain(entries, settings, side.Hand.Index);
            ProfileFingerChain(entries, settings, side.Hand.Middle);
            ProfileFingerChain(entries, settings, side.Hand.Ring);
            ProfileFingerChain(entries, settings, side.Hand.Pinky);
            ProfileFingerChain(entries, settings, side.Hand.Thumb);
        }

        private static void ProfileFingerChain(
            Dictionary<string, BoneProfileEntry> entries,
            HumanoidSettings settings,
            HumanoidComponent.BodySide.Fingers.Finger finger)
        {
            ProfileBone(entries, settings, finger.Proximal, finger.Intermediate, null);
            ProfileBone(entries, settings, finger.Intermediate, finger.Distal, finger.Proximal);
        }

        /// <summary>
        /// Profiles a single bone: detects axis mapping from geometry, computes confidence,
        /// and stores the result both in the entries dictionary and in <paramref name="settings"/>.
        /// </summary>
        private static void ProfileBone(
            Dictionary<string, BoneProfileEntry> entries,
            HumanoidSettings settings,
            HumanoidComponent.BoneDef bone,
            HumanoidComponent.BoneDef childBone,
            HumanoidComponent.BoneDef? parentBone)
        {
            if (bone.Node?.Name is null)
                return;

            string boneName = bone.Node.Name;

            // Don't override user-configured mappings — they have maximum confidence.
            if (settings.TryGetBoneAxisMapping(boneName, out var existingMapping))
            {
                // Legacy migration: older mappings may have no sign fields (0).
                // Preserve user-selected axes, but upgrade missing polarity signs automatically.
                if (NeedsSignUpgrade(existingMapping))
                {
                    var (detected, _, _) = DetectAxisMapping(bone, childBone, parentBone, settings);
                    existingMapping = UpgradeMissingSigns(existingMapping, detected);
                    settings.BoneAxisMappings[boneName] = existingMapping;
                }

                entries[boneName] = new BoneProfileEntry
                {
                    BoneName = boneName,
                    Mapping = existingMapping,
                    Confidence = 1.0f,
                    Reason = "User-configured mapping",
                };
                return;
            }

            // Attempt geometry-based detection
            var (mapping, confidence, reason) = DetectAxisMapping(bone, childBone, parentBone, settings);

            settings.BoneAxisMappings[boneName] = mapping;
            entries[boneName] = new BoneProfileEntry
            {
                BoneName = boneName,
                Mapping = mapping,
                Confidence = confidence,
                Reason = reason,
            };
        }

        private static bool NeedsSignUpgrade(BoneAxisMapping mapping)
            => mapping.TwistSign == 0 || mapping.FrontBackSign == 0 || mapping.LeftRightSign == 0;

        private static BoneAxisMapping UpgradeMissingSigns(BoneAxisMapping existing, BoneAxisMapping detected)
        {
            int twistSign = existing.TwistSign != 0
                ? existing.TwistSign
                : SignForAxis(detected, existing.TwistAxis);

            int frontBackSign = existing.FrontBackSign != 0
                ? existing.FrontBackSign
                : SignForAxis(detected, existing.FrontBackAxis);

            int leftRightSign = existing.LeftRightSign != 0
                ? existing.LeftRightSign
                : SignForAxis(detected, existing.LeftRightAxis);

            return new BoneAxisMapping
            {
                TwistAxis = existing.TwistAxis,
                TwistSign = twistSign,
                FrontBackAxis = existing.FrontBackAxis,
                FrontBackSign = frontBackSign,
                LeftRightAxis = existing.LeftRightAxis,
                LeftRightSign = leftRightSign,
            };
        }

        private static int SignForAxis(BoneAxisMapping mapping, int axis)
        {
            if (mapping.TwistAxis == axis)
                return mapping.TwistSign != 0 ? mapping.TwistSign : 1;

            if (mapping.FrontBackAxis == axis)
                return mapping.FrontBackSign != 0 ? mapping.FrontBackSign : 1;

            if (mapping.LeftRightAxis == axis)
                return mapping.LeftRightSign != 0 ? mapping.LeftRightSign : 1;

            return 1;
        }

        private static (BoneAxisMapping mapping, float confidence, string reason) DetectAxisMapping(
            HumanoidComponent.BoneDef bone,
            HumanoidComponent.BoneDef childBone,
            HumanoidComponent.BoneDef? parentBone,
            HumanoidSettings settings)
        {
            if (childBone.Node is null)
            {
                // No child bone — try to inherit from parent
                return InheritOrDefault(bone, parentBone, settings, "no child bone");
            }

            Vector3 boneWorldPos = bone.WorldBindPose.Translation;
            Vector3 childWorldPos = childBone.WorldBindPose.Translation;
            Vector3 dirWorld = childWorldPos - boneWorldPos;

            if (dirWorld.LengthSquared() < 1e-8f)
            {
                // Near-zero distance — can't determine axis from geometry
                return InheritOrDefault(bone, parentBone, settings, "near-zero bone→child distance");
            }

            dirWorld = Vector3.Normalize(dirWorld);

            if (!Matrix4x4.Invert(bone.WorldBindPose, out Matrix4x4 invBind))
            {
                return InheritOrDefault(bone, parentBone, settings, "bind matrix not invertible");
            }

            Vector3 dirLocal = Vector3.TransformNormal(dirWorld, invBind);
            float localLen = dirLocal.Length();
            if (localLen < 1e-8f)
            {
                return InheritOrDefault(bone, parentBone, settings, "degenerate local direction");
            }
            dirLocal /= localLen;

            float ax = MathF.Abs(dirLocal.X);
            float ay = MathF.Abs(dirLocal.Y);
            float az = MathF.Abs(dirLocal.Z);

            // Find dominant axis
            int twistAxis, frontBackAxis, leftRightAxis;
            int twistSign;
            float dominance;

            if (ax >= ay && ax >= az)
            {
                twistAxis = 0; frontBackAxis = 1; leftRightAxis = 2;
                twistSign = SignOrOne(dirLocal.X);
                dominance = ax;
            }
            else if (az >= ax && az >= ay)
            {
                twistAxis = 2; frontBackAxis = 0; leftRightAxis = 1;
                twistSign = SignOrOne(dirLocal.Z);
                dominance = az;
            }
            else
            {
                twistAxis = 1; frontBackAxis = 0; leftRightAxis = 2;
                twistSign = SignOrOne(dirLocal.Y);
                dominance = ay;
            }

            // Bone→child direction only tells us twist polarity reliably.
            // The two swing axes are perpendicular to that direction, so inferring their sign from
            // tiny off-axis bind-pose noise produces unstable left/right mirroring bugs. Reuse the
            // parent/avatar basis for swing polarity instead.
            int frontBackSign = ResolveSwingAxisSign(parentBone, settings, frontBackAxis);
            int leftRightSign = ResolveSwingAxisSign(parentBone, settings, leftRightAxis);

            // Compute confidence based on how clearly one axis dominates
            float confidence;
            string reason;
            if (dominance >= StrongDominanceThreshold)
            {
                confidence = 1.0f;
                reason = $"Strong axis detection (dominance={dominance:F3}, local=({dirLocal.X:F3},{dirLocal.Y:F3},{dirLocal.Z:F3}))";
            }
            else if (dominance >= WeakDominanceThreshold)
            {
                confidence = 0.5f + 0.5f * ((dominance - WeakDominanceThreshold) / (StrongDominanceThreshold - WeakDominanceThreshold));
                reason = $"Weak axis detection (dominance={dominance:F3}, local=({dirLocal.X:F3},{dirLocal.Y:F3},{dirLocal.Z:F3}))";
            }
            else
            {
                // All axes are nearly equal — very ambiguous
                confidence = 0.3f;
                reason = $"Ambiguous axis detection (dominance={dominance:F3}, local=({dirLocal.X:F3},{dirLocal.Y:F3},{dirLocal.Z:F3}))";
            }

            var mapping = new BoneAxisMapping
            {
                TwistAxis = twistAxis,
                TwistSign = twistSign,
                FrontBackAxis = frontBackAxis,
                FrontBackSign = frontBackSign,
                LeftRightAxis = leftRightAxis,
                LeftRightSign = leftRightSign,
            };

            return (mapping, confidence, reason);
        }

        private static (BoneAxisMapping mapping, float confidence, string reason) InheritOrDefault(
            HumanoidComponent.BoneDef bone,
            HumanoidComponent.BoneDef? parentBone,
            HumanoidSettings settings,
            string detailReason)
        {
            // Attempt parent-bone inheritance
            if (parentBone?.Node?.Name is string parentName &&
                settings.TryGetBoneAxisMapping(parentName, out var parentMapping))
            {
                return (parentMapping, 0.5f,
                    $"Inherited from parent '{parentName}' ({detailReason})");
            }

            // Fall back to default
            return (BoneAxisMapping.Default, 0.0f,
                $"Default fallback ({detailReason})");
        }

        private static int SignOrOne(float value)
            => value < 0.0f ? -1 : 1;

        private static int ResolveSwingAxisSign(
            HumanoidComponent.BoneDef? parentBone,
            HumanoidSettings settings,
            int axis)
        {
            if (parentBone?.Node?.Name is string parentName &&
                settings.TryGetBoneAxisMapping(parentName, out var parentMapping))
            {
                return SignForAxis(parentMapping, axis);
            }

            return 1;
        }

        /// <summary>
        /// Logs a summary of the profile result to the Animation diagnostic category.
        /// </summary>
        public static void LogProfileSummary(ProfileResult result, string avatarName)
        {
            Debug.Animation(
                $"[AvatarProfile] '{avatarName}': " +
                $"confidence={result.OverallConfidence:P0} " +
                $"bones={result.ProfiledBoneCount} " +
                $"fallbacks={result.FallbackBoneCount}");

            if (result.OverallConfidence < 0.6f)
            {
                Debug.Animation(
                    $"[AvatarProfile] WARNING: Low calibration confidence for '{avatarName}'. " +
                    "Some bone rotations may appear incorrect. Check bone naming and bind pose.");
            }

            // Log per-bone details for bones with low confidence
            foreach (var entry in result.BoneEntries.Values)
            {
                if (entry.Confidence < 0.5f)
                {
                    Debug.Animation(
                        $"[AvatarProfile]   LOW: '{entry.BoneName}' " +
                        $"confidence={entry.Confidence:F2} " +
                        $"twist={entry.Mapping.TwistAxis}({entry.Mapping.TwistSign:+#;-#}) " +
                        $"fb={entry.Mapping.FrontBackAxis}({entry.Mapping.FrontBackSign:+#;-#}) " +
                        $"lr={entry.Mapping.LeftRightAxis}({entry.Mapping.LeftRightSign:+#;-#}) " +
                        $"reason={entry.Reason}");
                }
            }
        }

        /// <summary>
        /// Logs the full per-bone axis mapping dump (useful for debugging).
        /// </summary>
        public static void LogFullAxisDump(ProfileResult result, string avatarName)
        {
            Debug.Animation($"[AvatarProfile] Full axis dump for '{avatarName}':");
            foreach (var entry in result.BoneEntries.Values)
            {
                Debug.Animation(
                    $"[AvatarProfile]   '{entry.BoneName,-25}' " +
                    $"twist={entry.Mapping.TwistAxis}({entry.Mapping.TwistSign:+#;-#}) " +
                    $"fb={entry.Mapping.FrontBackAxis}({entry.Mapping.FrontBackSign:+#;-#}) " +
                    $"lr={entry.Mapping.LeftRightAxis}({entry.Mapping.LeftRightSign:+#;-#}) " +
                    $"conf={entry.Confidence:F2} " +
                    $"({entry.Reason})");
            }
        }
    }
}
