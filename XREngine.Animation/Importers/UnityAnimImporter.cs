using System.Globalization;
using System.Numerics;
using Unity;
using XREngine.Animation;
using XREngine.Animation.IK;
using XREngine.Components.Animation;
using YamlDotNet.RepresentationModel;

namespace XREngine.Animation.Importers
{
    public static class AnimYamlImporter
    {
        private const float TangentLinkTolerance = 0.0001f;

        public static bool Constrained = false;
        public static bool LerpConstrained = false;

        // ── Unity LH → Engine RH coordinate conversion ──────────────────
        // Unity uses left-handed Y-up (+Z forward); the engine uses right-handed
        // Y-up (-Z forward, OpenGL convention).
        //
        // Assimp's ZAxisRotation=180 applies a global root rotation to the skeleton
        // that makes the model face the camera (-Z). However, animation data from
        // .anim files remains in Unity's original coordinate space.
        //
        // The conversion is a Z-reflection (LH→RH) followed by a 180° Y rotation
        // (to match the Assimp-rotated skeleton's facing direction):
        //
        //   Z-reflection:  (x, y, z) → (x, y, -z)        positions
        //                  (x,y,z,w) → (-x,-y,z,w)        quaternions
        //
        //   180° Y rotation: (x, y, z) → (-x, y, -z)     positions
        //                    Ry*q*Ry⁻¹ conjugation        quaternions
        //
        //   Combined:  position  (x, y, z) → (-x, y, z)   [negate X]
        //              quaternion(x,y,z,w) → (x,-y,-z,w)   [negate Y and Z]

        private static Vector3 ConvertPosition(Vector3 v)
            => new(-v.X, v.Y, v.Z);

        /*

        /// <summary>
        /// Converts a quaternion from Unity's left-handed coordinate system
        /// to the engine's right-handed system with the Assimp root rotation accounted for.
        /// Combined Z-reflection + 180° Y rotation: (x,y,z,w) → (x,-y,-z,w).
        /// </summary>
        */
        private static Quaternion ConvertRotation(Quaternion q)
            => new(q.X, -q.Y, -q.Z, q.W);

        /*

        /// <summary>
        /// Converts an IK goal position from Unity humanoid avatar space to runtime body-local
        /// goal space used by <see cref="HumanoidIKSolverComponent"/>.
        ///
        /// IK goals are expressed relative to the hips, whose local frame was imported through
        /// Assimp with the same Z-reflect + 180° Y conversion as all bone channels. Therefore
        /// the full <see cref="ConvertPosition"/> transform is required — not just Z-reflection.
        /// </summary>

        /// <summary>
        /// Converts an IK goal rotation from Unity humanoid avatar space to runtime body-local
        /// goal space. Uses the full <see cref="ConvertRotation"/> transform (Z-reflect + 180° Y)
        /// to match the hips frame convention established during skeleton import.
        /// </summary>

        */
        private static float GetPositionComponentScale(char component)
            => component switch
            {
                'x' => 1.0f,
                'y' => 1.0f,
                'z' => 1.0f,
                _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported position component."),
            };

        private static float GetRotationComponentScale(char component)
            => component switch
            {
                'x' => 1.0f,
                'y' => 1.0f,
                'z' => 1.0f,
                'w' => 1.0f,
                _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported rotation component."),
            };

        private static char GetHumanoidBodyPositionTargetComponent(char component)
            => component switch
            {
                'x' => 'x',
                'y' => 'y',
                'z' => 'z',
                _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported humanoid position component."),
            };

        private static float GetHumanoidBodyPositionComponentScale(char component)
            => component switch
            {
                'x' => -1.0f,
                'y' => 1.0f,
                'z' => 1.0f,
                _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported humanoid position component."),
            };

        private static char GetHumanoidRootMotionPositionTargetComponent(char component)
            => component switch
            {
                'x' => 'x',
                'y' => 'z',
                'z' => 'y',
                _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported root-motion position component."),
            };

        private static float GetHumanoidRootMotionPositionComponentScale(char component)
            => component switch
            {
                'x' => -1.0f,
                'y' => 1.0f,
                'z' => 1.0f,
                _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported root-motion position component."),
            };

        private static float GetHumanoidBodyRotationComponentScale(char component)
            => component switch
            {
                'x' => 1.0f,
                'y' => -1.0f,
                'z' => -1.0f,
                'w' => 1.0f,
                _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported humanoid rotation component."),
            };

        /*
        /// <summary>
        /// Normalizes quaternion keys and enforces sign continuity (q and -q represent the
        /// same rotation). Keeping keys in the same hemisphere avoids apparent random flips
        /// when interpolating between adjacent keys.
        /// </summary>
        */

        // Unity humanoid IK goal curves (LeftFootT/Q, RightHandT/Q, etc.) are authored
        // in avatar/humanoid body-relative space. At runtime, UpdateAnimatedIKGoal transforms
        // them through the hips world matrix, which includes the full Assimp conversion
        // (Z-reflect + 180° Y). Therefore the same ConvertPosition/ConvertRotation is applied.
        // Runtime application is gated by HumanoidSettings.IKGoalPolicy — which
        // defaults to ApplyIfCalibrated (i.e. skipped until avatar calibration exists).
        private static bool ImportHumanoidIKGoalCurves => true;

        // RootT/RootQ in Unity humanoid clips represent the body center (hips) position
        // and orientation. Applied as bind-relative offsets on the Hips bone via
        // HumanoidComponent.SetRootPosition/SetRootRotation. This produces hip sway/bob
        // without overriding the model's scene-graph placement.
        private static bool ImportHumanoidRootMotionCurves => true;

        private sealed record ScalarCurve(
            string? Path,
            string Attribute,
            int? ClassId,
            IReadOnlyList<CurveKey> Keys,
            int PreInfinity,
            int PostInfinity);

        private sealed record VectorCurve(
            string? Path,
            string Attribute,
            int? ClassId,
            IReadOnlyDictionary<char, IReadOnlyList<CurveKey>> ComponentKeys,
            int PreInfinity,
            int PostInfinity);

