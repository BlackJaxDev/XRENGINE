using System.Globalization;
using System.Numerics;
using XREngine.Animation;
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

        private sealed record CurveKey(float Time, float Value, float InSlope, float OutSlope, int TangentMode);

        public static AnimationClip Import(string filePath)
        {
            if (filePath is null)
                throw new ArgumentNullException(nameof(filePath));

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
                // We forward these to HumanoidComponent.SetValue(string name, float amount).
                if (IsHumanoidMuscleCurve(c))
                {
                    var anim = BuildFloatAnim(c, length, looped, sampleRate, valueScale: 1.0f);
                    builder.AddHumanoidValueAnimation(c.Attribute, anim);
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

        private sealed class TransformCurveGroup
        {
            public TransformCurveGroup(string kind) => Kind = kind;
            public string Kind { get; }
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
                var getComp = GetOrAddMethod(node, "GetComponent", new object?[] { "ModelComponent" }, animatedArgIndex: 0, cacheReturnValue: true);
                var method = GetOrAddMethod(getComp, "SetBlendShapeWeightNormalized", new object?[] { blendshapeName, 0.0f, StringComparison.InvariantCultureIgnoreCase }, animatedArgIndex: 1, cacheReturnValue: false);
                method.Animation = anim;
            }

            public void AddHumanoidValueAnimation(string humanoidAttribute, PropAnimFloat anim)
            {
                // Humanoid values are applied on the root node's HumanoidComponent.
                // We keep this importer decoupled from the HumanoidComponent assembly by using string-based reflection.
                var getComp = GetOrAddMethod(_sceneNode, "GetComponent", new object?[] { "HumanoidComponent" }, animatedArgIndex: 0, cacheReturnValue: true);
                var method = GetOrAddMethod(getComp, "SetValue", new object?[] { humanoidAttribute, 0.0f }, animatedArgIndex: 1, cacheReturnValue: false);
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
                        current = GetOrAddMethod(current, "FindDescendantByName", new object?[] { seg, StringComparison.InvariantCultureIgnoreCase }, animatedArgIndex: 0, cacheReturnValue: true);
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
                    if (child.MemberName != methodName || child.MemberType != EAnimationMemberType.Method)
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

        private static bool IsHumanoidMuscleCurve(ScalarCurve c)
        {
            // Humanoid muscle curves commonly have an empty binding path and classID 95.
            if (!string.IsNullOrWhiteSpace(c.Path))
                return false;

            if (c.ClassId is not 95)
                return false;

            // Heuristic: humanoid muscle names have spaces and/or hyphens.
            // Avoid treating blendShape.* or RootT/RootQ as humanoid.
            if (c.Attribute.StartsWith("blendShape.", StringComparison.Ordinal))
                return false;

            if (c.Attribute.StartsWith("RootT.", StringComparison.Ordinal) || c.Attribute.StartsWith("RootQ.", StringComparison.Ordinal))
                return false;

            return c.Attribute.Contains(' ') || c.Attribute.Contains('-');
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
                    InterpolationTypeIn = EVectorInterpType.Smooth,
                    InterpolationTypeOut = EVectorInterpType.Smooth,
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
