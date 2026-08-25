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
            /// Per-bone detail entries keyed by the resolved scene node. Node
            /// identity avoids collisions between duplicate display names.
            /// </summary>
            public IReadOnlyDictionary<SceneNode, BoneProfileEntry> BoneEntries { get; init; } =
                new Dictionary<SceneNode, BoneProfileEntry>(ReferenceEqualityComparer.Instance);

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
            var entries = new Dictionary<SceneNode, BoneProfileEntry>(ReferenceEqualityComparer.Instance);
            var authoredMappings = new Dictionary<string, BoneAxisMapping>(
                settings.BoneAxisMappings,
                StringComparer.OrdinalIgnoreCase);
            GetBindBodyBasis(component, out Vector3 bodyLeft, out Vector3 bodyUp, out Vector3 bodyForward);

            // ── Spine chain ─────────────────────────────────────────────
            ProfileBone(entries, authoredMappings, settings, component.Hips, component.Spine, null);
            ProfileBone(entries, authoredMappings, settings, component.Spine, component.Chest, component.Hips);
            var chestChild = component.UpperChest.Node is not null ? component.UpperChest : component.Neck;
            ProfileBone(entries, authoredMappings, settings, component.Chest, chestChild, component.Spine);
            if (component.UpperChest.Node is not null)
                ProfileBone(entries, authoredMappings, settings, component.UpperChest, component.Neck, component.Chest);
            ProfileBone(entries, authoredMappings, settings, component.Neck, component.Head, component.Chest);
            ProfileBone(entries, authoredMappings, settings, component.Head, null, component.Neck.Node is not null ? component.Neck : component.Chest);

            // ── Left side ───────────────────────────────────────────────
            ProfileLimbs(entries, authoredMappings, settings, component.Left, component, isLeft: true, bodyLeft, bodyUp, bodyForward);

            // ── Right side ──────────────────────────────────────────────
            ProfileLimbs(entries, authoredMappings, settings, component.Right, component, isLeft: false, bodyLeft, bodyUp, bodyForward);

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
            Dictionary<SceneNode, BoneProfileEntry> entries,
            IReadOnlyDictionary<string, BoneAxisMapping> authoredMappings,
            HumanoidSettings settings,
            HumanoidComponent.BodySide side,
            HumanoidComponent component,
            bool isLeft,
            Vector3 bodyLeft,
            Vector3 bodyUp,
            Vector3 bodyForward)
        {
            Vector3 armPitchAxisWorld = isLeft ? bodyForward : -bodyForward;
            Vector3 armRollAxisWorld = isLeft ? -bodyUp : bodyUp;
            Vector3 legPitchAxisWorld = -bodyLeft;
            Vector3 legRollAxisWorld = isLeft ? -bodyForward : bodyForward;

            // Arm chain
            ProfileBone(entries, authoredMappings, settings, side.Shoulder, side.Arm, null, armPitchAxisWorld, armRollAxisWorld);
            ProfileBone(entries, authoredMappings, settings, side.Arm, side.Elbow, side.Shoulder, armPitchAxisWorld, armRollAxisWorld);
            ProfileBone(entries, authoredMappings, settings, side.Elbow, side.Wrist, side.Arm, armPitchAxisWorld, armRollAxisWorld);
            ProfileBone(entries, authoredMappings, settings, side.Wrist, null, side.Elbow, armPitchAxisWorld, armRollAxisWorld);

            // Leg chain
            ProfileBone(entries, authoredMappings, settings, side.Leg, side.Knee, component.Hips, legPitchAxisWorld, legRollAxisWorld);
            ProfileBone(entries, authoredMappings, settings, side.Knee, side.Foot, side.Leg, legPitchAxisWorld, legRollAxisWorld);
            ProfileBone(entries, authoredMappings, settings, side.Foot, side.Toes, side.Knee, legPitchAxisWorld, legRollAxisWorld);

            // Fingers  
            ProfileFingerChain(entries, authoredMappings, settings, side.Hand.Index);
            ProfileFingerChain(entries, authoredMappings, settings, side.Hand.Middle);
            ProfileFingerChain(entries, authoredMappings, settings, side.Hand.Ring);
            ProfileFingerChain(entries, authoredMappings, settings, side.Hand.Pinky);
            ProfileFingerChain(entries, authoredMappings, settings, side.Hand.Thumb);
        }

        private static void ProfileFingerChain(
            Dictionary<SceneNode, BoneProfileEntry> entries,
            IReadOnlyDictionary<string, BoneAxisMapping> authoredMappings,
            HumanoidSettings settings,
            HumanoidComponent.BodySide.Fingers.Finger finger)
        {
            ProfileBone(entries, authoredMappings, settings, finger.Proximal, finger.Intermediate, null);
            ProfileBone(entries, authoredMappings, settings, finger.Intermediate, finger.Distal, finger.Proximal);
        }

        /// <summary>
        /// Profiles a single bone: detects axis mapping from geometry, computes confidence,
        /// and stores the result both in the entries dictionary and in <paramref name="settings"/>.
        /// </summary>
        private static void ProfileBone(
            Dictionary<SceneNode, BoneProfileEntry> entries,
            IReadOnlyDictionary<string, BoneAxisMapping> authoredMappings,
            HumanoidSettings settings,
            HumanoidComponent.BoneDef bone,
            HumanoidComponent.BoneDef? childBone,
            HumanoidComponent.BoneDef? parentBone,
            Vector3? preferredPitchAxisWorld = null,
            Vector3? preferredRollAxisWorld = null)
        {
            if (bone.Node is not SceneNode node || node.Name is not string boneName)
                return;
            if (entries.ContainsKey(node))
                return;

            // Don't override user-configured mappings — they have maximum confidence.
            if (authoredMappings.TryGetValue(boneName, out var existingMapping))
            {
                // Legacy migration: older mappings may have no sign fields (0).
                // Preserve user-selected axes, but upgrade missing polarity signs automatically.
                if (NeedsSignUpgrade(existingMapping))
                {
                    var (detected, _, _) = DetectAxisMapping(
                        entries,
                        authoredMappings,
                        bone,
                        childBone,
                        parentBone,
                        preferredPitchAxisWorld,
                        preferredRollAxisWorld);
                    existingMapping = UpgradeMissingSigns(existingMapping, detected);
                    settings.BoneAxisMappings[boneName] = existingMapping;
                }

                entries[node] = new BoneProfileEntry
                {
                    BoneName = boneName,
                    Mapping = existingMapping,
                    Confidence = 1.0f,
                    Reason = "User-configured mapping",
                };
                return;
            }

            // Attempt geometry-based detection
            var (mapping, confidence, reason) = DetectAxisMapping(
                entries,
                authoredMappings,
                bone,
                childBone,
                parentBone,
                preferredPitchAxisWorld,
                preferredRollAxisWorld);

            settings.BoneAxisMappings[boneName] = mapping;
            entries[node] = new BoneProfileEntry
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
            IReadOnlyDictionary<SceneNode, BoneProfileEntry> entries,
            IReadOnlyDictionary<string, BoneAxisMapping> authoredMappings,
            HumanoidComponent.BoneDef bone,
            HumanoidComponent.BoneDef? childBone,
            HumanoidComponent.BoneDef? parentBone,
            Vector3? preferredPitchAxisWorld,
            Vector3? preferredRollAxisWorld)
        {
            Vector3 dirWorld;
            string directionDescription;
            if (childBone?.Node is not null)
            {
                dirWorld = childBone.WorldBindPose.Translation - bone.WorldBindPose.Translation;
                directionDescription = "bone-to-child";
            }
            else if (parentBone?.Node is not null)
            {
                // Terminal semantic joints such as Head and Hand still need a
                // validated local basis. Their incoming chain direction is a
                // stable geometry signal even when they have no semantic child.
                dirWorld = bone.WorldBindPose.Translation - parentBone.WorldBindPose.Translation;
                directionDescription = "parent-to-terminal";
            }
            else
                return InheritOrDefault(parentBone, entries, authoredMappings, "no child or parent direction");

            if (dirWorld.LengthSquared() < 1e-8f)
            {
                return InheritOrDefault(parentBone, entries, authoredMappings, $"near-zero {directionDescription} distance");
            }

            dirWorld = Vector3.Normalize(dirWorld);

            if (!Matrix4x4.Invert(bone.WorldBindPose, out Matrix4x4 invBind))
            {
                return InheritOrDefault(parentBone, entries, authoredMappings, "bind matrix not invertible");
            }

            Vector3 dirLocal = Vector3.TransformNormal(dirWorld, invBind);
            float localLen = dirLocal.Length();
            if (localLen < 1e-8f)
            {
                return InheritOrDefault(parentBone, entries, authoredMappings, "degenerate local direction");
            }
            dirLocal /= localLen;

            if (preferredPitchAxisWorld.HasValue && preferredRollAxisWorld.HasValue)
                return DetectLimbAxisMapping(
                    entries,
                    authoredMappings,
                    bone,
                    parentBone,
                    dirLocal,
                    preferredPitchAxisWorld.Value,
                    preferredRollAxisWorld.Value);

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
            int frontBackSign = ResolveSwingAxisSign(parentBone, entries, authoredMappings, frontBackAxis);
            int leftRightSign = ResolveSwingAxisSign(parentBone, entries, authoredMappings, leftRightAxis);

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

        private static (BoneAxisMapping mapping, float confidence, string reason) DetectLimbAxisMapping(
            IReadOnlyDictionary<SceneNode, BoneProfileEntry> entries,
            IReadOnlyDictionary<string, BoneAxisMapping> authoredMappings,
            HumanoidComponent.BoneDef bone,
            HumanoidComponent.BoneDef? parentBone,
            Vector3 twistLocal,
            Vector3 preferredPitchAxisWorld,
            Vector3 preferredRollAxisWorld)
        {
            if (!Matrix4x4.Invert(bone.WorldBindPose, out Matrix4x4 invBind))
                return InheritOrDefault(parentBone, entries, authoredMappings, "bind matrix not invertible");

            Vector3 pitchLocal = Vector3.TransformNormal(preferredPitchAxisWorld, invBind);
            Vector3 rollLocal = Vector3.TransformNormal(preferredRollAxisWorld, invBind);

            float pitchLen = pitchLocal.Length();
            float rollLen = rollLocal.Length();
            if (pitchLen < 1e-8f || rollLen < 1e-8f)
                return InheritOrDefault(parentBone, entries, authoredMappings, "degenerate limb body-basis axis");

            pitchLocal /= pitchLen;
            rollLocal /= rollLen;

            int twistAxis = SelectDominantAxis(twistLocal);
            int twistSign = SignOrOne(GetAxisComponent(twistLocal, twistAxis));

            int frontBackAxis = SelectDominantAxis(pitchLocal, twistAxis);
            int frontBackSign = -SignOrOne(GetAxisComponent(pitchLocal, frontBackAxis));

            int leftRightAxis = SelectDominantAxis(rollLocal, twistAxis, frontBackAxis);
            int leftRightSign = SignOrOne(GetAxisComponent(rollLocal, leftRightAxis));

            float twistAlignment = MathF.Abs(GetAxisComponent(twistLocal, twistAxis));
            float frontBackAlignment = MathF.Abs(GetAxisComponent(pitchLocal, frontBackAxis));
            float leftRightAlignment = MathF.Abs(GetAxisComponent(rollLocal, leftRightAxis));
            float confidence = MathF.Min(twistAlignment, MathF.Min(frontBackAlignment, leftRightAlignment));

            var mapping = new BoneAxisMapping
            {
                TwistAxis = twistAxis,
                TwistSign = twistSign,
                FrontBackAxis = frontBackAxis,
                FrontBackSign = frontBackSign,
                LeftRightAxis = leftRightAxis,
                LeftRightSign = leftRightSign,
            };

            string reason = $"Body-basis limb detection (twist={twistAlignment:F3}, fb={frontBackAlignment:F3}, lr={leftRightAlignment:F3})";
            return (mapping, confidence, reason);
        }

        private static (BoneAxisMapping mapping, float confidence, string reason) InheritOrDefault(
            HumanoidComponent.BoneDef? parentBone,
            IReadOnlyDictionary<SceneNode, BoneProfileEntry> entries,
            IReadOnlyDictionary<string, BoneAxisMapping> authoredMappings,
            string detailReason)
        {
            if (parentBone?.Node is SceneNode parentNode
                && entries.TryGetValue(parentNode, out BoneProfileEntry parentEntry))
            {
                return (parentEntry.Mapping, 0.5f,
                    $"Inherited from parent '{parentNode.Name}' ({detailReason})");
            }

            if (parentBone?.Node?.Name is string parentName
                && authoredMappings.TryGetValue(parentName, out BoneAxisMapping parentMapping))
            {
                return (parentMapping, 0.5f,
                    $"Inherited from authored parent '{parentName}' ({detailReason})");
            }

            // Fall back to default
            return (BoneAxisMapping.Default, 0.0f,
                $"Default fallback ({detailReason})");
        }

        private static int SignOrOne(float value)
            => value < 0.0f ? -1 : 1;

        private static int SelectDominantAxis(Vector3 vector, params int[] excludedAxes)
        {
            float best = float.NegativeInfinity;
            int bestAxis = -1;
            for (int axis = 0; axis < 3; axis++)
            {
                if (excludedAxes.Contains(axis))
                    continue;

                float magnitude = MathF.Abs(GetAxisComponent(vector, axis));
                if (magnitude > best)
                {
                    best = magnitude;
                    bestAxis = axis;
                }
            }

            return bestAxis >= 0 ? bestAxis : 0;
        }

        private static float GetAxisComponent(Vector3 vector, int axis)
            => axis switch
            {
                0 => vector.X,
                1 => vector.Y,
                2 => vector.Z,
                _ => 0.0f,
            };

        private static void GetBindBodyBasis(HumanoidComponent component, out Vector3 bodyLeft, out Vector3 bodyUp, out Vector3 bodyForward)
        {
            Vector3 hipsPos = component.Hips.WorldBindPose.Translation;
            Vector3 spinePos = component.Spine.Node is not null
                ? component.Spine.WorldBindPose.Translation
                : hipsPos + Vector3.UnitY;
            bodyUp = NormalizeOrFallback(spinePos - hipsPos, Vector3.UnitY);

            Vector3 sideSum =
                GetBindSideDelta(component.Left.Shoulder, component.Right.Shoulder) +
                GetBindSideDelta(component.Left.Arm, component.Right.Arm) +
                GetBindSideDelta(component.Left.Wrist, component.Right.Wrist) +
                GetBindSideDelta(component.Left.Leg, component.Right.Leg) +
                GetBindSideDelta(component.Left.Foot, component.Right.Foot) +
                GetBindSideDelta(component.Left.Eye, component.Right.Eye);

            bodyLeft = NormalizeOrFallback(RejectAxis(sideSum, bodyUp), RejectAxis(Vector3.UnitX, bodyUp));
            bodyForward = NormalizeOrFallback(Vector3.Cross(bodyLeft, bodyUp), RejectAxis(Vector3.UnitZ, bodyUp));
            bodyLeft = NormalizeOrFallback(Vector3.Cross(bodyUp, bodyForward), bodyLeft);
        }

        private static Vector3 GetBindSideDelta(HumanoidComponent.BoneDef left, HumanoidComponent.BoneDef right)
        {
            if (left.Node is null || right.Node is null)
                return Vector3.Zero;

            return left.WorldBindPose.Translation - right.WorldBindPose.Translation;
        }

        private static Vector3 RejectAxis(Vector3 vector, Vector3 normal)
            => vector - Vector3.Dot(vector, normal) * normal;

        private static Vector3 NormalizeOrFallback(Vector3 vector, Vector3 fallback)
        {
            float lenSq = vector.LengthSquared();
            return lenSq > 1e-8f ? vector / MathF.Sqrt(lenSq) : fallback;
        }

        private static int ResolveSwingAxisSign(
            HumanoidComponent.BoneDef? parentBone,
            IReadOnlyDictionary<SceneNode, BoneProfileEntry> entries,
            IReadOnlyDictionary<string, BoneAxisMapping> authoredMappings,
            int axis)
        {
            if (parentBone?.Node is SceneNode parentNode
                && entries.TryGetValue(parentNode, out BoneProfileEntry parentEntry))
                return SignForAxis(parentEntry.Mapping, axis);

            if (parentBone?.Node?.Name is string parentName
                && authoredMappings.TryGetValue(parentName, out BoneAxisMapping parentMapping))
                return SignForAxis(parentMapping, axis);

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