        private sealed record CurveKey(float Time, float Value, float InSlope, float OutSlope, int CombinedTangentMode)
        {
            /// <summary>
            /// Gets the left (in) tangent mode from the tangentMode bitmask.
            /// </summary>
            public TangentMode LeftTangentMode => UnityAnimationClip.TangentModeHelper.GetLeftTangentMode(CombinedTangentMode);

            /// <summary>
            /// Gets the right (out) tangent mode from the tangentMode bitmask.
            /// </summary>
            public TangentMode RightTangentMode => UnityAnimationClip.TangentModeHelper.GetRightTangentMode(CombinedTangentMode);

            /// <summary>
            /// Gets whether the tangent is "broken" (left and right can be edited independently).
            /// </summary>
            public bool IsBroken => UnityAnimationClip.TangentModeHelper.IsBroken(CombinedTangentMode);

            /// <summary>
            /// Gets the interpolation type for the incoming (left) tangent.
            /// </summary>
            public EVectorInterpType InInterpType => ToInterpType(LeftTangentMode);

            /// <summary>
            /// Gets the interpolation type for the outgoing (right) tangent.
            /// </summary>
            public EVectorInterpType OutInterpType => ToInterpType(RightTangentMode);

            /// <summary>
            /// Converts a Unity TangentMode to an EVectorInterpType.
            /// </summary>
            private static EVectorInterpType ToInterpType(TangentMode mode)
                => mode switch
                {
                    TangentMode.Constant => EVectorInterpType.Step,
                    TangentMode.Linear => EVectorInterpType.Linear,
                    TangentMode.Free or TangentMode.Auto or TangentMode.ClampedAuto => EVectorInterpType.Hermite,
                    _ => EVectorInterpType.Hermite,
                };
        }

