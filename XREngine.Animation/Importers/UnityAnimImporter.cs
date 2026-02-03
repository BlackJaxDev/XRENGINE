using System.Globalization;
using System.Numerics;
using Unity;
using XREngine.Animation;
using XREngine.Components.Animation;
using YamlDotNet.RepresentationModel;

namespace XREngine.Animation.Importers
{
    public static class AnimYamlImporter
    {
        private sealed record ScalarCurve(
            string? Path,
            string Attribute,
            int? ClassId,
            IReadOnlyList<CurveKey> Keys);

        private sealed record VectorCurve(
            string? Path,
            string Attribute,
            int? ClassId,
            IReadOnlyDictionary<char, IReadOnlyList<CurveKey>> ComponentKeys);

        private sealed record CurveKey(float Time, float Value, float InSlope, float OutSlope, int CombinedTangentMode)
        {
            /// <summary>
            /// Gets the left (in) tangent mode from the tangentMode bitmask.
            /// </summary>
            public TangentMode LeftTangentMode => TangentModeHelper.GetLeftTangentMode(CombinedTangentMode);

            /// <summary>
            /// Gets the right (out) tangent mode from the tangentMode bitmask.
            /// </summary>
            public TangentMode RightTangentMode => TangentModeHelper.GetRightTangentMode(CombinedTangentMode);

            /// <summary>
            /// Gets whether the tangent is "broken" (left and right can be edited independently).
            /// </summary>
            public bool IsBroken => TangentModeHelper.IsBroken(CombinedTangentMode);

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
                    TangentMode.Free or TangentMode.Auto or TangentMode.ClampedAuto => EVectorInterpType.Smooth,
                    _ => EVectorInterpType.Smooth,
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
            var transformGroups = new Dictionary<(string nodePath, string kind), TransformCurveGroup>();
            foreach (var kvp in scalarByTarget)
            {
                string nodePath = kvp.Key.nodePath;
                string attr = kvp.Key.attribute;
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

                if (group.Kind == "translation" || group.Kind == "scale")
                {
                    // Need x/y/z
                    if (!group.Components.TryGetValue('x', out var cx) ||
                        !group.Components.TryGetValue('y', out var cy) ||
                        !group.Components.TryGetValue('z', out var cz))
                        continue;

                    var xAnim = BuildFloatAnim(cx, length, looped, sampleRate, valueScale: 1.0f);
                    var yAnim = BuildFloatAnim(cy, length, looped, sampleRate, valueScale: 1.0f);
                    var zAnim = BuildFloatAnim(cz, length, looped, sampleRate, valueScale: 1.0f);

                    var vecAnim = new PropAnimVector3
                    {
                        LengthInSeconds = length,
                        Looped = looped,
                        BakedFramesPerSecond = sampleRate,
                    };

                    foreach (float t in UnionKeyTimes(cx, cy, cz))
                    {
                        var v = new Vector3(xAnim.GetValue(t), yAnim.GetValue(t), zAnim.GetValue(t));
                        vecAnim.Keyframes.Add(new Vector3Keyframe(t, v, Vector3.Zero, EVectorInterpType.Smooth));
                    }

                    string propName = group.Kind == "translation" ? "Translation" : "Scale";
                    builder.AddTransformPropertyAnimation(nodePath, propName, vecAnim);
                }
                else if (group.Kind == "rotation")
                {
                    if (!group.Components.TryGetValue('x', out var cx) ||
                        !group.Components.TryGetValue('y', out var cy) ||
                        !group.Components.TryGetValue('z', out var cz) ||
                        !group.Components.TryGetValue('w', out var cw))
                        continue;

                    var xAnim = BuildFloatAnim(cx, length, looped, sampleRate, valueScale: 1.0f);
                    var yAnim = BuildFloatAnim(cy, length, looped, sampleRate, valueScale: 1.0f);
                    var zAnim = BuildFloatAnim(cz, length, looped, sampleRate, valueScale: 1.0f);
                    var wAnim = BuildFloatAnim(cw, length, looped, sampleRate, valueScale: 1.0f);

                    var quatAnim = new PropAnimQuaternion
                    {
                        LengthInSeconds = length,
                        Looped = looped,
                        BakedFramesPerSecond = sampleRate,
                    };

                    foreach (float t in UnionKeyTimes(cx, cy, cz, cw))
                    {
                        var q = new Quaternion(xAnim.GetValue(t), yAnim.GetValue(t), zAnim.GetValue(t), wAnim.GetValue(t));
                        if (q.LengthSquared() > 0)
                            q = Quaternion.Normalize(q);
                        else
                            q = Quaternion.Identity;

                        quatAnim.Keyframes.Add(new QuaternionKeyframe(t, q, Quaternion.Identity, Quaternion.Identity, ERadialInterpType.Linear));
                    }

                    builder.AddTransformPropertyAnimation(nodePath, "Rotation", quatAnim);
                }
            }

