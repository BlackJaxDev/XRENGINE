using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SerializedAssets;
using XREngine.Animation;
using XREngine.Animation.IK;
using XREngine.Components.Animation;
using YamlDotNet.RepresentationModel;

namespace XREngine.Animation.Importers
{
    public static partial class AnimYamlImporter
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

        private static int GetHumanoidTranslationDofTargetComponentIndex(char component)
            => GetHumanoidRootMotionPositionTargetComponent(component) - 'x';

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
            string SourceField,
            string SourcePayload,
            string? Path,
            string Attribute,
            int? ClassId,
            SourceAssetReference Script,
            IReadOnlyList<CurveKey> Keys,
            int PreInfinity,
            int PostInfinity,
            int BindingFlags = 0,
            int BindingSerializedVersion = 0,
            ImportedAnimationBindingDescriptor? BindingDescriptor = null);

        private sealed record VectorCurve(
            string SourceField,
            string SourcePayload,
            string? Path,
            string Attribute,
            int? ClassId,
            SourceAssetReference Script,
            IReadOnlyDictionary<char, IReadOnlyList<CurveKey>> ComponentKeys,
            int PreInfinity,
            int PostInfinity,
            int BindingFlags = 0,
            int BindingSerializedVersion = 0);

        private sealed record ObjectCurve(
            string SourceField,
            string SourcePayload,
            string? Path,
            string Attribute,
            int? ClassId,
            SourceAssetReference Script,
            IReadOnlyList<ObjectCurveKey> Keys,
            ImportedAnimationBindingDescriptor? BindingDescriptor = null);

        private sealed record ObjectCurveKey(float Time, SourceAssetReference Value, int SourceOrder);

        private sealed record CurveKey(
            float Time,
            float Value,
            float InSlope,
            float OutSlope,
            int CombinedTangentMode,
            int WeightedMode,
            float InWeight,
            float OutWeight)
        {
            /// <summary>
            /// Gets the left (in) tangent mode from the tangentMode bitmask.
            /// </summary>
            public TangentMode LeftTangentMode => SerializedAnimationClip.TangentModeHelper.GetLeftTangentMode(CombinedTangentMode);

            /// <summary>
            /// Gets the right (out) tangent mode from the tangentMode bitmask.
            /// </summary>
            public TangentMode RightTangentMode => SerializedAnimationClip.TangentModeHelper.GetRightTangentMode(CombinedTangentMode);

            /// <summary>
            /// Gets whether the tangent is "broken" (left and right can be edited independently).
            /// </summary>
            public bool IsBroken => SerializedAnimationClip.TangentModeHelper.IsBroken(CombinedTangentMode);

            /// <summary>
            /// Gets the interpolation type for the incoming (left) tangent.
            /// </summary>
            public EVectorInterpType InInterpType
                => float.IsInfinity(InSlope) || LeftTangentMode == TangentMode.Constant
                    ? EVectorInterpType.Step
                    : EVectorInterpType.Hermite;

            /// <summary>
            /// Gets the interpolation type for the outgoing (right) tangent.
            /// </summary>
            public EVectorInterpType OutInterpType
                => float.IsInfinity(OutSlope) || RightTangentMode == TangentMode.Constant
                    ? EVectorInterpType.Step
                    : EVectorInterpType.Hermite;

        }

        public static AnimationClip Import(string filePath)
            => ImportCore(filePath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        private static AnimationClip ImportCore(string filePath, HashSet<string> activeAdditiveReferences)
        {
            ArgumentNullException.ThrowIfNull(filePath);

            byte[] sourceBytes = File.ReadAllBytes(filePath);
            string sourceContentHash = Convert.ToHexString(SHA256.HashData(sourceBytes));
            using var sourceStream = new MemoryStream(sourceBytes, writable: false);
            using var reader = new StreamReader(sourceStream, detectEncodingFromByteOrderMarks: true);
            var yaml = new YamlStream();
            yaml.Load(reader);

            var clipMap = GetAnimationClipMapping(yaml);
            int serializedVersion = GetScalarInt(clipMap, "serializedVersion") ?? 0;
            bool recognizedSerializedVersion = IsRecognizedSerializedVersion(serializedVersion);
            var manifestBuilder = new ImportedAnimationImportManifestBuilder
            {
                SourceIdentity = new ImportedAnimationSourceIdentity
                {
                    SerializedVersion = serializedVersion,
                    SourceContentSha256 = sourceContentHash,
                },
            };
            manifestBuilder.RecordSection(
                EImportedAnimationDataDomain.SourceEncoding,
                recognizedSerializedVersion
                    ? EImportedAnimationCapabilityState.SupportedAndApplied
                    : EImportedAnimationCapabilityState.Unsupported,
                "AnimationClip.serializedVersion",
                recognizedSerializedVersion
                    ? $"Unity YAML AnimationClip serializedVersion {serializedVersion}."
                    : serializedVersion > 0
                        ? $"Unity YAML AnimationClip serializedVersion {serializedVersion} is not in the currently declared source-version contract."
                        : "AnimationClip.serializedVersion is missing; the source schema cannot be identified safely.",
                serializedYaml: string.Empty);
            AuditSourceSchema(clipMap, serializedVersion, manifestBuilder);

            string name = GetScalarString(clipMap, "m_Name") ?? Path.GetFileNameWithoutExtension(filePath);
            int sampleRate = GetScalarInt(clipMap, "m_SampleRate") ?? 30;
            int sourceWrapMode = GetScalarInt(clipMap, "m_WrapMode") ?? 0;
            if (sourceWrapMode is not (0 or 1 or 2 or 4 or 8))
            {
                manifestBuilder.RecordSection(
                    EImportedAnimationDataDomain.ClipMetadata,
                    EImportedAnimationCapabilityState.Unsupported,
                    "m_WrapMode",
                    $"Unity wrap mode {sourceWrapMode} is outside the declared native capability contract.",
                    sourceWrapMode.ToString(CultureInfo.InvariantCulture));
            }

            ImportedAnimationClipMetadata clipMetadata = ReadClipMetadata(clipMap, sampleRate, sourceWrapMode);
            manifestBuilder.RecordSection(
                EImportedAnimationDataDomain.ClipMetadata,
                EImportedAnimationCapabilityState.SupportedAndApplied,
                "AnimationClip header",
                "Sample rate, wrap behavior, legacy/compression/high-quality flags, and authored bounds were imported.",
                serializedYaml: string.Empty);

            var settingsMap = GetMappingOrNull(clipMap, "m_AnimationClipSettings");
            float startTime = GetScalarFloatOrNull(settingsMap, "m_StartTime") ?? 0.0f;
            float stopTime = GetScalarFloatOrNull(settingsMap, "m_StopTime") ?? 0.0f;
            bool looped = (GetScalarIntOrNull(settingsMap, "m_LoopTime") ?? 0) != 0;
            ImportedHumanoidClipRootMotionSettings? rootMotionSettings = settingsMap is null
                ? null
                : new ImportedHumanoidClipRootMotionSettings
                {
                    AdditiveReferencePoseClip = ReadAssetReference(GetMappingOrNull(settingsMap, "m_AdditiveReferencePoseClip")),
                    AdditiveReferencePoseTime = GetScalarFloatOrNull(settingsMap, "m_AdditiveReferencePoseTime") ?? 0.0f,
                    HasAdditiveReferencePose = (GetScalarIntOrNull(settingsMap, "m_HasAdditiveReferencePose") ?? 0) != 0,
                    StartTime = startTime,
                    StopTime = stopTime,
                    OrientationOffsetY = GetScalarFloatOrNull(settingsMap, "m_OrientationOffsetY") ?? 0.0f,
                    Level = GetScalarFloatOrNull(settingsMap, "m_Level") ?? 0.0f,
                    CycleOffset = GetScalarFloatOrNull(settingsMap, "m_CycleOffset") ?? 0.0f,
                    LoopTime = looped,
                    LoopPose = (GetScalarIntOrNull(settingsMap, "m_LoopBlend") ?? 0) != 0,
                    BakeOrientationIntoPose = (GetScalarIntOrNull(settingsMap, "m_LoopBlendOrientation") ?? 0) != 0,
                    BakePositionYIntoPose = (GetScalarIntOrNull(settingsMap, "m_LoopBlendPositionY") ?? 0) != 0,
                    BakePositionXZIntoPose = (GetScalarIntOrNull(settingsMap, "m_LoopBlendPositionXZ") ?? 0) != 0,
                    KeepOriginalOrientation = (GetScalarIntOrNull(settingsMap, "m_KeepOriginalOrientation") ?? 0) != 0,
                    KeepOriginalPositionY = (GetScalarIntOrNull(settingsMap, "m_KeepOriginalPositionY") ?? 0) != 0,
                    KeepOriginalPositionXZ = (GetScalarIntOrNull(settingsMap, "m_KeepOriginalPositionXZ") ?? 0) != 0,
                    HeightFromFeet = (GetScalarIntOrNull(settingsMap, "m_HeightFromFeet") ?? 0) != 0,
                    Mirror = (GetScalarIntOrNull(settingsMap, "m_Mirror") ?? 0) != 0,
                };
            string rootSettingsDiagnostic = string.Empty;
            bool rootSettingsExecutable = settingsMap is null
                || ImportedHumanoidRootMotionPolicy.TryCreate(
                    rootMotionSettings!,
                    out _,
                    out rootSettingsDiagnostic);

            var curves = new List<ScalarCurve>();
            var vecCurves = new List<VectorCurve>();
            var materialBindings = new List<SerializedMaterialAnimationBinding>();
            var materialBindingDiagnostics = new List<string>();
            var genericBindings = new List<ImportedAnimationBindingDescriptor>();
            ImportedAnimationEvent[] animationEvents = ReadAnimationEvents(
                clipMap,
                filePath,
                startTime,
                stopTime,
                manifestBuilder);
            List<ObjectCurve> objectCurves = ReadObjectReferenceCurves(clipMap, filePath, manifestBuilder);

            // Some exporters duplicate data between m_FloatCurves and m_EditorCurves.
            // Prefer m_FloatCurves when present; fall back to m_EditorCurves.
            bool addedAny = false;
            addedAny |= TryReadCurveList(clipMap, "m_FloatCurves", curves, vecCurves, manifestBuilder);
            if (!addedAny)
                TryReadCurveList(clipMap, "m_EditorCurves", curves, vecCurves, manifestBuilder);
            else if (GetSequenceOrNull(clipMap, "m_EditorCurves") is { Children.Count: > 0 })
                manifestBuilder.RecordNotice(
                    EImportedAnimationDataDomain.SourceEncoding,
                    "m_EditorCurves was present alongside authoritative m_FloatCurves and was treated as Unity's editor duplicate representation.");

            // Also attempt to read other curve lists (some exporters store transform curves there).
            TryReadCurveList(clipMap, "m_PositionCurves", curves, vecCurves, manifestBuilder);
            TryReadCurveList(clipMap, "m_ScaleCurves", curves, vecCurves, manifestBuilder);
            TryReadCurveList(clipMap, "m_EulerCurves", curves, vecCurves, manifestBuilder);
            TryReadCurveList(clipMap, "m_RotationCurves", curves, vecCurves, manifestBuilder);
            DecodeCompressedRotationCurves(clipMap, vecCurves, manifestBuilder);
            DecodePackedClipRepresentations(
                clipMap,
                filePath,
                curves,
                objectCurves,
                manifestBuilder,
                hasAuthoritativeEditableScalarCurves: curves.Count > 0 || vecCurves.Count > 0,
                hasAuthoritativeEditableObjectCurves: objectCurves.Count > 0,
                startTime,
                stopTime,
                sampleRate);
            NormalizeQuaternionVectorCurves(vecCurves, manifestBuilder);
            NormalizeDefaultInfinityModes(curves, vecCurves, sourceWrapMode, looped);
            ReadMaterialObjectReferenceBindings(clipMap, materialBindings, materialBindingDiagnostics);
            RemoveUnsupportedCurveEncodings(curves, manifestBuilder);
            RemoveUnsupportedCurveEncodings(vecCurves, manifestBuilder);

            float length = Math.Max(0.0f, stopTime - startTime);
            if (length <= 0.0f)
                length = GetMaxTime(curves, vecCurves, objectCurves, animationEvents);

            var clip = new AnimationClip
            {
                Name = name,
                LengthInSeconds = length,
                Looped = looped,
                SampleRate = sampleRate,
                ImportedMetadata = clipMetadata,
                ImportedEvents = animationEvents,
                ImportedHumanoidRootMotionSettings = rootMotionSettings,
                RootMember = new AnimationMember("Root", EAnimationMemberType.Group),
            };

            // All animations are rooted at an AnimationClipComponent (XRComponent) instance.
            // We navigate from that root to SceneNode, then to descendants by name.
            var builder = new AnimMemberBuilder(clip.RootMember);

            // 1) Handle scalar curves (includes RootT.x/RootQ.w/etc and blendShape.*)
            var scalarByTarget = new Dictionary<
                (string nodePath, string attribute, uint pathHash, uint attributeHash, int component),
                ScalarCurve>();
            foreach (var c in curves)
            {
                string nodePath = NormalizePath(c.Path);
                ImportedAnimationBindingDescriptor? packedBinding = c.BindingDescriptor;
                var key = (
                    nodePath,
                    c.Attribute,
                    packedBinding?.PathHash ?? 0,
                    packedBinding?.AttributeHash ?? 0,
                    packedBinding?.Component ?? -1);
                if (scalarByTarget.TryAdd(key, c))
                    continue;

                manifestBuilder.RecordBinding(
                    EImportedAnimationDataDomain.SourceEncoding,
                    EImportedAnimationCapabilityState.Unsupported,
                    c.SourceField,
                    nodePath,
                    c.Attribute,
                    c.ClassId,
                    runtimeTarget: string.Empty,
                    "Multiple authoritative scalar curves target the same path and attribute.");
                manifestBuilder.PreservePayload(
                    EImportedAnimationDataDomain.SourceEncoding,
                    $"{c.SourceField}:{nodePath}:{c.Attribute}",
                    c.SourcePayload);
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

                // Packed bindings retain only Unity path/property hashes. They must
                // remain on the typed runtime binding path so the target hierarchy,
                // blendshape table, or adapter can resolve those hashes at preflight.
                if (kvp.Value.BindingDescriptor is not null
                    && !IsNativePackedHumanoidSemanticBinding(kvp.Value))
                    continue;

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

            NormalizeQuaternionScalarGroups(transformGroups.Values, manifestBuilder);
            NormalizeQuaternionScalarGroups(rootMotionGroups.Values, manifestBuilder);
            NormalizeQuaternionScalarGroups(ikGoalGroups.Values, manifestBuilder);

            // Build transform animations.
            foreach (var kv in transformGroups)
            {
                string nodePath = kv.Key.nodePath;
                var group = kv.Value;

                if (group.Kind == "translation")
                {
                    foreach (var component in group.Components.OrderBy(x => x.Key))
                    {
                        RecordAppliedBinding(
                            manifestBuilder,
                            EImportedAnimationDataDomain.GenericTransform,
                            component.Value,
                            $"{nodePath}:Transform.Translation.{component.Key}");
                        var anim = BuildFloatAnim(component.Value, length, looped, sampleRate, GetPositionComponentScale(component.Key), startTime);
                        builder.AddTransformComponentAnimation(nodePath, group.Kind, component.Key, anim);
                    }
                }
                else if (group.Kind == "scale")
                {
                    foreach (var component in group.Components.OrderBy(x => x.Key))
                    {
                        RecordAppliedBinding(
                            manifestBuilder,
                            EImportedAnimationDataDomain.GenericTransform,
                            component.Value,
                            $"{nodePath}:Transform.Scale.{component.Key}");
                        var anim = BuildFloatAnim(component.Value, length, looped, sampleRate, 1.0f, startTime);
                        builder.AddTransformComponentAnimation(nodePath, group.Kind, component.Key, anim);
                    }
                }
                else if (group.Kind == "rotation")
                {
                    foreach (var component in group.Components.OrderBy(x => x.Key))
                    {
                        RecordAppliedBinding(
                            manifestBuilder,
                            EImportedAnimationDataDomain.GenericTransform,
                            component.Value,
                            $"{nodePath}:Transform.Rotation.{component.Key}");
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
                        RecordAppliedBinding(
                            manifestBuilder,
                            EImportedAnimationDataDomain.HumanoidBody,
                            component.Value,
                            $"HumanoidComponent.ImportedBody.Position.{targetComponent}");
                        var anim = BuildFloatAnim(component.Value, length, looped, sampleRate, GetHumanoidRootMotionPositionComponentScale(component.Key), startTime);
                        builder.AddRootMotionComponentAnimation(targetComponent, anim);
                    }
                }

                if (rootMotionGroups.TryGetValue("rotation", out var rootRotGroup))
                {
                    foreach (var component in rootRotGroup.Components.OrderBy(x => x.Key))
                    {
                        RecordAppliedBinding(
                            manifestBuilder,
                            EImportedAnimationDataDomain.HumanoidBody,
                            component.Value,
                            $"HumanoidComponent.ImportedBody.Rotation.{component.Key}");
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
                            RecordAppliedBinding(
                                manifestBuilder,
                                EImportedAnimationDataDomain.HumanoidIK,
                                component.Value,
                                $"HumanoidIK.{goalName}.Position.{targetComponent}");
                            var anim = BuildFloatAnim(component.Value, length, looped, sampleRate, GetHumanoidBodyPositionComponentScale(component.Key), startTime);
                            builder.AddIKGoalPositionComponentAnimation(goalName, targetComponent, anim);
                        }
                    }

                    if (rotGroup is not null)
                    {
                        foreach (var component in rotGroup.Components.OrderBy(x => x.Key))
                        {
                            RecordAppliedBinding(
                                manifestBuilder,
                                EImportedAnimationDataDomain.HumanoidIK,
                                component.Value,
                                $"HumanoidIK.{goalName}.Rotation.{component.Key}");
                            var anim = BuildFloatAnim(component.Value, length, looped, sampleRate, GetHumanoidBodyRotationComponentScale(component.Key), startTime);
                            builder.AddIKGoalRotationComponentAnimation(goalName, component.Key, anim);
                        }
                    }
                }
            }

            // Track humanoid muscle curve count for clip classification.
            int humanoidMuscleCount = 0;
            int humanoidTranslationDofCount = 0;

            // Build blendshape animations and remaining scalar animations.
            foreach (var c in curves)
            {
                bool nativePackedHumanoid = IsNativePackedHumanoidSemanticBinding(c);

                // Skip ones consumed by transform grouping.
                if ((c.BindingDescriptor is null || nativePackedHumanoid)
                    && TryMapTransformComponent(c.Attribute, out _, out _))
                    continue;

                // Skip ones consumed by IK goal grouping.
                if ((c.BindingDescriptor is null || nativePackedHumanoid)
                    && TryMapIKGoalComponent(c.Attribute, out _, out _, out _))
                    continue;

                // Skip ones consumed by root motion grouping.
                if ((c.BindingDescriptor is null || nativePackedHumanoid)
                    && TryMapRootMotionComponent(c.Attribute, out _, out _))
                    continue;

                string nodePath = NormalizePath(c.Path);

                if (TryParseMaterialBinding(
                    nodePath,
                    c.Attribute,
                    c.ClassId,
                    out SerializedMaterialAnimationBinding materialBinding))
                {
                    RecordAppliedBinding(
                        manifestBuilder,
                        EImportedAnimationDataDomain.GenericProperty,
                        c,
                        $"Material[{materialBinding.MaterialSlot}].{materialBinding.SemanticProperty}");
                    var anim = BuildFloatAnim(c, length, looped, sampleRate, valueScale: 1.0f, startTime);
                    builder.AddMaterialFloatAnimation(materialBinding, anim);
                    materialBindings.Add(materialBinding);
                    continue;
                }

                // Humanoid (muscle) curves: these typically have an empty path and classID 95,
                // and the attribute is a human-readable muscle name like "Neck Nod Down-Up".
                // We map these strings to the underlying int value of EHumanoidValue and forward to HumanoidComponent.SetValue(int, float).
                if (IsHumanoidMuscleCurve(c))
                {
                    if (!TryMapImportedHumanoidAttributeToValue(c.Attribute, out EHumanoidValue humanoidValue))
                    {
                        RecordPreservedBinding(
                            manifestBuilder,
                            EImportedAnimationDataDomain.HumanoidMuscle,
                            c,
                            "The Unity humanoid muscle name is not in the declared native HumanTrait map.");
                        continue;
                    }

                    RecordAppliedBinding(
                        manifestBuilder,
                        EImportedAnimationDataDomain.HumanoidMuscle,
                        c,
                        $"HumanoidComponent.{humanoidValue}");
                    var anim = BuildFloatAnim(c, length, looped, sampleRate, valueScale: 1.0f, startTime);
                    builder.AddHumanoidValueAnimation(humanoidValue, anim);
                    humanoidMuscleCount++;
                    continue;
                }

                if (TryMapHumanoidTranslationDofComponent(c.Attribute, out EHumanoidTranslationDofBone bone, out char sourceComponent))
                {
                    int targetComponent = GetHumanoidTranslationDofTargetComponentIndex(sourceComponent);
                    RecordAppliedBinding(
                        manifestBuilder,
                        EImportedAnimationDataDomain.HumanoidMuscle,
                        c,
                        $"HumanoidComponent.ImportedTranslationDof.{bone}[{targetComponent}]");
                    var anim = BuildFloatAnim(
                        c,
                        length,
                        looped,
                        sampleRate,
                        GetHumanoidRootMotionPositionComponentScale(sourceComponent),
                        startTime);
                    builder.AddHumanoidTranslationDofAnimation(bone, targetComponent, anim);
                    humanoidTranslationDofCount++;
                    continue;
                }

                if (c.Attribute.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    string blendshapeName = c.Attribute["blendShape.".Length..];
                    RecordAppliedBinding(
                        manifestBuilder,
                        EImportedAnimationDataDomain.GenericProperty,
                        c,
                        $"{nodePath}:BlendShape.{blendshapeName}");
                    // Blendshape weights are typically 0..100; engine normalized is 0..1.
                    var anim = BuildFloatAnim(c, length, looped, sampleRate, valueScale: 1.0f / 100.0f, startTime);
                    builder.AddBlendshapeAnimation(nodePath, blendshapeName, anim);
                    continue;
                }

                // Best-effort generic scalar property: attempt to animate a property on the node's Transform.
                // If attribute matches a known Transform property, map it; otherwise store it as a property name.
                if (TryMapScalarTransformProperty(c.Attribute, out string transformPropertyName))
                {
                    RecordAppliedBinding(
                        manifestBuilder,
                        EImportedAnimationDataDomain.GenericProperty,
                        c,
                        $"{nodePath}:Transform.{transformPropertyName}");
                    var anim = BuildFloatAnim(c, length, looped, sampleRate, valueScale: 1.0f, startTime);
                    builder.AddTransformScalarPropertyAnimation(nodePath, transformPropertyName, anim);
                    continue;
                }

                ImportedAnimationBindingDescriptor descriptor = c.BindingDescriptor
                    ?? CreateGenericBindingDescriptor(
                        c.SourceField,
                        nodePath,
                        c.Attribute,
                        c.ClassId,
                        c.Script,
                        EImportedAnimationBindingValueKind.Float,
                        component: GetSerializedComponentIndex(c.Attribute)) with
                    {
                        BindingFlags = c.BindingFlags,
                        BindingSerializedVersion = c.BindingSerializedVersion,
                    };
                genericBindings.Add(descriptor);
                PropAnimFloat genericAnimation = BuildFloatAnim(c, length, looped, sampleRate, valueScale: 1.0f, startTime);
                builder.AddGenericFloatAnimation(descriptor, genericAnimation);
                RecordGenericBinding(manifestBuilder, descriptor);
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
                            RecordAppliedBinding(
                                manifestBuilder,
                                EImportedAnimationDataDomain.GenericTransform,
                                vc,
                                $"{nodePath}:Transform.{kind}.{component.Key}");
                            var anim = BuildFloatAnim(
                                new ScalarCurve(vc.SourceField, vc.SourcePayload, vc.Path, $"{vc.Attribute}.{component.Key}", vc.ClassId, vc.Script, component.Value, vc.PreInfinity, vc.PostInfinity),
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

                            RecordAppliedBinding(
                                manifestBuilder,
                                EImportedAnimationDataDomain.GenericTransform,
                                vc,
                                $"{nodePath}:Transform.Rotation.{component.Key}");
                            var anim = BuildFloatAnim(
                                new ScalarCurve(vc.SourceField, vc.SourcePayload, vc.Path, $"{vc.Attribute}.{component.Key}", vc.ClassId, vc.Script, component.Value, vc.PreInfinity, vc.PostInfinity),
                                length,
                                looped,
                                sampleRate,
                                GetRotationComponentScale(component.Key),
                                startTime);
                            builder.AddTransformComponentAnimation(nodePath, kind, component.Key, anim);
                        }
                    }
                    continue;
                }

                foreach ((char componentName, IReadOnlyList<CurveKey> componentKeys) in vc.ComponentKeys.OrderBy(static x => x.Key))
                {
                    int componentIndex = "xyzw".IndexOf(componentName);
                    if (componentIndex < 0)
                        continue;

                    EImportedAnimationBindingValueKind valueKind = vc.ComponentKeys.Count switch
                    {
                        2 => EImportedAnimationBindingValueKind.Vector2,
                        3 when vc.Attribute.Contains("Euler", StringComparison.OrdinalIgnoreCase) => EImportedAnimationBindingValueKind.Euler,
                        3 => EImportedAnimationBindingValueKind.Vector3,
                        4 when vc.Attribute.Contains("Rotation", StringComparison.OrdinalIgnoreCase) => EImportedAnimationBindingValueKind.Quaternion,
                        _ => EImportedAnimationBindingValueKind.Vector4,
                    };
                    ImportedAnimationBindingDescriptor descriptor = CreateGenericBindingDescriptor(
                        vc.SourceField,
                        nodePath,
                        vc.Attribute,
                        vc.ClassId,
                        vc.Script,
                        valueKind,
                        componentIndex) with
                    {
                        BindingFlags = vc.BindingFlags,
                        BindingSerializedVersion = vc.BindingSerializedVersion,
                    };
                    genericBindings.Add(descriptor);
                    ScalarCurve scalarComponent = new(
                        vc.SourceField,
                        vc.SourcePayload,
                        vc.Path,
                        $"{vc.Attribute}.{componentName}",
                        vc.ClassId,
                        vc.Script,
                        componentKeys,
                        vc.PreInfinity,
                        vc.PostInfinity);
                    builder.AddGenericFloatAnimation(
                        descriptor,
                        BuildFloatAnim(scalarComponent, length, looped, sampleRate, 1.0f, startTime));
                    RecordGenericBinding(manifestBuilder, descriptor);
                }
            }

            foreach (ObjectCurve objectCurve in objectCurves)
            {
                string nodePath = NormalizePath(objectCurve.Path);
                ImportedAnimationBindingDescriptor descriptor = objectCurve.BindingDescriptor
                    ?? CreateGenericBindingDescriptor(
                        objectCurve.SourceField,
                        nodePath,
                        objectCurve.Attribute,
                        objectCurve.ClassId,
                        objectCurve.Script,
                        EImportedAnimationBindingValueKind.ObjectReference,
                        component: -1);
                bool hasMissingReference = objectCurve.Keys.Any(static key =>
                    !key.Value.IsNull
                    && string.IsNullOrWhiteSpace(key.Value.ResolvedAssetPath));
                genericBindings.Add(descriptor);
                builder.AddGenericObjectAnimation(
                    descriptor,
                    BuildObjectAnim(objectCurve, length, looped, startTime));
                manifestBuilder.RecordBinding(
                    EImportedAnimationDataDomain.ObjectReference,
                    hasMissingReference
                        ? EImportedAnimationCapabilityState.PreservedNotExecutable
                        : descriptor.RequiresAdapter
                            ? EImportedAnimationCapabilityState.RequiresRuntimeAdapter
                            : EImportedAnimationCapabilityState.SupportedAndApplied,
                    descriptor.SourceField,
                    descriptor.NodePath,
                    descriptor.Attribute,
                    descriptor.ClassId,
                    descriptor.RequiresAdapter ? "IUnityAnimationBindingAdapter" : "Native object-reference binding",
                    hasMissingReference
                        ? "At least one non-null Unity object key could not be resolved through a project .meta GUID."
                        : descriptor.RequiresAdapter
                            ? "A Unity-only object target requires an explicit IUnityAnimationBindingAdapter on the animated node."
                            : string.Empty);
            }

            // ── Clip classification ──────────────────────────────────────────
            clip.HasMuscleChannels = humanoidMuscleCount + humanoidTranslationDofCount > 0;
            clip.HasRootMotion = rootMotionGroups.Count > 0;
            clip.HasIKGoals = ikGoalGroups.Count > 0;
            clip.ClipKind = humanoidMuscleCount + humanoidTranslationDofCount > 0
                ? EAnimationClipKind.ImportedHumanoidMuscle
                : EAnimationClipKind.GenericTransform;
            clip.SourceMaterialBindings = [.. materialBindings.Distinct()];
                clip.MaterialBindingDiagnostics = [.. materialBindingDiagnostics];
            clip.ImportedGenericBindings = [.. genericBindings.Distinct()];
            ResolveAdditiveReferencePose(filePath, clip, manifestBuilder, activeAdditiveReferences);
            manifestBuilder.SourceIdentity.ImportSettingsSha256 = ComputeImportSettingsHash(
                rootMotionSettings,
                clip.ImportedAdditiveReferencePoseClip?.SourceImportManifest?.SourceIdentity.SourceContentSha256);
            if (settingsMap is not null)
            {
                manifestBuilder.RecordSection(
                    EImportedAnimationDataDomain.RootMotionSettings,
                    rootSettingsExecutable
                        ? EImportedAnimationCapabilityState.SupportedAndApplied
                        : EImportedAnimationCapabilityState.PreservedNotExecutable,
                    "m_AnimationClipSettings",
                    rootSettingsExecutable
                        ? "All authored root-motion settings are executable by the current native evaluator."
                        : rootSettingsDiagnostic,
                    rootSettingsExecutable ? string.Empty : settingsMap.ToString());
            }
            clip.SourceImportManifest = manifestBuilder.Build();

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

            public void AddMaterialFloatAnimation(SerializedMaterialAnimationBinding binding, PropAnimFloat anim)
            {
                var node = GetSceneNodeByPath(binding.NodePath);
                var getComp = GetOrAddMethod(node, "GetComponent", ["ModelComponent"], animatedArgIndex: -1, cacheReturnValue: true);
                var getBinding = GetOrAddMethod(
                    getComp,
                    "GetMaterialAnimationBinding",
                    [binding.MaterialSlot, binding.SourceProperty, binding.Component],
                    animatedArgIndex: -1,
                    cacheReturnValue: true);
                var setter = GetOrAddMethod(getBinding, "SetFloat", [0.0f], animatedArgIndex: 0, cacheReturnValue: false);
                setter.Animation = anim;
            }

            public void AddBlendshapeAnimation(string nodePath, string blendshapeName, PropAnimFloat anim)
            {
                var node = GetSceneNodeByPath(nodePath);
                var getComp = GetOrAddMethod(node, "GetComponent", ["ModelComponent"], animatedArgIndex: -1, cacheReturnValue: true);
                var method = GetOrAddMethod(getComp, "SetBlendShapeWeightNormalized", [blendshapeName, 0.0f, StringComparison.InvariantCultureIgnoreCase], animatedArgIndex: 1, cacheReturnValue: false);
                method.Animation = anim;
            }

            public void AddGenericFloatAnimation(
                ImportedAnimationBindingDescriptor binding,
                PropAnimFloat animation)
            {
                AnimationMember method = GetOrAddMethod(
                    _root,
                    "SetUnityAnimationFloat",
                    [binding, 0.0f],
                    animatedArgIndex: 1,
                    cacheReturnValue: false);
                method.Animation = animation;
            }

            public void AddGenericObjectAnimation(
                ImportedAnimationBindingDescriptor binding,
                PropAnimObject animation)
            {
                AnimationMember method = GetOrAddMethod(
                    _root,
                    "SetUnityAnimationObjectReference",
                    [binding, default(SourceAssetReference)],
                    animatedArgIndex: 1,
                    cacheReturnValue: false);
                method.Animation = animation;
            }

            public void AddHumanoidValueAnimation(EHumanoidValue humanoidValue, PropAnimFloat anim)
            {
                // Humanoid values are applied on the root node's HumanoidComponent.
                // We keep this importer decoupled from the HumanoidComponent assembly by using string-based reflection.
                var getComp = GetOrAddMethod(_sceneNode, "GetComponentInHierarchy", ["HumanoidComponent"], animatedArgIndex: -1, cacheReturnValue: true);
                var method = GetOrAddMethod(getComp, "SetImportedRawValue", [humanoidValue, 0.0f, false], animatedArgIndex: 1, cacheReturnValue: false);
                method.Animation = anim;
            }

            public void AddHumanoidTranslationDofAnimation(
                EHumanoidTranslationDofBone bone,
                int component,
                PropAnimFloat anim)
            {
                var getComp = GetOrAddMethod(_sceneNode, "GetComponentInHierarchy", ["HumanoidComponent"], animatedArgIndex: -1, cacheReturnValue: true);
                var method = GetOrAddMethod(
                    getComp,
                    "SetImportedTranslationDof",
                    [bone, component, 0.0f],
                    animatedArgIndex: 2,
                    cacheReturnValue: false);
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

        private static bool TryMapImportedHumanoidAttributeToValue(string attribute, out EHumanoidValue humanoidValue)
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

            return ImportedHumanoidMuscleMap.TryGetValue(attribute, out humanoidValue);
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
            return TryMapImportedHumanoidAttributeToValue(c.Attribute, out _);
        }

        private static bool TryMapHumanoidTranslationDofComponent(
            string attribute,
            out EHumanoidTranslationDofBone bone,
            out char component)
        {
            bone = default;
            component = default;
            const string suffix = "TDOF.";
            int suffixStart = attribute.IndexOf(suffix, StringComparison.Ordinal);
            if (suffixStart <= 0 || suffixStart + suffix.Length + 1 != attribute.Length)
                return false;

            if (!Enum.TryParse(attribute[..suffixStart], ignoreCase: false, out bone)
                || !Enum.IsDefined(bone))
                return false;

            component = attribute[^1];
            return component is 'x' or 'y' or 'z';
        }

        private static bool TryReadCurveList(
            YamlMappingNode clipMap,
            string key,
            List<ScalarCurve> scalarCurves,
            List<VectorCurve> vectorCurves,
            ImportedAnimationImportManifestBuilder manifestBuilder)
        {
            var seq = GetSequenceOrNull(clipMap, key);
            if (seq is null || seq.Children.Count == 0)
                return false;

            bool addedAny = false;
            foreach (var itemNode in seq.Children)
            {
                if (itemNode is not YamlMappingNode item)
                {
                    manifestBuilder.RecordSection(
                        EImportedAnimationDataDomain.SourceEncoding,
                        EImportedAnimationCapabilityState.Unsupported,
                        key,
                        $"{key} contains a non-mapping curve entry.",
                        itemNode.ToString());
                    continue;
                }

                string? path = GetScalarString(item, "path");
                string? attribute = GetScalarString(item, "attribute");
                int? classId = GetScalarInt(item, "classID");
                SourceAssetReference script = ReadAssetReference(GetMappingOrNull(item, "script"));
                string sourcePayload = item.ToString();

                // Case 1: Float curve item (attribute + curve.m_Curve)
                if (!string.IsNullOrEmpty(attribute))
                {
                    if (TryParseCurveData(item, out var keys, out int scalarPreInfinity, out int scalarPostInfinity))
                    {
                        scalarCurves.Add(new ScalarCurve(key, sourcePayload, path, attribute!, classId, script, keys, scalarPreInfinity, scalarPostInfinity,
                            GetScalarInt(item, "flags") ?? 0, GetScalarInt(item, "serializedVersion") ?? 0));
                        addedAny = true;
                        continue;
                    }
                }

                // Case 2: Vector/quaternion curve item (curve has x/y/z(/w) each containing curve data)
                // These are not present in your current samples, but this keeps the importer usable for more exporter variants.
                if (TryParseVectorCurveData(key, item, out var vecAttribute, out var components, out int vectorPreInfinity, out int vectorPostInfinity))
                {
                    vectorCurves.Add(new VectorCurve(
                        key,
                        sourcePayload,
                        path,
                        vecAttribute,
                        classId,
                        script,
                        components,
                        vectorPreInfinity,
                        vectorPostInfinity,
                        GetScalarInt(item, "flags") ?? 0,
                        GetScalarInt(item, "serializedVersion") ?? 0));
                    addedAny = true;
                    continue;
                }

                manifestBuilder.RecordSection(
                    EImportedAnimationDataDomain.SourceEncoding,
                    EImportedAnimationCapabilityState.Unsupported,
                    key,
                    $"Could not decode curve binding '{attribute ?? "(missing attribute)"}' in {key}.",
                    sourcePayload);
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
                int weightedMode = GetScalarInt(km, "weightedMode") ?? 0;
                float inWeight = GetScalarFloat(km, "inWeight") ?? 0.0f;
                float outWeight = GetScalarFloat(km, "outWeight") ?? 0.0f;
                list.Add(new CurveKey(time, value, inSlope, outSlope, tangentMode, weightedMode, inWeight, outWeight));
            }

            keys = list;
            return true;
        }

        private static bool TryParseVectorCurveData(
            string sourceField,
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

            // Unity's canonical transform lists imply the serialized property from
            // the list name. Third-party exporters may emit an explicit attribute.
            attribute = GetScalarString(item, "attribute")
                ?? GetImpliedVectorAttribute(sourceField);
            if (string.IsNullOrEmpty(attribute))
                return false;

            preInfinity = GetScalarInt(curveMap, "m_PreInfinity") ?? 0;
            postInfinity = GetScalarInt(curveMap, "m_PostInfinity") ?? 0;

            // Canonical Unity YAML stores one vector/quaternion key sequence whose
            // value and tangent fields are mappings. Normalize it to scalar channels
            // so every serialized family uses the same evaluator below.
            if (TryGetSequence(curveMap, "m_Curve", out YamlSequenceNode canonicalKeys)
                && TryParseCanonicalVectorKeys(canonicalKeys, out var canonicalComponents))
            {
                componentKeys = canonicalComponents;
                return true;
            }

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
                    int weightedMode = GetScalarInt(km, "weightedMode") ?? 0;
                    float inWeight = GetScalarFloat(km, "inWeight") ?? 0.0f;
                    float outWeight = GetScalarFloat(km, "outWeight") ?? 0.0f;
                    list.Add(new CurveKey(time, value, inSlope, outSlope, tangentMode, weightedMode, inWeight, outWeight));
                }

                comps[c] = list;
            }

            if (comps.Count == 0)
                return false;

            componentKeys = comps;
            return true;
        }

        private static string GetImpliedVectorAttribute(string sourceField)
            => sourceField switch
            {
                "m_PositionCurves" => "m_LocalPosition",
                "m_ScaleCurves" => "m_LocalScale",
                "m_RotationCurves" or "m_CompressedRotationCurves" => "m_LocalRotation",
                "m_EulerCurves" => "localEulerAnglesRaw",
                _ => string.Empty,
            };

        private static bool TryParseCanonicalVectorKeys(
            YamlSequenceNode keySequence,
            out IReadOnlyDictionary<char, IReadOnlyList<CurveKey>> componentKeys)
        {
            componentKeys = new Dictionary<char, IReadOnlyList<CurveKey>>();
            if (keySequence.Children.Count == 0
                || keySequence.Children[0] is not YamlMappingNode firstKey
                || GetMappingOrNull(firstKey, "value") is not YamlMappingNode firstValue)
                return false;

            char[] components = firstValue.Children.ContainsKey(new YamlScalarNode("w"))
                ? ['x', 'y', 'z', 'w']
                : ['x', 'y', 'z'];
            Dictionary<char, List<CurveKey>> normalized = new(components.Length);
            for (int i = 0; i < components.Length; i++)
                normalized.Add(components[i], new List<CurveKey>(keySequence.Children.Count));

            for (int keyIndex = 0; keyIndex < keySequence.Children.Count; keyIndex++)
            {
                if (keySequence.Children[keyIndex] is not YamlMappingNode key
                    || GetMappingOrNull(key, "value") is not YamlMappingNode value)
                    return false;

                float time = GetScalarFloat(key, "time") ?? 0.0f;
                int scalarTangentMode = GetScalarInt(key, "tangentMode") ?? 0;
                int scalarWeightedMode = GetScalarInt(key, "weightedMode") ?? 0;
                for (int componentIndex = 0; componentIndex < components.Length; componentIndex++)
                {
                    char component = components[componentIndex];
                    if (GetScalarFloat(value, component.ToString()) is not float componentValue)
                        return false;
                    normalized[component].Add(new CurveKey(
                        time,
                        componentValue,
                        GetMappedOrScalarFloat(key, "inSlope", component),
                        GetMappedOrScalarFloat(key, "outSlope", component),
                        GetMappedOrScalarInt(key, "tangentMode", component, scalarTangentMode),
                        GetMappedOrScalarInt(key, "weightedMode", component, scalarWeightedMode),
                        GetMappedOrScalarFloat(key, "inWeight", component),
                        GetMappedOrScalarFloat(key, "outWeight", component)));
                }
            }

            componentKeys = normalized.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<CurveKey>)pair.Value);
            return true;
        }

        private static void NormalizeQuaternionVectorCurves(
            List<VectorCurve> curves,
            ImportedAnimationImportManifestBuilder manifestBuilder)
        {
            for (int i = curves.Count - 1; i >= 0; i--)
            {
                VectorCurve curve = curves[i];
                bool mappedRotation = TryMapVectorAttribute(
                    curve.Attribute,
                    out string kind,
                    out int componentCount)
                    && kind == "rotation"
                    && componentCount == 4;
                if (!mappedRotation && !IsQuaternionVectorAttribute(curve))
                    continue;

                if (!TryNormalizeQuaternionChannels(
                    curve.ComponentKeys,
                    out IReadOnlyDictionary<char, IReadOnlyList<CurveKey>> normalized,
                    out string diagnostic))
                {
                    RecordPreservedBinding(
                        manifestBuilder,
                        EImportedAnimationDataDomain.SourceEncoding,
                        curve,
                        diagnostic);
                    curves.RemoveAt(i);
                    continue;
                }
                curves[i] = curve with { ComponentKeys = normalized };
            }
        }

        private static void NormalizeQuaternionScalarGroups(
            IEnumerable<TransformCurveGroup> groups,
            ImportedAnimationImportManifestBuilder manifestBuilder)
        {
            foreach (TransformCurveGroup group in groups)
            {
                if (group.Kind != "rotation"
                    || !group.Components.TryGetValue('x', out ScalarCurve? x)
                    || !group.Components.TryGetValue('y', out ScalarCurve? y)
                    || !group.Components.TryGetValue('z', out ScalarCurve? z)
                    || !group.Components.TryGetValue('w', out ScalarCurve? w))
                    continue;

                Dictionary<char, IReadOnlyList<CurveKey>> channels = new(4)
                {
                    ['x'] = x.Keys,
                    ['y'] = y.Keys,
                    ['z'] = z.Keys,
                    ['w'] = w.Keys,
                };
                if (!TryNormalizeQuaternionChannels(channels, out var normalized, out string diagnostic))
                {
                    if (TryValidateUnevenQuaternionChannels(
                        x,
                        y,
                        z,
                        w,
                        out string unevenDiagnostic))
                    {
                        manifestBuilder.RecordNotice(
                            EImportedAnimationDataDomain.SourceEncoding,
                            $"{x.SourceField} quaternion components use independently reduced key times; native playback combines and normalizes them after scalar evaluation, and treats opposite-sign samples as the same rotation.");
                        continue;
                    }
                    manifestBuilder.RecordSection(
                        EImportedAnimationDataDomain.SourceEncoding,
                        EImportedAnimationCapabilityState.Unsupported,
                        x.SourceField,
                        string.IsNullOrEmpty(unevenDiagnostic) ? diagnostic : unevenDiagnostic,
                        x.SourcePayload);
                    continue;
                }

                group.Components['x'] = x with { Keys = normalized['x'] };
                group.Components['y'] = y with { Keys = normalized['y'] };
                group.Components['z'] = z with { Keys = normalized['z'] };
                group.Components['w'] = w with { Keys = normalized['w'] };
            }
        }

        private static bool IsQuaternionVectorAttribute(VectorCurve curve)
            => curve.ComponentKeys.Count == 4
                && curve.ComponentKeys.ContainsKey('x')
                && curve.ComponentKeys.ContainsKey('y')
                && curve.ComponentKeys.ContainsKey('z')
                && curve.ComponentKeys.ContainsKey('w')
                && (curve.Attribute.Contains("rotation", StringComparison.OrdinalIgnoreCase)
                    || curve.Attribute.Contains("quaternion", StringComparison.OrdinalIgnoreCase));

        private static bool TryValidateUnevenQuaternionChannels(
            ScalarCurve x,
            ScalarCurve y,
            ScalarCurve z,
            ScalarCurve w,
            out string diagnostic)
        {
            float maxTime = Math.Max(
                Math.Max(GetLastKeyTime(x.Keys), GetLastKeyTime(y.Keys)),
                Math.Max(GetLastKeyTime(z.Keys), GetLastKeyTime(w.Keys)));
            if (!float.IsFinite(maxTime) || maxTime < 0.0f)
            {
                diagnostic = "Quaternion component curves have invalid time bounds.";
                return false;
            }

            PropAnimFloat xAnimation = BuildFloatAnim(x, maxTime, looped: false, fps: 0, valueScale: 1.0f, timeOffsetSeconds: 0.0f);
            PropAnimFloat yAnimation = BuildFloatAnim(y, maxTime, looped: false, fps: 0, valueScale: 1.0f, timeOffsetSeconds: 0.0f);
            PropAnimFloat zAnimation = BuildFloatAnim(z, maxTime, looped: false, fps: 0, valueScale: 1.0f, timeOffsetSeconds: 0.0f);
            PropAnimFloat wAnimation = BuildFloatAnim(w, maxTime, looped: false, fps: 0, valueScale: 1.0f, timeOffsetSeconds: 0.0f);

            SortedSet<float> authoredTimes = [];
            AddKeyTimes(authoredTimes, x.Keys);
            AddKeyTimes(authoredTimes, y.Keys);
            AddKeyTimes(authoredTimes, z.Keys);
            AddKeyTimes(authoredTimes, w.Keys);
            float[] keyTimes = [.. authoredTimes];
            for (int timeIndex = 0; timeIndex < keyTimes.Length; timeIndex++)
            {
                if (!TryValidateQuaternionSample(
                    xAnimation,
                    yAnimation,
                    zAnimation,
                    wAnimation,
                    keyTimes[timeIndex],
                    out diagnostic))
                    return false;
                if (timeIndex + 1 < keyTimes.Length
                    && !TryValidateQuaternionSample(
                        xAnimation,
                        yAnimation,
                        zAnimation,
                        wAnimation,
                        (keyTimes[timeIndex] + keyTimes[timeIndex + 1]) * 0.5f,
                        out diagnostic))
                    return false;
            }

            diagnostic = string.Empty;
            return true;
        }

        private static bool TryValidateQuaternionSample(
            PropAnimFloat xAnimation,
            PropAnimFloat yAnimation,
            PropAnimFloat zAnimation,
            PropAnimFloat wAnimation,
            float time,
            out string diagnostic)
        {
            Quaternion value = new(
                xAnimation.GetValue(time),
                yAnimation.GetValue(time),
                zAnimation.GetValue(time),
                wAnimation.GetValue(time));
            float lengthSquared = value.LengthSquared();
            if (!float.IsFinite(lengthSquared) || lengthSquared <= 1.0e-12f)
            {
                diagnostic = $"Quaternion scalar curves evaluate to a non-finite or zero value at t={time.ToString("R", CultureInfo.InvariantCulture)}.";
                return false;
            }

            // q and -q are exactly the same rotation. Unity clips may switch
            // sign hemispheres at an authored key (commonly the loop endpoint),
            // so sign alone is never an unsupported condition. Runtime targets
            // normalize the combined quartet and blend it shortest-arc.
            diagnostic = string.Empty;
            return true;
        }

        private static float GetLastKeyTime(IReadOnlyList<CurveKey> keys)
            => keys.Count == 0 ? 0.0f : keys.Max(static key => key.Time);

        private static void AddKeyTimes(ISet<float> destination, IReadOnlyList<CurveKey> keys)
        {
            for (int i = 0; i < keys.Count; i++)
                if (float.IsFinite(keys[i].Time))
                    destination.Add(keys[i].Time);
        }

        private static bool TryNormalizeQuaternionChannels(
            IReadOnlyDictionary<char, IReadOnlyList<CurveKey>> channels,
            out IReadOnlyDictionary<char, IReadOnlyList<CurveKey>> normalized,
            out string diagnostic)
        {
            normalized = channels;
            if (!channels.TryGetValue('x', out IReadOnlyList<CurveKey>? x)
                || !channels.TryGetValue('y', out IReadOnlyList<CurveKey>? y)
                || !channels.TryGetValue('z', out IReadOnlyList<CurveKey>? z)
                || !channels.TryGetValue('w', out IReadOnlyList<CurveKey>? w))
            {
                diagnostic = "Quaternion curve does not contain all x/y/z/w channels.";
                return false;
            }
            if (x.Count != y.Count || x.Count != z.Count || x.Count != w.Count)
            {
                diagnostic = "Quaternion component curves have different key counts.";
                return false;
            }

            CurveKey[][] output = [x.ToArray(), y.ToArray(), z.ToArray(), w.ToArray()];
            Quaternion previous = Quaternion.Identity;
            bool hasPrevious = false;
            for (int keyIndex = 0; keyIndex < x.Count; keyIndex++)
            {
                float time = x[keyIndex].Time;
                if (MathF.Abs(y[keyIndex].Time - time) > 0.000001f
                    || MathF.Abs(z[keyIndex].Time - time) > 0.000001f
                    || MathF.Abs(w[keyIndex].Time - time) > 0.000001f)
                {
                    diagnostic = "Quaternion component curves have mismatched key times.";
                    return false;
                }

                Quaternion value = new(
                    x[keyIndex].Value,
                    y[keyIndex].Value,
                    z[keyIndex].Value,
                    w[keyIndex].Value);
                float lengthSquared = value.LengthSquared();
                if (!float.IsFinite(lengthSquared) || lengthSquared <= 1.0e-12f)
                {
                    diagnostic = $"Quaternion key at t={time.ToString("R", CultureInfo.InvariantCulture)} is non-finite or zero-length.";
                    return false;
                }

                float inverseLength = 1.0f / MathF.Sqrt(lengthSquared);
                value *= inverseLength;
                float sign = hasPrevious && Quaternion.Dot(previous, value) < 0.0f ? -1.0f : 1.0f;
                value *= sign;
                float tangentScale = inverseLength * sign;
                float[] values = [value.X, value.Y, value.Z, value.W];
                for (int component = 0; component < 4; component++)
                {
                    CurveKey key = output[component][keyIndex];
                    output[component][keyIndex] = key with
                    {
                        Value = values[component],
                        InSlope = key.InSlope * tangentScale,
                        OutSlope = key.OutSlope * tangentScale,
                    };
                }
                previous = value;
                hasPrevious = true;
            }

            normalized = new Dictionary<char, IReadOnlyList<CurveKey>>(4)
            {
                ['x'] = output[0],
                ['y'] = output[1],
                ['z'] = output[2],
                ['w'] = output[3],
            };
            diagnostic = string.Empty;
            return true;
        }

        private static float GetMappedOrScalarFloat(
            YamlMappingNode parent,
            string key,
            char component)
        {
            if (GetMappingOrNull(parent, key) is YamlMappingNode mapping)
                return GetScalarFloat(mapping, component.ToString()) ?? 0.0f;
            return GetScalarFloat(parent, key) ?? 0.0f;
        }

        private static int GetMappedOrScalarInt(
            YamlMappingNode parent,
            string key,
            char component,
            int fallback)
        {
            if (GetMappingOrNull(parent, key) is YamlMappingNode mapping)
                return GetScalarInt(mapping, component.ToString()) ?? fallback;
            return GetScalarInt(parent, key) ?? fallback;
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

            IReadOnlyList<CurveKey> trimmedKeys = TrimCurveKeys(
                curve.Keys,
                timeOffsetSeconds,
                timeOffsetSeconds + length);
            foreach (CurveKey k in trimmedKeys)
                anim.Keyframes.Add(CreateFloatKeyframe(k, fps, valueScale, timeOffsetSeconds));

            return anim;
        }

        private static PropAnimObject BuildObjectAnim(
            ObjectCurve curve,
            float length,
            bool looped,
            float timeOffsetSeconds)
        {
            PropAnimObject animation = new(length, looped, useKeyframes: true)
            {
                DiscreteValueRounding = PropAnimObject.EDiscreteValueRounding.Floor,
            };
            EKeyframeInfinityMode infinity = looped
                ? EKeyframeInfinityMode.Loop
                : EKeyframeInfinityMode.Once;
            animation.Keyframes.PreInfinityMode = infinity;
            animation.Keyframes.PostInfinityMode = infinity;
            IReadOnlyList<ObjectCurveKey> trimmedKeys = TrimObjectCurveKeys(
                curve.Keys,
                timeOffsetSeconds,
                timeOffsetSeconds + length);
            foreach (ObjectCurveKey key in trimmedKeys)
            {
                animation.Keyframes.Add(new ObjectKeyframe
                {
                    Second = key.Time - timeOffsetSeconds,
                    Value = key.Value,
                });
            }
            return animation;
        }

        private static IReadOnlyList<CurveKey> TrimCurveKeys(
            IReadOnlyList<CurveKey> source,
            float startTime,
            float stopTime)
        {
            if (source.Count == 0)
                return source;

            List<CurveKey> keys = [.. source.OrderBy(static key => key.Time)];
            if (!float.IsFinite(startTime) || !float.IsFinite(stopTime) || stopTime < startTime)
                return keys;

            SplitCurveAt(keys, startTime);
            if (stopTime > startTime)
                SplitCurveAt(keys, stopTime);
            return keys
                .Where(key => key.Time >= startTime - 0.000001f && key.Time <= stopTime + 0.000001f)
                .ToArray();
        }

        private static void SplitCurveAt(List<CurveKey> keys, float splitTime)
        {
            int rightIndex = keys.FindIndex(key => key.Time >= splitTime - 0.000001f);
            if (rightIndex <= 0 || rightIndex >= keys.Count)
                return;
            CurveKey right = keys[rightIndex];
            if (MathF.Abs(right.Time - splitTime) <= 0.000001f)
                return;

            CurveKey left = keys[rightIndex - 1];
            if (splitTime <= left.Time || splitTime >= right.Time)
                return;

            SplitSourceCurveSegment(
                left,
                right,
                splitTime,
                out CurveKey adjustedLeft,
                out CurveKey boundary,
                out CurveKey adjustedRight);
            keys[rightIndex - 1] = adjustedLeft;
            keys[rightIndex] = adjustedRight;
            keys.Insert(rightIndex, boundary);
        }

        private static void SplitSourceCurveSegment(
            CurveKey left,
            CurveKey right,
            float splitTime,
            out CurveKey adjustedLeft,
            out CurveKey boundary,
            out CurveKey adjustedRight)
        {
            float duration = right.Time - left.Time;
            if (left.OutInterpType == EVectorInterpType.Step || duration <= 0.0f)
            {
                adjustedLeft = left with { OutSlope = float.PositiveInfinity };
                boundary = new CurveKey(
                    splitTime,
                    left.Value,
                    float.PositiveInfinity,
                    float.PositiveInfinity,
                    CombinedTangentMode: 0,
                    WeightedMode: 0,
                    InWeight: 1.0f / 3.0f,
                    OutWeight: 1.0f / 3.0f);
                adjustedRight = right with { InSlope = float.PositiveInfinity };
                return;
            }

            float normalizedTime = Math.Clamp((splitTime - left.Time) / duration, 0.0f, 1.0f);
            float outWeight = (left.WeightedMode & 2) != 0 ? left.OutWeight : 1.0f / 3.0f;
            float inWeight = (right.WeightedMode & 1) != 0 ? right.InWeight : 1.0f / 3.0f;
            Vector2 p0 = new(0.0f, left.Value);
            Vector2 p1 = new(outWeight, left.Value + left.OutSlope * duration * outWeight);
            Vector2 p2 = new(1.0f - inWeight, right.Value - right.InSlope * duration * inWeight);
            Vector2 p3 = new(1.0f, right.Value);
            float parameter = InvertSourceBezierTime(normalizedTime, p1.X, p2.X);

            Vector2 a = Vector2.Lerp(p0, p1, parameter);
            Vector2 b = Vector2.Lerp(p1, p2, parameter);
            Vector2 c = Vector2.Lerp(p2, p3, parameter);
            Vector2 d = Vector2.Lerp(a, b, parameter);
            Vector2 e = Vector2.Lerp(b, c, parameter);
            Vector2 point = Vector2.Lerp(d, e, parameter);

            float leftDuration = Math.Max(point.X - p0.X, 0.0000001f);
            float rightDuration = Math.Max(p3.X - point.X, 0.0000001f);
            adjustedLeft = left with
            {
                OutSlope = GetBezierHandleSlope(p0, a, duration, left.OutSlope),
                OutWeight = Math.Clamp((a.X - p0.X) / leftDuration, 0.0f, 1.0f),
                WeightedMode = (left.WeightedMode & 1) | 2,
                CombinedTangentMode = left.CombinedTangentMode & ~(0xF << 5),
            };
            boundary = new CurveKey(
                splitTime,
                point.Y,
                GetBezierHandleSlope(d, point, duration, left.OutSlope),
                GetBezierHandleSlope(point, e, duration, right.InSlope),
                CombinedTangentMode: 0,
                WeightedMode: 3,
                InWeight: Math.Clamp((point.X - d.X) / leftDuration, 0.0f, 1.0f),
                OutWeight: Math.Clamp((e.X - point.X) / rightDuration, 0.0f, 1.0f));
            adjustedRight = right with
            {
                InSlope = GetBezierHandleSlope(c, p3, duration, right.InSlope),
                InWeight = Math.Clamp((p3.X - c.X) / rightDuration, 0.0f, 1.0f),
                WeightedMode = (right.WeightedMode & 2) | 1,
                CombinedTangentMode = right.CombinedTangentMode & ~(0xF << 1),
            };
        }

        private static float GetBezierHandleSlope(
            Vector2 from,
            Vector2 to,
            float sourceDuration,
            float fallback)
        {
            float deltaX = to.X - from.X;
            return MathF.Abs(deltaX) <= 0.0000001f
                ? fallback
                : (to.Y - from.Y) / (deltaX * sourceDuration);
        }

        private static float InvertSourceBezierTime(float target, float x1, float x2)
        {
            float lower = 0.0f;
            float upper = 1.0f;
            float parameter = target;
            for (int iteration = 0; iteration < 16; iteration++)
            {
                float value = EvaluateSourceBezier(0.0f, x1, x2, 1.0f, parameter);
                float error = value - target;
                if (MathF.Abs(error) <= 0.0000001f)
                    break;
                if (error < 0.0f)
                    lower = parameter;
                else
                    upper = parameter;

                float inverse = 1.0f - parameter;
                float derivative = 3.0f * inverse * inverse * x1
                    + 6.0f * inverse * parameter * (x2 - x1)
                    + 3.0f * parameter * parameter * (1.0f - x2);
                float candidate = MathF.Abs(derivative) > 0.0000001f
                    ? parameter - error / derivative
                    : float.NaN;
                parameter = float.IsFinite(candidate) && candidate > lower && candidate < upper
                    ? candidate
                    : (lower + upper) * 0.5f;
            }
            return Math.Clamp(parameter, 0.0f, 1.0f);
        }

        private static float EvaluateSourceBezier(float p0, float p1, float p2, float p3, float parameter)
        {
            float inverse = 1.0f - parameter;
            return inverse * inverse * inverse * p0
                + 3.0f * inverse * inverse * parameter * p1
                + 3.0f * inverse * parameter * parameter * p2
                + parameter * parameter * parameter * p3;
        }

        private static IReadOnlyList<ObjectCurveKey> TrimObjectCurveKeys(
            IReadOnlyList<ObjectCurveKey> source,
            float startTime,
            float stopTime)
        {
            if (source.Count == 0)
                return source;

            ObjectCurveKey[] ordered = [.. source.OrderBy(static key => key.Time).ThenBy(static key => key.SourceOrder)];
            if (!float.IsFinite(startTime) || !float.IsFinite(stopTime) || stopTime < startTime)
                return ordered;

            List<ObjectCurveKey> trimmed = [];
            ObjectCurveKey boundary = ordered[0] with { Time = startTime };
            for (int i = 0; i < ordered.Length; i++)
            {
                if (ordered[i].Time > startTime + 0.000001f)
                    break;
                boundary = ordered[i] with { Time = startTime };
            }
            trimmed.Add(boundary);
            for (int i = 0; i < ordered.Length; i++)
            {
                ObjectCurveKey key = ordered[i];
                if (key.Time <= startTime + 0.000001f || key.Time > stopTime + 0.000001f)
                    continue;
                trimmed.Add(key);
            }
            return trimmed;
        }

        private static FloatKeyframe CreateFloatKeyframe(CurveKey key, int fps, float valueScale, float timeOffsetSeconds)
        {
            // BuildFloatAnim has split any curve segment crossed by the clip trim
            // bounds, including weighted Bezier handles, so all retained keys are
            // legal non-negative track times without changing the authored curve.
            float normalizedTime = key.Time - timeOffsetSeconds;
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
            kf.WeightedMode = (EKeyframeWeightedMode)key.WeightedMode;
            kf.InWeight = key.InWeight;
            kf.OutWeight = key.OutWeight;

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

        private static EKeyframeInfinityMode MapInfinityMode(int sourceInfinity)
            => sourceInfinity switch
            {
                0 => EKeyframeInfinityMode.Default,
                1 => EKeyframeInfinityMode.Once,
                2 => EKeyframeInfinityMode.Loop,
                4 => EKeyframeInfinityMode.PingPong,
                8 => EKeyframeInfinityMode.ClampForever,
                _ => throw new InvalidDataException($"Unsupported Unity infinity mode {sourceInfinity}."),
            };

        private static void NormalizeDefaultInfinityModes(
            List<ScalarCurve> scalarCurves,
            List<VectorCurve> vectorCurves,
            int sourceWrapMode,
            bool looped)
        {
            int effectiveDefault = sourceWrapMode == 0
                ? (looped ? 2 : 1)
                : sourceWrapMode;

            for (int i = 0; i < scalarCurves.Count; i++)
            {
                ScalarCurve curve = scalarCurves[i];
                scalarCurves[i] = curve with
                {
                    PreInfinity = curve.PreInfinity == 0 ? effectiveDefault : curve.PreInfinity,
                    PostInfinity = curve.PostInfinity == 0 ? effectiveDefault : curve.PostInfinity,
                };
            }

            for (int i = 0; i < vectorCurves.Count; i++)
            {
                VectorCurve curve = vectorCurves[i];
                vectorCurves[i] = curve with
                {
                    PreInfinity = curve.PreInfinity == 0 ? effectiveDefault : curve.PreInfinity,
                    PostInfinity = curve.PostInfinity == 0 ? effectiveDefault : curve.PostInfinity,
                };
            }
        }

        private static float GetMaxTime(
            List<ScalarCurve> scalarCurves,
            List<VectorCurve> vectorCurves,
            List<ObjectCurve> objectCurves,
            ImportedAnimationEvent[] animationEvents)
        {
            float max = 0.0f;
            foreach (var c in scalarCurves)
                foreach (var k in c.Keys)
                    max = Math.Max(max, k.Time);

            foreach (var vc in vectorCurves)
                foreach (var ks in vc.ComponentKeys.Values)
                    foreach (var k in ks)
                        max = Math.Max(max, k.Time);

            foreach (ObjectCurve curve in objectCurves)
                foreach (ObjectCurveKey key in curve.Keys)
                    max = Math.Max(max, key.Time);

            foreach (ImportedAnimationEvent animationEvent in animationEvents)
                max = Math.Max(max, animationEvent.Time);

            return max;
        }

        private static bool TryParseMaterialBinding(
            string nodePath,
            string attribute,
            int? classId,
            out SerializedMaterialAnimationBinding binding)
        {
            binding = null!;
            Match match = Regex.Match(
                attribute,
                @"^(?:(?:m_)?materials?\.Array\.data\[(?<slot>\d+)\]|material(?:\[(?<slot2>\d+)\])?)\.(?<property>_[A-Za-z0-9_]+?)(?:\.(?<component>[rgbaxyzw]))?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                return false;

            int slot = 0;
            string slotText = match.Groups["slot"].Success
                ? match.Groups["slot"].Value
                : match.Groups["slot2"].Value;
            if (slotText.Length > 0)
                int.TryParse(slotText, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot);

            string property = match.Groups["property"].Value;
            int component = match.Groups["component"].Success
                ? "rgbaxyzw".IndexOf(match.Groups["component"].Value[0])
                : -1;
            if (component >= 4)
                component -= 4;

            SerializedMaterialAnimationValueKind kind = component >= 0
                ? (property.Contains("Color", StringComparison.OrdinalIgnoreCase)
                    ? SerializedMaterialAnimationValueKind.Color
                    : SerializedMaterialAnimationValueKind.Vector)
                : (property.Contains("Mode", StringComparison.OrdinalIgnoreCase) ||
                   property.Contains("Enabled", StringComparison.OrdinalIgnoreCase) ||
                   property.Contains("Toggle", StringComparison.OrdinalIgnoreCase)
                    ? SerializedMaterialAnimationValueKind.Int
                    : SerializedMaterialAnimationValueKind.Float);

            binding = new SerializedMaterialAnimationBinding(
                nodePath,
                attribute,
                property,
                property,
                slot,
                component,
                kind,
                classId);
            return true;
        }

        private static void ReadMaterialObjectReferenceBindings(
            YamlMappingNode clipMap,
            ICollection<SerializedMaterialAnimationBinding> bindings,
            ICollection<string> diagnostics)
        {
            YamlSequenceNode? sequence = GetSequenceOrNull(clipMap, "m_PPtrCurves");
            if (sequence is null)
                return;

            foreach (YamlNode node in sequence.Children)
            {
                if (node is not YamlMappingNode item)
                    continue;

                string attribute = GetScalarString(item, "attribute") ?? string.Empty;
                string nodePath = NormalizePath(GetScalarString(item, "path"));
                int? classId = GetScalarInt(item, "classID");
                if (!TryParseMaterialBinding(nodePath, attribute, classId, out SerializedMaterialAnimationBinding parsed))
                    continue;

                SerializedMaterialAnimationValueKind kind =
                    parsed.SourceProperty.Contains("Tex", StringComparison.OrdinalIgnoreCase) ||
                    parsed.SourceProperty.Contains("Map", StringComparison.OrdinalIgnoreCase)
                        ? SerializedMaterialAnimationValueKind.Texture
                        : SerializedMaterialAnimationValueKind.ObjectReference;
                bindings.Add(parsed with { ValueKind = kind });
                diagnostics.Add(
                    $"Imported Unity object-reference curve '{attribute}' through the typed runtime resolver.");
            }
        }

        private static List<ObjectCurve> ReadObjectReferenceCurves(
            YamlMappingNode clipMap,
            string clipFilePath,
            ImportedAnimationImportManifestBuilder manifestBuilder)
        {
            YamlSequenceNode? sequence = GetSequenceOrNull(clipMap, "m_PPtrCurves");
            if (sequence is null || sequence.Children.Count == 0)
                return [];

            List<ObjectCurve> curves = new(sequence.Children.Count);
            HashSet<string> referencedGuids = new(StringComparer.OrdinalIgnoreCase);
            for (int curveIndex = 0; curveIndex < sequence.Children.Count; curveIndex++)
            {
                if (sequence.Children[curveIndex] is not YamlMappingNode item)
                {
                    manifestBuilder.RecordSection(
                        EImportedAnimationDataDomain.ObjectReference,
                        EImportedAnimationCapabilityState.Unsupported,
                        $"m_PPtrCurves[{curveIndex}]",
                        "Object-reference curve entry is not a mapping.",
                        sequence.Children[curveIndex].ToString());
                    continue;
                }

                YamlSequenceNode? keys = GetSequenceOrNull(item, "curve");
                if (keys is null && GetMappingOrNull(item, "curve") is YamlMappingNode curveMap)
                    keys = GetSequenceOrNull(curveMap, "m_Curve");
                string attribute = GetScalarString(item, "attribute") ?? string.Empty;
                if (keys is null || string.IsNullOrWhiteSpace(attribute))
                {
                    manifestBuilder.RecordSection(
                        EImportedAnimationDataDomain.ObjectReference,
                        EImportedAnimationCapabilityState.Unsupported,
                        $"m_PPtrCurves[{curveIndex}]",
                        "Object-reference curve is missing its key sequence or attribute.",
                        item.ToString());
                    continue;
                }

                List<ObjectCurveKey> objectKeys = new(keys.Children.Count);
                for (int keyIndex = 0; keyIndex < keys.Children.Count; keyIndex++)
                {
                    if (keys.Children[keyIndex] is not YamlMappingNode keyMap)
                        continue;
                    SourceAssetReference reference = ReadAssetReference(GetMappingOrNull(keyMap, "value"));
                    if (!string.IsNullOrWhiteSpace(reference.Guid))
                        referencedGuids.Add(reference.Guid);
                    objectKeys.Add(new ObjectCurveKey(
                        GetScalarFloat(keyMap, "time") ?? 0.0f,
                        reference,
                        keyIndex));
                }

                curves.Add(new ObjectCurve(
                    "m_PPtrCurves",
                    item.ToString(),
                    GetScalarString(item, "path"),
                    attribute,
                    GetScalarInt(item, "classID"),
                    ReadAssetReference(GetMappingOrNull(item, "script")),
                    objectKeys));
            }

            Dictionary<string, string> resolvedPaths = ResolveSourceGuidPaths(clipFilePath, referencedGuids);
            for (int curveIndex = 0; curveIndex < curves.Count; curveIndex++)
            {
                ObjectCurve curve = curves[curveIndex];
                ObjectCurveKey[] resolvedKeys = new ObjectCurveKey[curve.Keys.Count];
                for (int keyIndex = 0; keyIndex < curve.Keys.Count; keyIndex++)
                {
                    ObjectCurveKey key = curve.Keys[keyIndex];
                    SourceAssetReference reference = key.Value;
                    if (resolvedPaths.TryGetValue(reference.Guid, out string? resolvedPath))
                        reference = reference with { ResolvedAssetPath = resolvedPath };
                    resolvedKeys[keyIndex] = key with { Value = reference };
                }
                curves[curveIndex] = curve with { Keys = resolvedKeys };
            }

            return curves;
        }

        private static Dictionary<string, string> ResolveSourceGuidPaths(
            string clipFilePath,
            HashSet<string> requestedGuids)
        {
            Dictionary<string, string> resolved = new(StringComparer.OrdinalIgnoreCase);
            if (requestedGuids.Count == 0)
                return resolved;

            DirectoryInfo? directory = new FileInfo(Path.GetFullPath(clipFilePath)).Directory;
            while (directory is not null
                && !directory.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase))
                directory = directory.Parent;
            if (directory?.Parent is not DirectoryInfo projectRoot)
                return resolved;

            try
            {
                foreach (string metaPath in Directory.EnumerateFiles(directory.FullName, "*.meta", SearchOption.AllDirectories))
                {
                    string? guid = null;
                    foreach (string line in File.ReadLines(metaPath).Take(24))
                    {
                        ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
                        if (!trimmed.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
                            continue;
                        guid = trimmed[5..].Trim().ToString();
                        break;
                    }

                    if (guid is null || !requestedGuids.Contains(guid))
                        continue;

                    string assetPath = metaPath[..^".meta".Length];
                    resolved[guid] = Path.GetRelativePath(projectRoot.FullName, assetPath).Replace('\\', '/');
                    if (resolved.Count == requestedGuids.Count)
                        break;
                }
            }
            catch (IOException)
            {
                // A missing/locked .meta remains an explicit unresolved-reference
                // capability diagnostic on the affected object curve.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return resolved;
        }

        private static ImportedAnimationBindingDescriptor CreateGenericBindingDescriptor(
            string sourceField,
            string nodePath,
            string attribute,
            int? classId,
            SourceAssetReference script,
            EImportedAnimationBindingValueKind valueKind,
            int component)
            => new()
            {
                SourceField = sourceField,
                NodePath = nodePath,
                Attribute = attribute,
                ClassId = classId,
                Script = script,
                ValueKind = valueKind,
                Component = component,
                RequiresAdapter = RequiresExplicitBindingAdapter(classId, script),
            };

        private static bool RequiresExplicitBindingAdapter(int? classId, SourceAssetReference script)
        {
            if (!script.IsNull || classId is 114)
                return true;
            return classId is not (1 or 4 or 20 or 23 or 33 or 54 or 81 or 82 or 108 or 137 or 224);
        }

        private static int GetSerializedComponentIndex(string attribute)
        {
            int separator = attribute.LastIndexOf('.');
            if (separator < 0 || separator == attribute.Length - 1)
                return -1;
            return char.ToLowerInvariant(attribute[^1]) switch
            {
                'x' or 'r' => 0,
                'y' or 'g' => 1,
                'z' or 'b' => 2,
                'w' or 'a' => 3,
                _ => -1,
            };
        }

        private static void RecordGenericBinding(
            ImportedAnimationImportManifestBuilder manifestBuilder,
            ImportedAnimationBindingDescriptor descriptor)
            => manifestBuilder.RecordBinding(
                EImportedAnimationDataDomain.GenericProperty,
                descriptor.RequiresAdapter
                    ? EImportedAnimationCapabilityState.RequiresRuntimeAdapter
                    : EImportedAnimationCapabilityState.SupportedAndApplied,
                descriptor.SourceField,
                descriptor.NodePath,
                descriptor.Attribute,
                descriptor.ClassId,
                descriptor.RequiresAdapter
                    ? "IUnityAnimationBindingAdapter"
                    : "Native typed serialized-property resolver",
                descriptor.RequiresAdapter
                    ? "The Unity-only component/property is preserved and requires an explicit IUnityAnimationBindingAdapter on the animated node."
                    : string.Empty);

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

        private static void RecordAppliedBinding(
            ImportedAnimationImportManifestBuilder manifestBuilder,
            EImportedAnimationDataDomain domain,
            ScalarCurve curve,
            string runtimeTarget)
            => manifestBuilder.RecordBinding(
                domain,
                EImportedAnimationCapabilityState.SupportedAndApplied,
                curve.SourceField,
                NormalizePath(curve.Path),
                curve.Attribute,
                curve.ClassId,
                runtimeTarget);

        private static void RecordAppliedBinding(
            ImportedAnimationImportManifestBuilder manifestBuilder,
            EImportedAnimationDataDomain domain,
            VectorCurve curve,
            string runtimeTarget)
            => manifestBuilder.RecordBinding(
                domain,
                EImportedAnimationCapabilityState.SupportedAndApplied,
                curve.SourceField,
                NormalizePath(curve.Path),
                curve.Attribute,
                curve.ClassId,
                runtimeTarget);

        private static void RecordPreservedBinding(
            ImportedAnimationImportManifestBuilder manifestBuilder,
            EImportedAnimationDataDomain domain,
            ScalarCurve curve,
            string diagnostic)
        {
            manifestBuilder.RecordBinding(
                domain,
                EImportedAnimationCapabilityState.PreservedNotExecutable,
                curve.SourceField,
                NormalizePath(curve.Path),
                curve.Attribute,
                curve.ClassId,
                runtimeTarget: string.Empty,
                diagnostic);
            manifestBuilder.PreservePayload(
                domain,
                $"{curve.SourceField}:{NormalizePath(curve.Path)}:{curve.Attribute}",
                curve.SourcePayload);
        }

        private static void RecordPreservedBinding(
            ImportedAnimationImportManifestBuilder manifestBuilder,
            EImportedAnimationDataDomain domain,
            VectorCurve curve,
            string diagnostic)
        {
            manifestBuilder.RecordBinding(
                domain,
                EImportedAnimationCapabilityState.PreservedNotExecutable,
                curve.SourceField,
                NormalizePath(curve.Path),
                curve.Attribute,
                curve.ClassId,
                runtimeTarget: string.Empty,
                diagnostic);
            manifestBuilder.PreservePayload(
                domain,
                $"{curve.SourceField}:{NormalizePath(curve.Path)}:{curve.Attribute}",
                curve.SourcePayload);
        }

        private static void RemoveUnsupportedCurveEncodings(
            List<ScalarCurve> curves,
            ImportedAnimationImportManifestBuilder manifestBuilder)
        {
            for (int i = curves.Count - 1; i >= 0; i--)
            {
                ScalarCurve curve = curves[i];
                if (!TryGetUnsupportedCurveEncoding(curve.Keys, curve.PreInfinity, curve.PostInfinity, out string diagnostic))
                    continue;

                RecordPreservedBinding(
                    manifestBuilder,
                    EImportedAnimationDataDomain.SourceEncoding,
                    curve,
                    diagnostic);
                curves.RemoveAt(i);
            }
        }

        private static void RemoveUnsupportedCurveEncodings(
            List<VectorCurve> curves,
            ImportedAnimationImportManifestBuilder manifestBuilder)
        {
            for (int i = curves.Count - 1; i >= 0; i--)
            {
                VectorCurve curve = curves[i];
                string diagnostic = string.Empty;
                foreach (IReadOnlyList<CurveKey> keys in curve.ComponentKeys.Values)
                {
                    if (TryGetUnsupportedCurveEncoding(keys, curve.PreInfinity, curve.PostInfinity, out diagnostic))
                        break;
                }

                if (string.IsNullOrEmpty(diagnostic))
                    continue;

                RecordPreservedBinding(
                    manifestBuilder,
                    EImportedAnimationDataDomain.SourceEncoding,
                    curve,
                    diagnostic);
                curves.RemoveAt(i);
            }
        }

        private static bool TryGetUnsupportedCurveEncoding(
            IReadOnlyList<CurveKey> keys,
            int preInfinity,
            int postInfinity,
            out string diagnostic)
        {
            if (!IsCurrentlyExecutableInfinityMode(preInfinity)
                || !IsCurrentlyExecutableInfinityMode(postInfinity))
            {
                diagnostic = $"Unity infinity modes pre={preInfinity}, post={postInfinity} are not both implemented.";
                return true;
            }

            for (int i = 0; i < keys.Count; i++)
            {
                CurveKey key = keys[i];
                if ((key.WeightedMode & ~3) != 0)
                {
                    diagnostic = $"Weighted tangent key at t={key.Time.ToString("R", CultureInfo.InvariantCulture)} has invalid mode {key.WeightedMode}.";
                    return true;
                }

                if ((key.WeightedMode & 1) != 0 && (!float.IsFinite(key.InWeight) || key.InWeight < 0.0f || key.InWeight > 1.0f))
                {
                    diagnostic = $"Weighted tangent key at t={key.Time.ToString("R", CultureInfo.InvariantCulture)} has invalid inWeight {key.InWeight.ToString("R", CultureInfo.InvariantCulture)}.";
                    return true;
                }

                if ((key.WeightedMode & 2) != 0 && (!float.IsFinite(key.OutWeight) || key.OutWeight < 0.0f || key.OutWeight > 1.0f))
                {
                    diagnostic = $"Weighted tangent key at t={key.Time.ToString("R", CultureInfo.InvariantCulture)} has invalid outWeight {key.OutWeight.ToString("R", CultureInfo.InvariantCulture)}.";
                    return true;
                }
            }

            diagnostic = string.Empty;
            return false;
        }

        private static bool IsCurrentlyExecutableInfinityMode(int mode)
            => mode is 0 or 1 or 2 or 4 or 8;

        private static string ComputeImportSettingsHash(
            ImportedHumanoidClipRootMotionSettings? settings,
            string? additiveReferenceContentSha256)
        {
            string rootSettings = settings is null
                ? "none"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{settings.AdditiveReferencePoseClip.FileId}|{settings.AdditiveReferencePoseClip.Guid}|{settings.AdditiveReferencePoseClip.Type}|{settings.AdditiveReferencePoseTime:R}|{settings.HasAdditiveReferencePose}|{settings.StartTime:R}|{settings.StopTime:R}|{settings.OrientationOffsetY:R}|{settings.Level:R}|{settings.CycleOffset:R}|{settings.LoopTime}|{settings.LoopPose}|{settings.BakeOrientationIntoPose}|{settings.BakePositionYIntoPose}|{settings.BakePositionXZIntoPose}|{settings.KeepOriginalOrientation}|{settings.KeepOriginalPositionY}|{settings.KeepOriginalPositionXZ}|{settings.HeightFromFeet}|{settings.Mirror}");
            string canonical = string.Create(
                CultureInfo.InvariantCulture,
                $"manifest={ImportedAnimationImportManifest.CurrentSchemaVersion}|capability={ImportedAnimationImportCapabilityContract.CurrentVersion}|coordinate={ImportedAnimationCoordinateContract.CurrentContractId}|constrained={Constrained}|lerpConstrained={LerpConstrained}|humanoidIK={ImportHumanoidIKGoalCurves}|humanoidRoot={ImportHumanoidRootMotionCurves}|rootSettings={rootSettings}|additiveReferenceContent={additiveReferenceContentSha256 ?? "none"}");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }

        private static bool IsRecognizedSerializedVersion(int serializedVersion)
            // Unity 2022 may serialize otherwise equivalent editable curve clips as version 7
            // after changing only object-reference header fields. The curve, humanoid root,
            // IK, and clip-settings domains consumed above retain the version 6 layout.
            => ImportedAnimationImportCapabilityContract.SupportsSerializedVersion(serializedVersion);

        private static ImportedAnimationClipMetadata ReadClipMetadata(
            YamlMappingNode clipMap,
            int sampleRate,
            int sourceWrapMode)
        {
            YamlMappingNode? bounds = GetMappingOrNull(clipMap, "m_Bounds");
            return new ImportedAnimationClipMetadata
            {
                SampleRate = sampleRate,
                WrapMode = (EImportedAnimationWrapMode)sourceWrapMode,
                Legacy = (GetScalarInt(clipMap, "m_Legacy") ?? 0) != 0,
                Compressed = (GetScalarInt(clipMap, "m_Compressed") ?? 0) != 0,
                UseHighQualityCurve = (GetScalarInt(clipMap, "m_UseHighQualityCurve") ?? 0) != 0,
                HasGenericRootTransform = (GetScalarInt(clipMap, "m_HasGenericRootTransform") ?? 0) != 0,
                HasMotionFloatCurves = (GetScalarInt(clipMap, "m_HasMotionFloatCurves") ?? 0) != 0,
                GenerateMotionCurves = (GetScalarInt(clipMap, "m_GenerateMotionCurves") ?? 0) != 0,
                BoundsCenter = ReadVector3(GetMappingOrNull(bounds, "m_Center")),
                BoundsExtents = ReadVector3(GetMappingOrNull(bounds, "m_Extent")),
            };
        }

        private static ImportedAnimationEvent[] ReadAnimationEvents(
            YamlMappingNode clipMap,
            string clipFilePath,
            float startTimeSeconds,
            float stopTimeSeconds,
            ImportedAnimationImportManifestBuilder manifestBuilder)
        {
            YamlSequenceNode? sequence = GetSequenceOrNull(clipMap, "m_Events");
            if (sequence is null || sequence.Children.Count == 0)
                return [];

            List<ImportedAnimationEvent> events = new(sequence.Children.Count);
            HashSet<string> referencedGuids = new(StringComparer.OrdinalIgnoreCase);
            int trimmedEventCount = 0;
            int discardedCallbackCount = 0;
            bool hasTrimRange = stopTimeSeconds > startTimeSeconds;
            for (int sourceOrder = 0; sourceOrder < sequence.Children.Count; sourceOrder++)
            {
                if (sequence.Children[sourceOrder] is not YamlMappingNode item)
                {
                    manifestBuilder.RecordSection(
                        EImportedAnimationDataDomain.AnimationEvent,
                        EImportedAnimationCapabilityState.Unsupported,
                        $"m_Events[{sourceOrder}]",
                        "Animation event entry is not a mapping.",
                        sequence.Children[sourceOrder].ToString());
                    continue;
                }

                string functionName = GetScalarString(item, "functionName") ?? string.Empty;
                float sourceEventTime = GetScalarFloat(item, "time") ?? 0.0f;
                int messageOptions = GetScalarInt(item, "messageOptions") ?? 0;
                if (string.IsNullOrWhiteSpace(functionName)
                    || !float.IsFinite(sourceEventTime)
                    || messageOptions is not (0 or 1))
                {
                    manifestBuilder.RecordSection(
                        EImportedAnimationDataDomain.AnimationEvent,
                        EImportedAnimationCapabilityState.Unsupported,
                        $"m_Events[{sourceOrder}]",
                        "Animation event has an invalid function name, time, or message option.",
                        item.ToString());
                    continue;
                }

                const float boundaryTolerance = 0.000001f;
                if (hasTrimRange
                    && (sourceEventTime < startTimeSeconds - boundaryTolerance
                        || sourceEventTime > stopTimeSeconds + boundaryTolerance))
                {
                    trimmedEventCount++;
                    continue;
                }

                float eventTime = sourceEventTime - startTimeSeconds;
                if (hasTrimRange)
                    eventTime = Math.Clamp(eventTime, 0.0f, stopTimeSeconds - startTimeSeconds);

                if (!ImportedAnimationEventAllowlist.TryMap(functionName, out string eventId))
                {
                    discardedCallbackCount++;
                    string boundedFunctionName = functionName.Length <= 128
                        ? functionName
                        : functionName[..128];
                    manifestBuilder.RecordSection(
                        EImportedAnimationDataDomain.AnimationEvent,
                        EImportedAnimationCapabilityState.IntentionallyDiscarded,
                        $"m_Events[{sourceOrder}]",
                        $"Discarded source callback '{boundedFunctionName}' because it is not mapped to an explicit native animation event identifier.",
                        item.ToString());
                    continue;
                }

                SourceAssetReference objectReference = ReadAssetReference(
                    GetMappingOrNull(item, "objectReferenceParameter"));
                if (!string.IsNullOrWhiteSpace(objectReference.Guid))
                    referencedGuids.Add(objectReference.Guid);

                events.Add(new ImportedAnimationEvent
                {
                    Time = eventTime,
                    EventId = eventId,
                    StringParameter = GetScalarString(item, "data")
                        ?? GetScalarString(item, "stringParameter")
                        ?? string.Empty,
                    FloatParameter = GetScalarFloat(item, "floatParameter") ?? 0.0f,
                    IntParameter = GetScalarInt(item, "intParameter") ?? 0,
                    ObjectReferenceParameter = objectReference,
                    MessageOptions = (EImportedAnimationEventMessageOptions)messageOptions,
                    SourceOrder = sourceOrder,
                });
            }

            Dictionary<string, string> resolvedPaths = ResolveSourceGuidPaths(clipFilePath, referencedGuids);
            int unresolvedReferenceCount = 0;
            for (int i = 0; i < events.Count; i++)
            {
                ImportedAnimationEvent animationEvent = events[i];
                SourceAssetReference reference = animationEvent.ObjectReferenceParameter;
                if (reference.IsNull)
                    continue;
                if (resolvedPaths.TryGetValue(reference.Guid, out string? resolvedPath))
                {
                    animationEvent.ObjectReferenceParameter = reference with { ResolvedAssetPath = resolvedPath };
                    continue;
                }

                unresolvedReferenceCount++;
            }

            if (trimmedEventCount > 0)
            {
                manifestBuilder.RecordNotice(
                    EImportedAnimationDataDomain.AnimationEvent,
                    $"Excluded {trimmedEventCount} AnimationEvent entries outside the authored clip range " +
                    $"[{startTimeSeconds:R}, {stopTimeSeconds:R}] seconds.");
            }

            if (discardedCallbackCount > 0)
            {
                manifestBuilder.RecordNotice(
                    EImportedAnimationDataDomain.AnimationEvent,
                    $"Discarded {discardedCallbackCount} source callback(s) that were not present in the explicit native animation event allowlist.");
            }

            if (unresolvedReferenceCount > 0)
            {
                manifestBuilder.RecordNotice(
                    EImportedAnimationDataDomain.AnimationEvent,
                    $"{unresolvedReferenceCount} AnimationEvent object-reference parameters retain their stable " +
                    "Unity GUID/fileID identity because no matching project .meta file was available at import time.");
            }

            events.Sort(static (left, right) =>
            {
                int timeComparison = left.Time.CompareTo(right.Time);
                return timeComparison != 0 ? timeComparison : left.SourceOrder.CompareTo(right.SourceOrder);
            });
            if (events.Count > 0)
            {
                manifestBuilder.RecordSection(
                    EImportedAnimationDataDomain.AnimationEvent,
                    EImportedAnimationCapabilityState.SupportedAndApplied,
                    "m_Events",
                    $"Imported {events.Count} allowlisted native animation event entries in stable authored order.",
                    serializedYaml: string.Empty);
            }
            return [.. events];
        }

        private static SourceAssetReference ReadAssetReference(YamlMappingNode? map)
            => map is null
                ? default
                : new SourceAssetReference(
                    GetScalarLongOrNull(map, "fileID") ?? 0,
                    GetScalarStringOrNull(map, "guid") ?? string.Empty,
                    GetScalarIntOrNull(map, "type") ?? 0);

        private static Vector3 ReadVector3(YamlMappingNode? map)
            => map is null
                ? Vector3.Zero
                : new Vector3(
                    GetScalarFloatOrNull(map, "x") ?? 0.0f,
                    GetScalarFloatOrNull(map, "y") ?? 0.0f,
                    GetScalarFloatOrNull(map, "z") ?? 0.0f);

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

        private static YamlMappingNode? GetMappingOrNull(YamlMappingNode? map, string key)
            => map is not null && TryGetMapping(map, key, out var m) ? m : null;

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

        private static long? GetScalarLongOrNull(YamlMappingNode? map, string key)
        {
            string? scalar = map is null ? null : GetScalarString(map, key);
            return long.TryParse(scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                ? value
                : null;
        }

        private static float? GetScalarFloat(YamlMappingNode map, string key)
        {
            var s = GetScalarString(map, key);
            if (s is null)
                return null;
            if (s.Equals(".inf", StringComparison.OrdinalIgnoreCase)
                || s.Equals("inf", StringComparison.OrdinalIgnoreCase)
                || s.Equals("infinity", StringComparison.OrdinalIgnoreCase))
                return float.PositiveInfinity;
            if (s.Equals("-.inf", StringComparison.OrdinalIgnoreCase)
                || s.Equals("-inf", StringComparison.OrdinalIgnoreCase)
                || s.Equals("-infinity", StringComparison.OrdinalIgnoreCase))
                return float.NegativeInfinity;
            if (s.Equals(".nan", StringComparison.OrdinalIgnoreCase)
                || s.Equals("nan", StringComparison.OrdinalIgnoreCase))
                return float.NaN;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                return v;
            return null;
        }

        private static float? GetScalarFloatOrNull(YamlMappingNode? map, string key)
            => map is null ? null : GetScalarFloat(map, key);
    }
}