        public static AnimationClip Import(string filePath)
        {
            ArgumentNullException.ThrowIfNull(filePath);

            using var reader = new StreamReader(filePath);
            var yaml = new YamlStream();
            yaml.Load(reader);

            var clipMap = GetAnimationClipMapping(yaml);

            string name = GetScalarString(clipMap, "m_Name") ?? Path.GetFileNameWithoutExtension(filePath);
            int sampleRate = GetScalarInt(clipMap, "m_SampleRate") ?? 30;

            var settingsMap = GetMappingOrNull(clipMap, "m_AnimationClipSettings");
            float startTime = GetScalarFloatOrNull(settingsMap, "m_StartTime") ?? 0.0f;
            float stopTime = GetScalarFloatOrNull(settingsMap, "m_StopTime") ?? 0.0f;
            bool looped = (GetScalarIntOrNull(settingsMap, "m_LoopTime") ?? 0) != 0;

            var curves = new List<ScalarCurve>();
            var vecCurves = new List<VectorCurve>();

            // Some exporters duplicate data between m_FloatCurves and m_EditorCurves.
            // Prefer m_FloatCurves when present; fall back to m_EditorCurves.
            bool addedAny = false;
            addedAny |= TryReadCurveList(clipMap, "m_FloatCurves", curves, vecCurves);
            if (!addedAny)
                TryReadCurveList(clipMap, "m_EditorCurves", curves, vecCurves);

            // Also attempt to read other curve lists (some exporters store transform curves there).
            TryReadCurveList(clipMap, "m_PositionCurves", curves, vecCurves);
            TryReadCurveList(clipMap, "m_ScaleCurves", curves, vecCurves);
            TryReadCurveList(clipMap, "m_EulerCurves", curves, vecCurves);
            TryReadCurveList(clipMap, "m_RotationCurves", curves, vecCurves);

            float length = Math.Max(0.0f, stopTime - startTime);
            if (length <= 0.0f)
                length = GetMaxTime(curves, vecCurves);

            var clip = new AnimationClip
            {
                Name = name,
                LengthInSeconds = length,
                Looped = looped,
                SampleRate = sampleRate,
                RootMember = new AnimationMember("Root", EAnimationMemberType.Group),
            };

            // All animations are rooted at an AnimationClipComponent (XRComponent) instance.
            // We navigate from that root to SceneNode, then to descendants by name.
            var builder = new AnimMemberBuilder(clip.RootMember);

            // 1) Handle scalar curves (includes RootT.x/RootQ.w/etc and blendShape.*)
            var scalarByTarget = new Dictionary<(string nodePath, string attribute), ScalarCurve>();
            foreach (var c in curves)
            {
                string nodePath = NormalizePath(c.Path);
                scalarByTarget[(nodePath, c.Attribute)] = c;
            }

            // Group transform component curves into Translation/Rotation/Scale
            // (IK goal curves like LeftFootT/Q and root motion RootT/Q are handled separately below)
            var transformGroups = new Dictionary<(string nodePath, string kind), TransformCurveGroup>();
            var ikGoalGroups = new Dictionary<(string nodePath, string goalName, string kind), TransformCurveGroup>();
            var rootMotionGroups = new Dictionary<string, TransformCurveGroup>();
            foreach (var kvp in scalarByTarget)
            {
                string nodePath = kvp.Key.nodePath;
                string attr = kvp.Key.attribute;

                // Check for IK goal curves first (LeftFootT.x, RightHandQ.w, etc.)
                if (TryMapIKGoalComponent(attr, out string goalName, out string ikKind, out char ikComponent))
                {
                    var ikGroupKey = (nodePath, goalName, ikKind);
                    if (!ikGoalGroups.TryGetValue(ikGroupKey, out var ikGroup))
                    {
                        ikGroup = new TransformCurveGroup(ikKind);
                        ikGoalGroups[ikGroupKey] = ikGroup;
                    }
                    ikGroup.Components[ikComponent] = kvp.Value;
                    continue;
                }

                // Check for root motion curves (RootT.x, RootQ.w, etc.) — route to hips bind-relative.
                if (TryMapRootMotionComponent(attr, out string rootKind, out char rootComponent))
                {
                    if (!rootMotionGroups.TryGetValue(rootKind, out var rootGroup))
                    {
                        rootGroup = new TransformCurveGroup(rootKind);
                        rootMotionGroups[rootKind] = rootGroup;
                    }
                    rootGroup.Components[rootComponent] = kvp.Value;
                    continue;
                }

                if (!TryMapTransformComponent(attr, out string kind, out char component))
                    continue;

                var groupKey = (nodePath, kind);
                if (!transformGroups.TryGetValue(groupKey, out var group))
                {
                    group = new TransformCurveGroup(kind);
                    transformGroups[groupKey] = group;
                }
                group.Components[component] = kvp.Value;
            }

            // Build transform animations.
            foreach (var kv in transformGroups)
            {
                string nodePath = kv.Key.nodePath;
                var group = kv.Value;

                if (group.Kind == "translation")
                {
                    foreach (var component in group.Components.OrderBy(x => x.Key))
                    {
                        var anim = BuildFloatAnim(component.Value, length, looped, sampleRate, GetPositionComponentScale(component.Key), startTime);
                        builder.AddTransformComponentAnimation(nodePath, group.Kind, component.Key, anim);
                    }
                }
                else if (group.Kind == "scale")
                {
                    foreach (var component in group.Components.OrderBy(x => x.Key))
                    {
                        var anim = BuildFloatAnim(component.Value, length, looped, sampleRate, 1.0f, startTime);
                        builder.AddTransformComponentAnimation(nodePath, group.Kind, component.Key, anim);
                    }
                }
                else if (group.Kind == "rotation")
                {
                    foreach (var component in group.Components.OrderBy(x => x.Key))
                    {
                        var anim = BuildFloatAnim(component.Value, length, looped, sampleRate, GetRotationComponentScale(component.Key), startTime);
                        builder.AddTransformComponentAnimation(nodePath, group.Kind, component.Key, anim);
                    }
                }
            }

            // Build root motion animation (RootT/RootQ → hips bind-relative offsets).
            if (ImportHumanoidRootMotionCurves)
            {
                if (rootMotionGroups.TryGetValue("translation", out var rootPosGroup))
                {
                    foreach (var component in rootPosGroup.Components.OrderBy(x => x.Key))
                    {
                        char targetComponent = GetHumanoidRootMotionPositionTargetComponent(component.Key);
                        var anim = BuildFloatAnim(component.Value, length, looped, sampleRate, GetHumanoidRootMotionPositionComponentScale(component.Key), startTime);
                        builder.AddRootMotionComponentAnimation(targetComponent, anim);
                    }
                }

                if (rootMotionGroups.TryGetValue("rotation", out var rootRotGroup))
                {
                    foreach (var component in rootRotGroup.Components.OrderBy(x => x.Key))
                    {
                        var anim = BuildFloatAnim(component.Value, length, looped, sampleRate, GetHumanoidBodyRotationComponentScale(component.Key), startTime);
                        builder.AddRootMotionRotationComponentAnimation(component.Key, anim);
                    }
                }
            }

            if (ImportHumanoidIKGoalCurves)
            {
                // Build IK goal animations (LeftFootT/Q, RightFootT/Q, LeftHandT/Q, RightHandT/Q).
                // Route each component independently so Unity scalar tangents stay intact.
                var ikGoalsByName = new Dictionary<(string nodePath, string goalName), (TransformCurveGroup? pos, TransformCurveGroup? rot)>();
                foreach (var kv in ikGoalGroups)
                {
                    var key = (kv.Key.nodePath, kv.Key.goalName);
                    if (!ikGoalsByName.TryGetValue(key, out var pair))
                        pair = (null, null);

                    if (kv.Value.Kind == "translation")
                        pair = (kv.Value, pair.rot);
                    else if (kv.Value.Kind == "rotation")
                        pair = (pair.pos, kv.Value);

                    ikGoalsByName[key] = pair;
                }

                foreach (var kv in ikGoalsByName)
                {
                    // Unity humanoid .anim files author IK goals with left/right swapped
                    // relative to the engine's skeleton convention. Flip all limb chains
                    // at import time so left curves drive right end effectors and vice versa.
                    string goalName = kv.Key.goalName switch
                    {
                        //"LeftHand" => "RightHand",
                        //"RightHand" => "LeftHand",
                        //"LeftFoot" => "RightFoot",
                        //"RightFoot" => "LeftFoot",
                        _ => kv.Key.goalName,
                    };
                    var (posGroup, rotGroup) = kv.Value;

                    if (posGroup is not null)
                    {
                        foreach (var component in posGroup.Components.OrderBy(x => x.Key))
                        {
                            char targetComponent = GetHumanoidBodyPositionTargetComponent(component.Key);
                            var anim = BuildFloatAnim(component.Value, length, looped, sampleRate, GetHumanoidBodyPositionComponentScale(component.Key), startTime);
                            builder.AddIKGoalPositionComponentAnimation(goalName, targetComponent, anim);
                        }
                    }

                    if (rotGroup is not null)
                    {
                        foreach (var component in rotGroup.Components.OrderBy(x => x.Key))
                        {
                            var anim = BuildFloatAnim(component.Value, length, looped, sampleRate, GetHumanoidBodyRotationComponentScale(component.Key), startTime);
                            builder.AddIKGoalRotationComponentAnimation(goalName, component.Key, anim);
                        }
                    }
                }
            }

            // Track humanoid muscle curve count for clip classification.
            int humanoidMuscleCount = 0;

            // Build blendshape animations and remaining scalar animations.
            foreach (var c in curves)
            {
                // Skip ones consumed by transform grouping.
                if (TryMapTransformComponent(c.Attribute, out _, out _))
                    continue;

                // Skip ones consumed by IK goal grouping.
                if (TryMapIKGoalComponent(c.Attribute, out _, out _, out _))
                    continue;

                // Skip ones consumed by root motion grouping.
                if (TryMapRootMotionComponent(c.Attribute, out _, out _))
                    continue;

                string nodePath = NormalizePath(c.Path);

                // Humanoid (muscle) curves: these typically have an empty path and classID 95,
                // and the attribute is a human-readable muscle name like "Neck Nod Down-Up".
                // We map these strings to the underlying int value of EHumanoidValue and forward to HumanoidComponent.SetValue(int, float).
                if (IsHumanoidMuscleCurve(c))
                {
                    if (!TryMapUnityHumanoidAttributeToValue(c.Attribute, out EHumanoidValue humanoidValue))
                        continue;

                    var anim = BuildFloatAnim(c, length, looped, sampleRate, valueScale: 1.0f, startTime);
                    builder.AddHumanoidValueAnimation(humanoidValue, anim);
                    humanoidMuscleCount++;
                    continue;
                }

                if (c.Attribute.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    string blendshapeName = c.Attribute["blendShape.".Length..];
                    // Blendshape weights are typically 0..100; engine normalized is 0..1.
                    var anim = BuildFloatAnim(c, length, looped, sampleRate, valueScale: 1.0f / 100.0f, startTime);
                    builder.AddBlendshapeAnimation(nodePath, blendshapeName, anim);
                    continue;
                }

                // Best-effort generic scalar property: attempt to animate a property on the node's Transform.
                // If attribute matches a known Transform property, map it; otherwise store it as a property name.
                if (TryMapScalarTransformProperty(c.Attribute, out string transformPropertyName))
                {
                    var anim = BuildFloatAnim(c, length, looped, sampleRate, valueScale: 1.0f, startTime);
                    builder.AddTransformScalarPropertyAnimation(nodePath, transformPropertyName, anim);
                }
            }

            // 2) Handle explicit vector curves (if any were present in the YAML)
            foreach (var vc in vecCurves)
            {
                string nodePath = NormalizePath(vc.Path);
                if (TryMapVectorAttribute(vc.Attribute, out string kind, out int componentCount))
                {
                    if (kind is "translation" or "scale")
                    {
                        foreach (var component in vc.ComponentKeys.OrderBy(x => x.Key))
                        {
                            if (component.Key is not ('x' or 'y' or 'z'))
                                continue;

                            float valueScale = kind == "translation"
                                ? GetPositionComponentScale(component.Key)
                                : 1.0f;
                            var anim = BuildFloatAnim(
                                new ScalarCurve(vc.Path, $"{vc.Attribute}.{component.Key}", vc.ClassId, component.Value, vc.PreInfinity, vc.PostInfinity),
                                length,
                                looped,
                                sampleRate,
                                valueScale,
                                startTime);
                            builder.AddTransformComponentAnimation(nodePath, kind, component.Key, anim);
                        }
                    }
                    else if (kind == "rotation" && componentCount == 4)
                    {
                        foreach (var component in vc.ComponentKeys.OrderBy(x => x.Key))
                        {
                            if (component.Key is not ('x' or 'y' or 'z' or 'w'))
                                continue;

                            var anim = BuildFloatAnim(
                                new ScalarCurve(vc.Path, $"{vc.Attribute}.{component.Key}", vc.ClassId, component.Value, vc.PreInfinity, vc.PostInfinity),
                                length,
                                looped,
                                sampleRate,
                                GetRotationComponentScale(component.Key),
                                startTime);
                            builder.AddTransformComponentAnimation(nodePath, kind, component.Key, anim);
                        }
                    }
                }
            }

            // ── Clip classification ──────────────────────────────────────────
            clip.HasMuscleChannels = humanoidMuscleCount > 0;
            clip.HasRootMotion = rootMotionGroups.Count > 0;
            clip.HasIKGoals = ikGoalGroups.Count > 0;
            clip.ClipKind = humanoidMuscleCount > 0
                ? EAnimationClipKind.UnityHumanoidMuscle
                : EAnimationClipKind.GenericTransform;

            return clip;
        }