            // Build blendshape animations and remaining scalar animations.
            foreach (var c in curves)
            {
                // Skip ones consumed by transform grouping.
                if (TryMapTransformComponent(c.Attribute, out _, out _))
                    continue;

                string nodePath = NormalizePath(c.Path);

                // Humanoid (muscle) curves: these typically have an empty path and classID 95,
                // and the attribute is a human-readable muscle name like "Neck Nod Down-Up".
                // We map these strings to the underlying int value of EHumanoidValue and forward to HumanoidComponent.SetValue(int, float).
                if (IsHumanoidMuscleCurve(c))
                {
                    if (!TryMapUnityHumanoidAttributeToValue(c.Attribute, out EHumanoidValue humanoidValue))
                        continue;

                    var anim = BuildFloatAnim(c, length, looped, sampleRate, valueScale: 1.0f);
                    builder.AddHumanoidValueAnimation(humanoidValue, anim);
                    continue;
                }

                if (c.Attribute.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    string blendshapeName = c.Attribute["blendShape.".Length..];
                    // Blendshape weights are typically 0..100; engine normalized is 0..1.
                    var anim = BuildFloatAnim(c, length, looped, sampleRate, valueScale: 1.0f / 100.0f);
                    builder.AddBlendshapeAnimation(nodePath, blendshapeName, anim);
                    continue;
                }

                // Best-effort generic scalar property: attempt to animate a property on the node's Transform.
                // If attribute matches a known Transform property, map it; otherwise store it as a property name.
                if (TryMapScalarTransformProperty(c.Attribute, out string transformPropertyName))
                {
                    var anim = BuildFloatAnim(c, length, looped, sampleRate, valueScale: 1.0f);
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
                        if (!vc.ComponentKeys.TryGetValue('x', out var xKeys) ||
                            !vc.ComponentKeys.TryGetValue('y', out var yKeys) ||
                            !vc.ComponentKeys.TryGetValue('z', out var zKeys))
                            continue;

                        var xAnim = BuildFloatAnim(new ScalarCurve(vc.Path, vc.Attribute + ".x", vc.ClassId, xKeys), length, looped, sampleRate, 1.0f);
                        var yAnim = BuildFloatAnim(new ScalarCurve(vc.Path, vc.Attribute + ".y", vc.ClassId, yKeys), length, looped, sampleRate, 1.0f);
                        var zAnim = BuildFloatAnim(new ScalarCurve(vc.Path, vc.Attribute + ".z", vc.ClassId, zKeys), length, looped, sampleRate, 1.0f);

                        var vecAnim = new PropAnimVector3
                        {
                            LengthInSeconds = length,
                            Looped = looped,
                            BakedFramesPerSecond = sampleRate,
                        };
                        foreach (float t in UnionKeyTimes(xKeys, yKeys, zKeys))
                        {
                            var v = new Vector3(xAnim.GetValue(t), yAnim.GetValue(t), zAnim.GetValue(t));
                            vecAnim.Keyframes.Add(new Vector3Keyframe(t, v, Vector3.Zero, EVectorInterpType.Smooth));
                        }
                        string propName = kind == "translation" ? "Translation" : "Scale";
                        builder.AddTransformPropertyAnimation(nodePath, propName, vecAnim);
                    }
                    else if (kind == "rotation" && componentCount == 4)
                    {
                        if (!vc.ComponentKeys.TryGetValue('x', out var xKeys) ||
                            !vc.ComponentKeys.TryGetValue('y', out var yKeys) ||
                            !vc.ComponentKeys.TryGetValue('z', out var zKeys) ||
                            !vc.ComponentKeys.TryGetValue('w', out var wKeys))
                            continue;

                        var xAnim = BuildFloatAnim(new ScalarCurve(vc.Path, vc.Attribute + ".x", vc.ClassId, xKeys), length, looped, sampleRate, 1.0f);
                        var yAnim = BuildFloatAnim(new ScalarCurve(vc.Path, vc.Attribute + ".y", vc.ClassId, yKeys), length, looped, sampleRate, 1.0f);
                        var zAnim = BuildFloatAnim(new ScalarCurve(vc.Path, vc.Attribute + ".z", vc.ClassId, zKeys), length, looped, sampleRate, 1.0f);
                        var wAnim = BuildFloatAnim(new ScalarCurve(vc.Path, vc.Attribute + ".w", vc.ClassId, wKeys), length, looped, sampleRate, 1.0f);

                        var quatAnim = new PropAnimQuaternion
                        {
                            LengthInSeconds = length,
                            Looped = looped,
                            BakedFramesPerSecond = sampleRate,
                        };
                        foreach (float t in UnionKeyTimes(xKeys, yKeys, zKeys, wKeys))
                        {
                            var q = new Quaternion(xAnim.GetValue(t), yAnim.GetValue(t), zAnim.GetValue(t), wAnim.GetValue(t));
                            if (q.LengthSquared() > 0)
                                q = Quaternion.Normalize(q);
                            else
                                q = Quaternion.Identity;
                            quatAnim.Keyframes.Add(new QuaternionKeyframe(t, q, Quaternion.Identity, Quaternion.Identity, ERadialInterpType.Linear));
                        }
                        builder.AddTransformPropertyAnimation(nodePath, "Rotation", quatAnim);
                    }
                }
            }

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
                var method = GetOrAddMethod(getComp, "SetValue", [humanoidValue, 0.0f], animatedArgIndex: 1, cacheReturnValue: false);
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