        private sealed class TransformCurveGroup(string kind)
        {
            public string Kind { get; } = kind;
            public Dictionary<char, ScalarCurve> Components { get; } = new();
        }

        private sealed class AnimMemberBuilder
        {
            private readonly AnimationMember _root;
            private readonly AnimationMember _sceneNode;
            private readonly Dictionary<string, AnimationMember> _nodeCache = new(StringComparer.Ordinal);

            public AnimMemberBuilder(AnimationMember root)
            {
                _root = root;
                _sceneNode = GetOrAddChild(_root, "SceneNode", EAnimationMemberType.Property);
            }

            public void AddTransformPropertyAnimation(string nodePath, string propertyName, BasePropAnim anim)
            {
                var node = GetSceneNodeByPath(nodePath);
                var transform = GetOrAddChild(node, "Transform", EAnimationMemberType.Property);
                var prop = GetOrAddChild(transform, propertyName, EAnimationMemberType.Property);
                prop.Animation = anim;
            }

            public void AddTransformScalarPropertyAnimation(string nodePath, string propertyName, BasePropAnim anim)
            {
                var node = GetSceneNodeByPath(nodePath);
                var transform = GetOrAddChild(node, "Transform", EAnimationMemberType.Property);
                var prop = GetOrAddChild(transform, propertyName, EAnimationMemberType.Property);
                prop.Animation = anim;
            }

            public void AddTransformComponentAnimation(string nodePath, string kind, char component, PropAnimFloat anim)
            {
                string propertyName = kind switch
                {
                    "translation" => component switch
                    {
                        'x' => "TranslationX",
                        'y' => "TranslationY",
                        'z' => "TranslationZ",
                        _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported translation component."),
                    },
                    "scale" => component switch
                    {
                        'x' => "ScaleX",
                        'y' => "ScaleY",
                        'z' => "ScaleZ",
                        _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported scale component."),
                    },
                    "rotation" => component switch
                    {
                        'x' => "QuaternionX",
                        'y' => "QuaternionY",
                        'z' => "QuaternionZ",
                        'w' => "QuaternionW",
                        _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported rotation component."),
                    },
                    _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported transform animation kind."),
                };

                AddTransformScalarPropertyAnimation(nodePath, propertyName, anim);
            }

            public void AddBlendshapeAnimation(string nodePath, string blendshapeName, PropAnimFloat anim)
            {
                var node = GetSceneNodeByPath(nodePath);
                var getComp = GetOrAddMethod(node, "GetComponent", ["ModelComponent"], animatedArgIndex: -1, cacheReturnValue: true);
                var method = GetOrAddMethod(getComp, "SetBlendShapeWeightNormalized", [blendshapeName, 0.0f, StringComparison.InvariantCultureIgnoreCase], animatedArgIndex: 1, cacheReturnValue: false);
                method.Animation = anim;
            }

            public void AddHumanoidValueAnimation(EHumanoidValue humanoidValue, PropAnimFloat anim)
            {
                // Humanoid values are applied on the root node's HumanoidComponent.
                // We keep this importer decoupled from the HumanoidComponent assembly by using string-based reflection.
                var getComp = GetOrAddMethod(_sceneNode, "GetComponentInHierarchy", ["HumanoidComponent"], animatedArgIndex: -1, cacheReturnValue: true);
                var method = GetOrAddMethod(getComp, "SetImportedRawValue", [humanoidValue, 0.0f, false], animatedArgIndex: 1, cacheReturnValue: false);
                method.Animation = anim;
            }

            public void AddIKGoalPositionComponentAnimation(string goalName, char component, PropAnimFloat anim)
            {
                var getComp = GetOrAddMethod(_sceneNode, "GetComponentInHierarchy", ["HumanoidIKSolverComponent"], animatedArgIndex: -1, cacheReturnValue: true);
                if (!TryMapIKGoal(goalName, out ELimbEndEffector goal))
                    return;

                string methodName = component switch
                {
                    'x' => "SetAnimatedIKPositionX",
                    'y' => "SetAnimatedIKPositionY",
                    'z' => "SetAnimatedIKPositionZ",
                    _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported IK position component."),
                };

                var method = GetOrAddMethod(getComp, methodName, [goal, 0.0f], animatedArgIndex: 1, cacheReturnValue: false);
                method.Animation = anim;
            }

            public void AddIKGoalRotationComponentAnimation(string goalName, char component, PropAnimFloat anim)
            {
                var getComp = GetOrAddMethod(_sceneNode, "GetComponentInHierarchy", ["HumanoidIKSolverComponent"], animatedArgIndex: -1, cacheReturnValue: true);
                if (!TryMapIKGoal(goalName, out ELimbEndEffector goal))
                    return;

                string methodName = component switch
                {
                    'x' => "SetAnimatedIKRotationX",
                    'y' => "SetAnimatedIKRotationY",
                    'z' => "SetAnimatedIKRotationZ",
                    'w' => "SetAnimatedIKRotationW",
                    _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported IK rotation component."),
                };

                var method = GetOrAddMethod(getComp, methodName, [goal, 0.0f], animatedArgIndex: 1, cacheReturnValue: false);
                method.Animation = anim;
            }

            public void AddRootMotionComponentAnimation(char component, PropAnimFloat anim)
            {
                var getComp = GetOrAddMethod(_sceneNode, "GetComponentInHierarchy", ["HumanoidComponent"], animatedArgIndex: -1, cacheReturnValue: true);
                string methodName = component switch
                {
                    'x' => "SetRootPositionX",
                    'y' => "SetRootPositionY",
                    'z' => "SetRootPositionZ",
                    _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported root-motion position component."),
                };

                var method = GetOrAddMethod(getComp, methodName, [0.0f], animatedArgIndex: 0, cacheReturnValue: false);
                method.Animation = anim;
            }

            public void AddRootMotionRotationComponentAnimation(char component, PropAnimFloat anim)
            {
                var getComp = GetOrAddMethod(_sceneNode, "GetComponentInHierarchy", ["HumanoidComponent"], animatedArgIndex: -1, cacheReturnValue: true);
                string methodName = component switch
                {
                    'x' => "SetRootRotationX",
                    'y' => "SetRootRotationY",
                    'z' => "SetRootRotationZ",
                    'w' => "SetRootRotationW",
                    _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unsupported root-motion rotation component."),
                };

                var method = GetOrAddMethod(getComp, methodName, [0.0f], animatedArgIndex: 0, cacheReturnValue: false);
                method.Animation = anim;
            }

            private AnimationMember GetSceneNodeByPath(string nodePath)
            {
                // nodePath is normalized "A/B/C" or "".
                if (_nodeCache.TryGetValue(nodePath, out var cached))
                    return cached;

                AnimationMember current = _sceneNode;
                if (!string.IsNullOrWhiteSpace(nodePath))
                {
                    foreach (string seg in nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        current = GetOrAddMethod(current, "FindDescendantByName", [seg, StringComparison.InvariantCultureIgnoreCase], animatedArgIndex: -1, cacheReturnValue: true);
                    }
                }

                _nodeCache[nodePath] = current;
                return current;
            }

            private static AnimationMember GetOrAddChild(AnimationMember parent, string memberName, EAnimationMemberType memberType)
            {
                foreach (var child in parent.Children)
                {
                    if (child.MemberName == memberName && child.MemberType == memberType)
                        return child;
                }
                var created = new AnimationMember(memberName, memberType);
                parent.Children.Add(created);
                return created;
            }

            private static AnimationMember GetOrAddMethod(AnimationMember parent, string methodName, object?[] methodArgs, int animatedArgIndex, bool cacheReturnValue)
            {
                foreach (var child in parent.Children)
                {
                    if (child.MemberName != methodName || 
                        child.MemberType != EAnimationMemberType.Method)
                        continue;
                    
                    if (child.AnimatedMethodArgumentIndex != animatedArgIndex)
                        continue;

                    if (child.MethodArguments.Length != methodArgs.Length)
                        continue;
                    
                    bool equal = true;
                    for (int i = 0; i < methodArgs.Length; i++)
                    {
                        if (!Equals(child.MethodArguments[i], methodArgs[i]))
                        {
                            equal = false;
                            break;
                        }
                    }
                    if (equal)
                        return child;
                }

                var created = new AnimationMember(methodName, EAnimationMemberType.Method)
                {
                    MethodArguments = methodArgs,
                    AnimatedMethodArgumentIndex = animatedArgIndex,
                    CacheReturnValue = cacheReturnValue,
                };
                parent.Children.Add(created);
                return created;
            }

            private static bool TryMapIKGoal(string goalName, out ELimbEndEffector goal)
            {
                switch (goalName)
                {
                    case "LeftFoot": goal = ELimbEndEffector.LeftFoot; return true;
                    case "RightFoot": goal = ELimbEndEffector.RightFoot; return true;
                    case "LeftHand": goal = ELimbEndEffector.LeftHand; return true;
                    case "RightHand": goal = ELimbEndEffector.RightHand; return true;
                    default:
                        goal = default;
                        return false;
                }
            }
        }