            // IMPORTANT: These ints must match the underlying values of XREngine.Components.Animation.EHumanoidValue.
            // Keep the enum stable or update this mapping accordingly.
            switch (attribute)
            {
                case "Left Eye Down-Up": humanoidValue = EHumanoidValue.LeftEyeDownUp; return true;
                case "Left Eye In-Out": humanoidValue = EHumanoidValue.LeftEyeInOut; return true;
                case "Right Eye Down-Up": humanoidValue = EHumanoidValue.RightEyeDownUp; return true;
                case "Right Eye In-Out": humanoidValue = EHumanoidValue.RightEyeInOut; return true;

                case "Spine Front-Back": humanoidValue = EHumanoidValue.SpineFrontBack; return true;
                case "Spine Left-Right": humanoidValue = EHumanoidValue.SpineLeftRight; return true;
                case "Spine Twist Left-Right": humanoidValue = EHumanoidValue.SpineTwistLeftRight; return true;
                case "Chest Front-Back": humanoidValue = EHumanoidValue.ChestFrontBack; return true;
                case "Chest Left-Right": humanoidValue = EHumanoidValue.ChestLeftRight; return true;
                case "Chest Twist Left-Right": humanoidValue = EHumanoidValue.ChestTwistLeftRight; return true;
                case "UpperChest Front-Back": humanoidValue = EHumanoidValue.UpperChestFrontBack; return true;
                case "UpperChest Left-Right": humanoidValue = EHumanoidValue.UpperChestLeftRight; return true;
                case "UpperChest Twist Left-Right": humanoidValue = EHumanoidValue.UpperChestTwistLeftRight; return true;
                case "Neck Nod Down-Up": humanoidValue = EHumanoidValue.NeckNodDownUp; return true;
                case "Neck Tilt Left-Right": humanoidValue = EHumanoidValue.NeckTiltLeftRight; return true;
                case "Neck Turn Left-Right": humanoidValue = EHumanoidValue.NeckTurnLeftRight; return true;
                case "Head Nod Down-Up": humanoidValue = EHumanoidValue.HeadNodDownUp; return true;
                case "Head Tilt Left-Right": humanoidValue = EHumanoidValue.HeadTiltLeftRight; return true;
                case "Head Turn Left-Right": humanoidValue = EHumanoidValue.HeadTurnLeftRight; return true;
                case "Jaw Close": humanoidValue = EHumanoidValue.JawClose; return true;
                case "Jaw Left-Right": humanoidValue = EHumanoidValue.JawLeftRight; return true;

                case "Left Shoulder Down-Up": humanoidValue = EHumanoidValue.LeftShoulderDownUp; return true;
                case "Left Shoulder Front-Back": humanoidValue = EHumanoidValue.LeftShoulderFrontBack; return true;
                case "Left Arm Down-Up": humanoidValue = EHumanoidValue.LeftArmDownUp; return true;
                case "Left Arm Front-Back": humanoidValue = EHumanoidValue.LeftArmFrontBack; return true;
                case "Left Arm Twist In-Out": humanoidValue = EHumanoidValue.LeftArmTwistInOut; return true;
                case "Left Forearm Stretch": humanoidValue = EHumanoidValue.LeftForearmStretch; return true;
                case "Left Forearm Twist In-Out": humanoidValue = EHumanoidValue.LeftForearmTwistInOut; return true;
                case "Left Hand Down-Up": humanoidValue = EHumanoidValue.LeftHandDownUp; return true;
                case "Left Hand In-Out": humanoidValue = EHumanoidValue.LeftHandInOut; return true;
                case "Left Upper Leg Front-Back": humanoidValue = EHumanoidValue.LeftUpperLegFrontBack; return true;
                case "Left Upper Leg In-Out": humanoidValue = EHumanoidValue.LeftUpperLegInOut; return true;
                case "Left Upper Leg Twist In-Out": humanoidValue = EHumanoidValue.LeftUpperLegTwistInOut; return true;
                case "Left Lower Leg Stretch": humanoidValue = EHumanoidValue.LeftLowerLegStretch; return true;
                case "Left Lower Leg Twist In-Out": humanoidValue = EHumanoidValue.LeftLowerLegTwistInOut; return true;
                case "Left Foot Up-Down": humanoidValue = EHumanoidValue.LeftFootUpDown; return true;
                case "Left Foot Twist In-Out": humanoidValue = EHumanoidValue.LeftFootTwistInOut; return true;
                case "Left Toes Up-Down": humanoidValue = EHumanoidValue.LeftToesUpDown; return true;

                case "Right Shoulder Down-Up": humanoidValue = EHumanoidValue.RightShoulderDownUp; return true;
                case "Right Shoulder Front-Back": humanoidValue = EHumanoidValue.RightShoulderFrontBack; return true;
                case "Right Arm Down-Up": humanoidValue = EHumanoidValue.RightArmDownUp; return true;
                case "Right Arm Front-Back": humanoidValue = EHumanoidValue.RightArmFrontBack; return true;
                case "Right Arm Twist In-Out": humanoidValue = EHumanoidValue.RightArmTwistInOut; return true;
                case "Right Forearm Stretch": humanoidValue = EHumanoidValue.RightForearmStretch; return true;
                case "Right Forearm Twist In-Out": humanoidValue = EHumanoidValue.RightForearmTwistInOut; return true;
                case "Right Hand Down-Up": humanoidValue = EHumanoidValue.RightHandDownUp; return true;
                case "Right Hand In-Out": humanoidValue = EHumanoidValue.RightHandInOut; return true;
                case "Right Upper Leg Front-Back": humanoidValue = EHumanoidValue.RightUpperLegFrontBack; return true;
                case "Right Upper Leg In-Out": humanoidValue = EHumanoidValue.RightUpperLegInOut; return true;
                case "Right Upper Leg Twist In-Out": humanoidValue = EHumanoidValue.RightUpperLegTwistInOut; return true;
                case "Right Lower Leg Stretch": humanoidValue = EHumanoidValue.RightLowerLegStretch; return true;
                case "Right Lower Leg Twist In-Out": humanoidValue = EHumanoidValue.RightLowerLegTwistInOut; return true;
                case "Right Foot Up-Down": humanoidValue = EHumanoidValue.RightFootUpDown; return true;
                case "Right Foot Twist In-Out": humanoidValue = EHumanoidValue.RightFootTwistInOut; return true;
                case "Right Toes Up-Down": humanoidValue = EHumanoidValue.RightToesUpDown; return true;

                case "LeftHand.Index.Spread": humanoidValue = EHumanoidValue.LeftHandIndexSpread; return true;
                case "LeftHand.Index.1 Stretched": humanoidValue = EHumanoidValue.LeftHandIndex1Stretched; return true;
                case "LeftHand.Index.2 Stretched": humanoidValue = EHumanoidValue.LeftHandIndex2Stretched; return true;
                case "LeftHand.Index.3 Stretched": humanoidValue = EHumanoidValue.LeftHandIndex3Stretched; return true;
                case "LeftHand.Middle.Spread": humanoidValue = EHumanoidValue.LeftHandMiddleSpread; return true;
                case "LeftHand.Middle.1 Stretched": humanoidValue = EHumanoidValue.LeftHandMiddle1Stretched; return true;
                case "LeftHand.Middle.2 Stretched": humanoidValue = EHumanoidValue.LeftHandMiddle2Stretched; return true;
                case "LeftHand.Middle.3 Stretched": humanoidValue = EHumanoidValue.LeftHandMiddle3Stretched; return true;
                case "LeftHand.Ring.Spread": humanoidValue = EHumanoidValue.LeftHandRingSpread; return true;
                case "LeftHand.Ring.1 Stretched": humanoidValue = EHumanoidValue.LeftHandRing1Stretched; return true;
                case "LeftHand.Ring.2 Stretched": humanoidValue = EHumanoidValue.LeftHandRing2Stretched; return true;
                case "LeftHand.Ring.3 Stretched": humanoidValue = EHumanoidValue.LeftHandRing3Stretched; return true;
                case "LeftHand.Little.Spread": humanoidValue = EHumanoidValue.LeftHandLittleSpread; return true;
                case "LeftHand.Little.1 Stretched": humanoidValue = EHumanoidValue.LeftHandLittle1Stretched; return true;
                case "LeftHand.Little.2 Stretched": humanoidValue = EHumanoidValue.LeftHandLittle2Stretched; return true;
                case "LeftHand.Little.3 Stretched": humanoidValue = EHumanoidValue.LeftHandLittle3Stretched; return true;
                case "LeftHand.Thumb.Spread": humanoidValue = EHumanoidValue.LeftHandThumbSpread; return true;
                case "LeftHand.Thumb.1 Stretched": humanoidValue = EHumanoidValue.LeftHandThumb1Stretched; return true;
                case "LeftHand.Thumb.2 Stretched": humanoidValue = EHumanoidValue.LeftHandThumb2Stretched; return true;
                case "LeftHand.Thumb.3 Stretched": humanoidValue = EHumanoidValue.LeftHandThumb3Stretched; return true;

                case "RightHand.Index.Spread": humanoidValue = EHumanoidValue.RightHandIndexSpread; return true;
                case "RightHand.Index.1 Stretched": humanoidValue = EHumanoidValue.RightHandIndex1Stretched; return true;
                case "RightHand.Index.2 Stretched": humanoidValue = EHumanoidValue.RightHandIndex2Stretched; return true;
                case "RightHand.Index.3 Stretched": humanoidValue = EHumanoidValue.RightHandIndex3Stretched; return true;
                case "RightHand.Middle.Spread": humanoidValue = EHumanoidValue.RightHandMiddleSpread; return true;
                case "RightHand.Middle.1 Stretched": humanoidValue = EHumanoidValue.RightHandMiddle1Stretched; return true;
                case "RightHand.Middle.2 Stretched": humanoidValue = EHumanoidValue.RightHandMiddle2Stretched; return true;
                case "RightHand.Middle.3 Stretched": humanoidValue = EHumanoidValue.RightHandMiddle3Stretched; return true;
                case "RightHand.Ring.Spread": humanoidValue = EHumanoidValue.RightHandRingSpread; return true;
                case "RightHand.Ring.1 Stretched": humanoidValue = EHumanoidValue.RightHandRing1Stretched; return true;
                case "RightHand.Ring.2 Stretched": humanoidValue = EHumanoidValue.RightHandRing2Stretched; return true;
                case "RightHand.Ring.3 Stretched": humanoidValue = EHumanoidValue.RightHandRing3Stretched; return true;
                case "RightHand.Little.Spread": humanoidValue = EHumanoidValue.RightHandLittleSpread; return true;
                case "RightHand.Little.1 Stretched": humanoidValue = EHumanoidValue.RightHandLittle1Stretched; return true;
                case "RightHand.Little.2 Stretched": humanoidValue = EHumanoidValue.RightHandLittle2Stretched; return true;
                case "RightHand.Little.3 Stretched": humanoidValue = EHumanoidValue.RightHandLittle3Stretched; return true;
                case "RightHand.Thumb.Spread": humanoidValue = EHumanoidValue.RightHandThumbSpread; return true;
                case "RightHand.Thumb.1 Stretched": humanoidValue = EHumanoidValue.RightHandThumb1Stretched; return true;
                case "RightHand.Thumb.2 Stretched": humanoidValue = EHumanoidValue.RightHandThumb2Stretched; return true;
                case "RightHand.Thumb.3 Stretched": humanoidValue = EHumanoidValue.RightHandThumb3Stretched; return true;
            }