        private static bool TryMapUnityHumanoidAttributeToValue(string attribute, out EHumanoidValue humanoidValue)
        {
            // Ignore blendshape + numeric editor-curve attributes.
            if (attribute.StartsWith("blendShape.", StringComparison.Ordinal))
            {
                humanoidValue = default;
                return false;
            }
            if (attribute.Length > 0 && char.IsDigit(attribute[0]))
            {
                humanoidValue = default;
                return false;
            }

            return UnityHumanoidMuscleMap.TryGetValue(attribute, out humanoidValue);
        }

        private static bool IsHumanoidMuscleCurve(ScalarCurve c)
        {
            // Humanoid muscle curves commonly have an empty binding path and classID 95.
            if (!string.IsNullOrWhiteSpace(c.Path))
                return false;

            if (c.ClassId is not 95)
                return false;

            // Avoid treating blendShape.* or RootT/RootQ or IK goals as humanoid.
            if (c.Attribute.StartsWith("blendShape.", StringComparison.Ordinal))
                return false;

            if (c.Attribute.StartsWith("RootT.", StringComparison.Ordinal) || c.Attribute.StartsWith("RootQ.", StringComparison.Ordinal))
                return false;

            // IK goal curves (LeftFootT, RightFootQ, LeftHandT, etc.) are handled separately.
            if (TryMapIKGoalComponent(c.Attribute, out _, out _, out _))
                return false;

            // Use explicit mapping instead of a name heuristic so dot-only muscle names (e.g. LeftHand.Index.Spread)
            // are recognized correctly.
            return TryMapUnityHumanoidAttributeToValue(c.Attribute, out _);
        }

        private static bool TryReadCurveList(
            YamlMappingNode clipMap,
            string key,
            List<ScalarCurve> scalarCurves,
            List<VectorCurve> vectorCurves)
        {
            var seq = GetSequenceOrNull(clipMap, key);
            if (seq is null || seq.Children.Count == 0)
                return false;

            bool addedAny = false;
            foreach (var itemNode in seq.Children)
            {
                if (itemNode is not YamlMappingNode item)
                    continue;

                string? path = GetScalarString(item, "path");
                string? attribute = GetScalarString(item, "attribute");
                int? classId = GetScalarInt(item, "classID");

                // Case 1: Float curve item (attribute + curve.m_Curve)
                if (!string.IsNullOrEmpty(attribute))
                {
                    if (TryParseCurveData(item, out var keys, out int scalarPreInfinity, out int scalarPostInfinity))
                    {
                        scalarCurves.Add(new ScalarCurve(path, attribute!, classId, keys, scalarPreInfinity, scalarPostInfinity));
                        addedAny = true;
                        continue;
                    }
                }

                // Case 2: Vector/quaternion curve item (curve has x/y/z(/w) each containing curve data)
                // These are not present in your current samples, but this keeps the importer usable for more exporter variants.
                if (TryParseVectorCurveData(item, out var vecAttribute, out var components, out int vectorPreInfinity, out int vectorPostInfinity))
                {
                    vectorCurves.Add(new VectorCurve(path, vecAttribute, classId, components, vectorPreInfinity, vectorPostInfinity));
                    addedAny = true;
                }
            }

            return addedAny;
        }

        private static bool TryParseCurveData(
            YamlMappingNode item,
            out IReadOnlyList<CurveKey> keys,
            out int preInfinity,
            out int postInfinity)
        {
            keys = Array.Empty<CurveKey>();
            preInfinity = 0;
            postInfinity = 0;

            if (!TryGetMapping(item, "curve", out var curveMap))
                return false;

            if (!TryGetSequence(curveMap, "m_Curve", out var keySeq))
                return false;

            preInfinity = GetScalarInt(curveMap, "m_PreInfinity") ?? 0;
            postInfinity = GetScalarInt(curveMap, "m_PostInfinity") ?? 0;

            var list = new List<CurveKey>(keySeq.Children.Count);
            foreach (var k in keySeq.Children)
            {
                if (k is not YamlMappingNode km)
                    continue;
                float time = GetScalarFloat(km, "time") ?? 0.0f;
                float value = GetScalarFloat(km, "value") ?? 0.0f;
                float inSlope = GetScalarFloat(km, "inSlope") ?? 0.0f;
                float outSlope = GetScalarFloat(km, "outSlope") ?? 0.0f;
                int tangentMode = GetScalarInt(km, "tangentMode") ?? 0;
                list.Add(new CurveKey(time, value, inSlope, outSlope, tangentMode));
            }

            keys = list;
            return true;
        }

        private static bool TryParseVectorCurveData(
            YamlMappingNode item,
            out string attribute,
            out IReadOnlyDictionary<char, IReadOnlyList<CurveKey>> componentKeys,
            out int preInfinity,
            out int postInfinity)
        {
            attribute = string.Empty;
            componentKeys = new Dictionary<char, IReadOnlyList<CurveKey>>();
            preInfinity = 0;
            postInfinity = 0;

            if (!TryGetMapping(item, "curve", out var curveMap))
                return false;

            // Some exporters store "attribute" at the item level even for vector curves; if not, we can't map.
            attribute = GetScalarString(item, "attribute") ?? string.Empty;
            if (string.IsNullOrEmpty(attribute))
                return false;

            preInfinity = GetScalarInt(curveMap, "m_PreInfinity") ?? 0;
            postInfinity = GetScalarInt(curveMap, "m_PostInfinity") ?? 0;

            var comps = new Dictionary<char, IReadOnlyList<CurveKey>>();
            foreach (char c in new[] { 'x', 'y', 'z', 'w' })
            {
                if (!TryGetMapping(curveMap, c.ToString(), out var compMap))
                    continue;
                if (!TryGetSequence(compMap, "m_Curve", out var keySeq))
                    continue;

                var list = new List<CurveKey>(keySeq.Children.Count);
                foreach (var k in keySeq.Children)
                {
                    if (k is not YamlMappingNode km)
                        continue;
                    float time = GetScalarFloat(km, "time") ?? 0.0f;
                    float value = GetScalarFloat(km, "value") ?? 0.0f;
                    float inSlope = GetScalarFloat(km, "inSlope") ?? 0.0f;
                    float outSlope = GetScalarFloat(km, "outSlope") ?? 0.0f;
                    int tangentMode = GetScalarInt(km, "tangentMode") ?? 0;
                    list.Add(new CurveKey(time, value, inSlope, outSlope, tangentMode));
                }

                comps[c] = list;
            }

            if (comps.Count == 0)
                return false;

            componentKeys = comps;
            return true;
        }