            humanoidValue = default;
            return false;
        }

        private static bool IsHumanoidMuscleCurve(ScalarCurve c)
        {
            // Humanoid muscle curves commonly have an empty binding path and classID 95.
            if (!string.IsNullOrWhiteSpace(c.Path))
                return false;

            if (c.ClassId is not 95)
                return false;

            // Avoid treating blendShape.* or RootT/RootQ as humanoid.
            if (c.Attribute.StartsWith("blendShape.", StringComparison.Ordinal))
                return false;

            if (c.Attribute.StartsWith("RootT.", StringComparison.Ordinal) || c.Attribute.StartsWith("RootQ.", StringComparison.Ordinal))
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
                    if (TryParseCurveData(item, out var keys))
                    {
                        scalarCurves.Add(new ScalarCurve(path, attribute!, classId, keys));
                        addedAny = true;
                        continue;
                    }
                }

                // Case 2: Vector/quaternion curve item (curve has x/y/z(/w) each containing curve data)
                // These are not present in your current samples, but this keeps the importer usable for more exporter variants.
                if (TryParseVectorCurveData(item, out var vecAttribute, out var components))
                {
                    vectorCurves.Add(new VectorCurve(path, vecAttribute, classId, components));
                    addedAny = true;
                }
            }

            return addedAny;
        }

        private static bool TryParseCurveData(YamlMappingNode item, out IReadOnlyList<CurveKey> keys)
        {
            keys = Array.Empty<CurveKey>();

            if (!TryGetMapping(item, "curve", out var curveMap))
                return false;

            if (!TryGetSequence(curveMap, "m_Curve", out var keySeq))
                return false;

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
            out IReadOnlyDictionary<char, IReadOnlyList<CurveKey>> componentKeys)
        {
            attribute = string.Empty;
            componentKeys = new Dictionary<char, IReadOnlyList<CurveKey>>();

            if (!TryGetMapping(item, "curve", out var curveMap))
                return false;

            // Some exporters store "attribute" at the item level even for vector curves; if not, we can't map.
            attribute = GetScalarString(item, "attribute") ?? string.Empty;
            if (string.IsNullOrEmpty(attribute))
                return false;

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

        private static PropAnimFloat BuildFloatAnim(ScalarCurve curve, float length, bool looped, int fps, float valueScale)
        {
            var anim = new PropAnimFloat
            {
                LengthInSeconds = length,
                Looped = looped,
                BakedFramesPerSecond = fps,
            };

            foreach (var k in curve.Keys)
            {
                anim.Keyframes.Add(new FloatKeyframe
                {
                    Second = k.Time,
                    InValue = k.Value * valueScale,
                    OutValue = k.Value * valueScale,
                    InTangent = k.InSlope * valueScale,
                    OutTangent = k.OutSlope * valueScale,
                    InterpolationTypeIn = k.InInterpType,
                    InterpolationTypeOut = k.OutInterpType,
                });
            }

            return anim;
        }

        private static IEnumerable<float> UnionKeyTimes(params ScalarCurve[] curves)
            => curves
                .SelectMany(c => c.Keys.Select(k => k.Time))
                .Distinct()
                .OrderBy(t => t);

        private static IEnumerable<float> UnionKeyTimes(IReadOnlyList<CurveKey> a, IReadOnlyList<CurveKey> b, IReadOnlyList<CurveKey> c)
            => a.Select(k => k.Time)
                .Concat(b.Select(k => k.Time))
                .Concat(c.Select(k => k.Time))
                .Distinct()
                .OrderBy(t => t);

        private static IEnumerable<float> UnionKeyTimes(IReadOnlyList<CurveKey> a, IReadOnlyList<CurveKey> b, IReadOnlyList<CurveKey> c, IReadOnlyList<CurveKey> d)
            => a.Select(k => k.Time)
                .Concat(b.Select(k => k.Time))
                .Concat(c.Select(k => k.Time))
                .Concat(d.Select(k => k.Time))
                .Distinct()
                .OrderBy(t => t);

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

            // Root motion curves (common in many .anim clips)
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

        private static bool TryMapVectorAttribute(string attribute, out string kind, out int componentCount)
        {
            kind = string.Empty;
            componentCount = 0;

            if (attribute.Equals("RootT", StringComparison.Ordinal) || attribute.Equals("m_LocalPosition", StringComparison.Ordinal) || attribute.Equals("localPosition", StringComparison.Ordinal))
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
            if (attribute.Equals("RootQ", StringComparison.Ordinal) || attribute.Equals("m_LocalRotation", StringComparison.Ordinal) || attribute.Equals("localRotation", StringComparison.Ordinal))
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