        private static PropAnimFloat BuildFloatAnim(ScalarCurve curve, float length, bool looped, int fps, float valueScale, float timeOffsetSeconds)
        {
            var anim = new PropAnimFloat
            {
                LengthInSeconds = length,
                Looped = looped,
                BakedFramesPerSecond = fps,
                ConstrainKeyframedFPS = Constrained,
                LerpConstrainedFPS = LerpConstrained,
            };

            if (TryCreateAuthoredCadence(length, fps, out var authoredCadence))
                anim.SetAuthoredCadence(authoredCadence, notifyChanged: false);

            anim.Keyframes.PreInfinityMode = MapInfinityMode(curve.PreInfinity);
            anim.Keyframes.PostInfinityMode = MapInfinityMode(curve.PostInfinity);

            foreach (var k in curve.Keys)
                anim.Keyframes.Add(CreateFloatKeyframe(k, fps, valueScale, timeOffsetSeconds));

            return anim;
        }

        private static FloatKeyframe CreateFloatKeyframe(CurveKey key, int fps, float valueScale, float timeOffsetSeconds)
        {
            float normalizedTime = MathF.Max(0.0f, key.Time - timeOffsetSeconds);
            var kf = new FloatKeyframe
            {
                SyncInOutValues = false,
                SyncInOutTangentDirections = false,
                SyncInOutTangentMagnitudes = false,
                Second = normalizedTime,
                InterpolationTypeIn = key.InInterpType,
                InterpolationTypeOut = key.OutInterpType,
            };

            if (TryGetAuthoredFrameIndex(normalizedTime, fps, out int authoredFrameIndex))
                kf.AuthoredFrameIndex = authoredFrameIndex;

            kf.InValue = key.Value * valueScale;
            kf.OutValue = key.Value * valueScale;
            kf.InTangent = ConvertIncomingTangent(key.InSlope, valueScale);
            kf.OutTangent = ConvertOutgoingTangent(key.OutSlope, valueScale);

            if (!key.IsBroken && CanLinkTangents(kf.InTangent, kf.OutTangent))
            {
                kf.SyncInOutTangentDirections = true;
                kf.SyncInOutTangentMagnitudes = true;
            }

            return kf;
        }

        private static bool TryCreateAuthoredCadence(float lengthSeconds, int fps, out AuthoredCadence cadence)
        {
            cadence = default;
            if (fps <= 0 || !float.IsFinite(lengthSeconds) || lengthSeconds <= 0.0f)
                return false;

            float authoredFrames = lengthSeconds * fps;
            int roundedFrameCount = (int)MathF.Round(authoredFrames);
            if (roundedFrameCount <= 0)
                return false;

            float normalizedLength = roundedFrameCount / (float)fps;
            if (MathF.Abs(normalizedLength - lengthSeconds) > 0.0001f)
                return false;

            cadence = new AuthoredCadence(roundedFrameCount, fps);
            return true;
        }

        private static bool TryGetAuthoredFrameIndex(float timeSeconds, int fps, out int frameIndex)
        {
            frameIndex = 0;
            if (fps <= 0 || !float.IsFinite(timeSeconds) || timeSeconds < 0.0f)
                return false;

            float authoredFrame = timeSeconds * fps;
            float roundedFrame = MathF.Round(authoredFrame);
            if (MathF.Abs(authoredFrame - roundedFrame) > 0.0001f)
                return false;

            frameIndex = Math.Max(0, (int)roundedFrame);
            return true;
        }

        private static float ConvertIncomingTangent(float slope, float valueScale)
            => -(slope * valueScale);

        private static float ConvertOutgoingTangent(float slope, float valueScale)
            => slope * valueScale;

        private static bool CanLinkTangents(float inTangent, float outTangent)
            => MathF.Abs(inTangent + outTangent) <= TangentLinkTolerance;

        private static EKeyframeInfinityMode MapInfinityMode(int unityInfinity)
            => unityInfinity == 2
                ? EKeyframeInfinityMode.Loop
                : EKeyframeInfinityMode.Clamp;

        private static float GetMaxTime(List<ScalarCurve> scalarCurves, List<VectorCurve> vectorCurves)
        {
            float max = 0.0f;
            foreach (var c in scalarCurves)
                foreach (var k in c.Keys)
                    max = Math.Max(max, k.Time);

            foreach (var vc in vectorCurves)
                foreach (var ks in vc.ComponentKeys.Values)
                    foreach (var k in ks)
                        max = Math.Max(max, k.Time);

            return max;
        }

        private static bool TryMapTransformComponent(string attribute, out string kind, out char component)
        {
            kind = string.Empty;
            component = '\0';

            // NOTE: RootT/RootQ are intentionally NOT handled here.
            // They represent humanoid body-center (hips) motion, not a scene-node transform override.
            // They are handled separately via TryMapRootMotionComponent and the root-motion component setters.

            // Transform curves from other exporters
            if (TrySplitComponent(attribute, "m_LocalPosition", out component) || TrySplitComponent(attribute, "localPosition", out component))
            {
                kind = "translation";
                return component is 'x' or 'y' or 'z';
            }
            if (TrySplitComponent(attribute, "m_LocalScale", out component) || TrySplitComponent(attribute, "localScale", out component))
            {
                kind = "scale";
                return component is 'x' or 'y' or 'z';
            }
            if (TrySplitComponent(attribute, "m_LocalRotation", out component) || TrySplitComponent(attribute, "localRotation", out component))
            {
                kind = "rotation";
                return component is 'x' or 'y' or 'z' or 'w';
            }

            return false;
        }

        /// <summary>
        /// Maps IK goal curve attributes like "LeftFootT.x", "RightHandQ.w" to a goal name, kind, and component.
        /// </summary>
        private static bool TryMapIKGoalComponent(string attribute, out string goalName, out string kind, out char component)
        {
            goalName = string.Empty;
            kind = string.Empty;
            component = '\0';

            // IK goal position curves: LeftFootT, RightFootT, LeftHandT, RightHandT
            if (TrySplitComponent(attribute, "LeftFootT", out component)) { goalName = "LeftFoot"; kind = "translation"; return component is 'x' or 'y' or 'z'; }
            if (TrySplitComponent(attribute, "RightFootT", out component)) { goalName = "RightFoot"; kind = "translation"; return component is 'x' or 'y' or 'z'; }
            if (TrySplitComponent(attribute, "LeftHandT", out component)) { goalName = "LeftHand"; kind = "translation"; return component is 'x' or 'y' or 'z'; }
            if (TrySplitComponent(attribute, "RightHandT", out component)) { goalName = "RightHand"; kind = "translation"; return component is 'x' or 'y' or 'z'; }

            // IK goal rotation curves: LeftFootQ, RightFootQ, LeftHandQ, RightHandQ
            if (TrySplitComponent(attribute, "LeftFootQ", out component)) { goalName = "LeftFoot"; kind = "rotation"; return component is 'x' or 'y' or 'z' or 'w'; }
            if (TrySplitComponent(attribute, "RightFootQ", out component)) { goalName = "RightFoot"; kind = "rotation"; return component is 'x' or 'y' or 'z' or 'w'; }
            if (TrySplitComponent(attribute, "LeftHandQ", out component)) { goalName = "LeftHand"; kind = "rotation"; return component is 'x' or 'y' or 'z' or 'w'; }
            if (TrySplitComponent(attribute, "RightHandQ", out component)) { goalName = "RightHand"; kind = "rotation"; return component is 'x' or 'y' or 'z' or 'w'; }

            return false;
        }

        /// <summary>
        /// Maps root motion attributes (RootT.x, RootQ.w, etc.) to kind + component.
        /// </summary>
        private static bool TryMapRootMotionComponent(string attribute, out string kind, out char component)
        {
            kind = string.Empty;
            component = '\0';

            if (TrySplitComponent(attribute, "RootT", out component))
            {
                kind = "translation";
                return component is 'x' or 'y' or 'z';
            }
            if (TrySplitComponent(attribute, "RootQ", out component))
            {
                kind = "rotation";
                return component is 'x' or 'y' or 'z' or 'w';
            }

            return false;
        }

        private static bool TryMapVectorAttribute(string attribute, out string kind, out int componentCount)
        {
            kind = string.Empty;
            componentCount = 0;

            // NOTE: RootT/RootQ are intentionally excluded — handled as root motion via HumanoidComponent.
            if (attribute.Equals("m_LocalPosition", StringComparison.Ordinal) || attribute.Equals("localPosition", StringComparison.Ordinal))
            {
                kind = "translation";
                componentCount = 3;
                return true;
            }
            if (attribute.Equals("m_LocalScale", StringComparison.Ordinal) || attribute.Equals("localScale", StringComparison.Ordinal))
            {
                kind = "scale";
                componentCount = 3;
                return true;
            }
            if (attribute.Equals("m_LocalRotation", StringComparison.Ordinal) || attribute.Equals("localRotation", StringComparison.Ordinal))
            {
                kind = "rotation";
                componentCount = 4;
                return true;
            }

            return false;
        }

        private static bool TryMapScalarTransformProperty(string attribute, out string propertyName)
        {
            // For uncommon scalar properties (e.g. translation smoothing) - best effort.
            propertyName = attribute;
            return false;
        }

        private static bool TrySplitComponent(string attribute, string prefix, out char component)
        {
            component = '\0';
            if (!attribute.StartsWith(prefix + ".", StringComparison.Ordinal))
                return false;
            if (attribute.Length != prefix.Length + 2)
                return false;
            component = attribute[^1];
            return true;
        }

        private static string NormalizePath(string? path)
            => string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/');

        private static YamlMappingNode GetAnimationClipMapping(YamlStream yaml)
        {
            if (yaml.Documents.Count == 0)
                throw new InvalidDataException("No YAML documents found.");

            if (yaml.Documents[0].RootNode is not YamlMappingNode root)
                throw new InvalidDataException("Unexpected YAML root node.");

            // File root is typically a mapping with a single key "AnimationClip".
            if (!TryGetMapping(root, "AnimationClip", out var clipMap))
            {
                // Some YAML streams use an anchor key; attempt to find any mapping value that contains m_Name.
                foreach (var kv in root.Children)
                {
                    if (kv.Value is YamlMappingNode m && m.Children.Keys.OfType<YamlScalarNode>().Any(s => s.Value == "m_Name"))
                        return m;
                }
                throw new InvalidDataException("Could not locate AnimationClip mapping.");
            }

            return clipMap;
        }

        private static bool TryGetMapping(YamlMappingNode map, string key, out YamlMappingNode value)
        {
            value = null!;
            if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node))
                return false;
            if (node is not YamlMappingNode m)
                return false;
            value = m;
            return true;
        }

        private static YamlMappingNode? GetMappingOrNull(YamlMappingNode map, string key)
            => TryGetMapping(map, key, out var m) ? m : null;

        private static bool TryGetSequence(YamlMappingNode map, string key, out YamlSequenceNode seq)
        {
            seq = null!;
            if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node))
                return false;
            if (node is not YamlSequenceNode s)
                return false;
            seq = s;
            return true;
        }

        private static YamlSequenceNode? GetSequenceOrNull(YamlMappingNode map, string key)
            => TryGetSequence(map, key, out var s) ? s : null;

        private static string? GetScalarString(YamlMappingNode map, string key)
        {
            if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node))
                return null;
            return (node as YamlScalarNode)?.Value;
        }

        private static string? GetScalarStringOrNull(YamlMappingNode? map, string key)
            => map is null ? null : GetScalarString(map, key);

        private static int? GetScalarInt(YamlMappingNode map, string key)
        {
            var s = GetScalarString(map, key);
            if (s is null)
                return null;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return v;
            return null;
        }

        private static int? GetScalarIntOrNull(YamlMappingNode? map, string key)
            => map is null ? null : GetScalarInt(map, key);

        private static float? GetScalarFloat(YamlMappingNode map, string key)
        {
            var s = GetScalarString(map, key);
            if (s is null)
                return null;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                return v;
            return null;
        }

        private static float? GetScalarFloatOrNull(YamlMappingNode? map, string key)
            => map is null ? null : GetScalarFloat(map, key);
    }
}
