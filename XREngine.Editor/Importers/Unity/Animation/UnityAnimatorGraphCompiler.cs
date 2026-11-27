using System.Numerics;
using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Components.Animation;
using XREngine.Scene.Prefabs;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

/// <summary>
/// Compiles Unity YAML <c>.controller</c> assets (including cross-controller <c>BlendTree</c>
/// references, <c>.anim</c> clip leaves, <c>AvatarMask</c> filters, and supported avatar
/// state behaviours) into a native <see cref="AnimStateMachine"/>.
/// </summary>
public static class UnityAnimatorGraphCompiler
{
    private const string AvatarSdkScriptGuid = "67cc4cb7839cd3741b63733d5adf0442";
    private const long AvatarParameterDriverScriptFileId = -706344726L;
    private const long AvatarTrackingControlScriptFileId = -646210727L;

    /// <summary>
    /// Compiles a Unity <c>.controller</c> file into a new native <see cref="AnimStateMachine"/>.
    /// </summary>
    public static UnityAnimatorCompileResult Compile(
        string controllerPath,
        SourceProjectImportContext? context = null,
        UnityAnimatorCompileOptions? options = null)
    {
        var stateMachine = new AnimStateMachine();
        return MergeInto(stateMachine, controllerPath, context, options);
    }

    /// <summary>
    /// Compiles a Unity <c>.controller</c> file and appends its parameters and layers into an
    /// existing <see cref="AnimStateMachine"/> without overwriting already-defined parameters.
    /// </summary>
    public static UnityAnimatorCompileResult MergeInto(
        AnimStateMachine target,
        string controllerPath,
        SourceProjectImportContext? context = null,
        UnityAnimatorCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerPath);

        string normalizedPath = Path.GetFullPath(controllerPath);
        SourceProjectImportContext activeContext = context ?? new SourceProjectImportContext(normalizedPath);
        UnityAnimatorCompileOptions activeOptions = options ?? new UnityAnimatorCompileOptions();

        var session = new CompileSession(target, activeContext, activeOptions, context is not null);
        UnityAnimatorControllerDocument rootDocument = session.LoadControllerDocument(normalizedPath);
        if (rootDocument.ControllerRoot is null)
            throw new InvalidDataException($"Unity AnimatorController mapping was not found in '{normalizedPath}'.");

        if (string.IsNullOrWhiteSpace(target.Name))
            target.Name = SerializedAssetYamlReader.GetScalarString(rootDocument.ControllerRoot, "m_Name")
                ?? Path.GetFileNameWithoutExtension(normalizedPath);
        if (activeOptions.AllowMissingImportedBindings)
            target.AllowMissingImportedBindings = true;

        session.CollectParameters(rootDocument);
        List<AnimLayer> compiledLayers = session.CompileLayers(rootDocument);
        session.CommitMergedGraph(compiledLayers);

        return new UnityAnimatorCompileResult
        {
            StateMachine = target,
            ControllerPath = normalizedPath,
            Diagnostics = session.Diagnostics,
            CompiledParameterCount = session.CompiledParameterCount,
            CompiledLayerCount = session.CompiledLayerCount,
            CompiledStateCount = session.CompiledStateCount,
            CompiledTransitionCount = session.CompiledTransitionCount,
            CompiledBlendTreeCount = session.CompiledBlendTreeCount,
            CompiledClipCount = session.CompiledClipCount,
            CrossControllerMotionCount = session.CrossControllerMotionCount,
        };
    }

    private sealed class CompileSession(
        AnimStateMachine target,
        SourceProjectImportContext context,
        UnityAnimatorCompileOptions options,
        bool forwardDiagnosticsToContext)
    {
        private readonly record struct PendingParameter(
            string Name,
            int ParamType,
            float DefaultFloat,
            int DefaultInt,
            bool DefaultBool,
            string SourcePath);

        private readonly Dictionary<string, UnityAnimatorControllerDocument> _documentsByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _collectedParameterControllers = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PendingParameter> _pendingParameters = [];
        private readonly HashSet<string> _pendingParameterNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AnimationClip> _compiledClipsByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string DocumentPath, long FileId), MotionBase?> _compiledMotions = [];
        private readonly HashSet<(string DocumentPath, long FileId)> _activeMotions = [];
        private readonly Dictionary<string, EHumanoidBodyMask> _maskCache = new(StringComparer.OrdinalIgnoreCase);

        public List<SourceImportDiagnostic> Diagnostics { get; } = [];
        public int CompiledParameterCount { get; private set; }
        public int CompiledLayerCount { get; private set; }
        public int CompiledStateCount { get; private set; }
        public int CompiledTransitionCount { get; private set; }
        public int CompiledBlendTreeCount { get; private set; }
        public int CompiledClipCount { get; private set; }
        public int CrossControllerMotionCount { get; private set; }

        public UnityAnimatorControllerDocument LoadControllerDocument(string controllerPath, string? sourceGuid = null)
        {
            string normalizedPath = Path.GetFullPath(controllerPath);
            if (_documentsByPath.TryGetValue(normalizedPath, out UnityAnimatorControllerDocument? cached))
                return cached;

            UnityAnimatorControllerDocument loaded = UnityAnimatorControllerDocument.Load(normalizedPath, sourceGuid);
            _documentsByPath[normalizedPath] = loaded;
            return loaded;
        }

        public void CollectParameters(UnityAnimatorControllerDocument document)
        {
            if (document.ControllerRoot is null || !_collectedParameterControllers.Add(document.FilePath))
                return;

            if (SerializedAssetYamlReader.GetNode(document.ControllerRoot, "m_AnimatorParameters") is not YamlSequenceNode parameters)
                return;

            foreach (YamlNode item in parameters.Children)
            {
                if (item is not YamlMappingNode paramMap)
                    continue;

                string? name = SerializedAssetYamlReader.GetScalarString(paramMap, "m_Name");
                if (string.IsNullOrWhiteSpace(name) || !_pendingParameterNames.Add(name))
                    continue;

                int paramType = SerializedAssetYamlReader.GetScalarInt(paramMap, "m_Type") ?? 1;
                _pendingParameters.Add(new PendingParameter(
                    name,
                    paramType,
                    SerializedAssetYamlReader.GetScalarFloat(paramMap, "m_DefaultFloat") ?? 0.0f,
                    SerializedAssetYamlReader.GetScalarInt(paramMap, "m_DefaultInt") ?? 0,
                    SerializedAssetYamlReader.GetScalarBool(paramMap, "m_DefaultBool") ?? false,
                    document.FilePath));
            }
        }

        public List<AnimLayer> CompileLayers(UnityAnimatorControllerDocument document)
        {
            var compiledLayers = new List<AnimLayer>();
            if (document.ControllerRoot is null)
                return compiledLayers;

            if (SerializedAssetYamlReader.GetNode(document.ControllerRoot, "m_AnimatorLayers") is not YamlSequenceNode layers)
                return compiledLayers;

            for (int layerIndex = 0; layerIndex < layers.Children.Count; layerIndex++)
            {
                if (layers.Children[layerIndex] is not YamlMappingNode layerMap)
                    continue;

                string layerName = SerializedAssetYamlReader.GetScalarString(layerMap, "m_Name") ?? $"Layer {layerIndex}";
                int syncedLayerIndex = SerializedAssetYamlReader.GetScalarInt(layerMap, "m_SyncedLayerIndex") ?? -1;
                if (syncedLayerIndex >= 0)
                {
                    AddDiagnostic(
                        "UNITYANIM0005",
                        SourceImportDiagnosticSeverity.Warning,
                        $"Layer '{layerName}' uses synced layer index {syncedLayerIndex}, which has no native synced-layer equivalent.",
                        document.FilePath,
                        $"m_AnimatorLayers[{layerIndex}].m_SyncedLayerIndex");
                }

                if (SerializedAssetYamlReader.GetNode(layerMap, "m_Behaviours") is YamlSequenceNode layerBehaviours
                    && layerBehaviours.Children.Count > 0)
                {
                    AddDiagnostic(
                        "UNITYANIM0009",
                        SourceImportDiagnosticSeverity.Warning,
                        $"Layer '{layerName}' defines {layerBehaviours.Children.Count} layer-level behaviour(s) that were not converted.",
                        document.FilePath,
                        $"m_AnimatorLayers[{layerIndex}].m_Behaviours");
                }

                SourceAssetReference stateMachineRef = SerializedAssetYamlReader.ParseReference(
                    SerializedAssetYamlReader.GetNode(layerMap, "m_StateMachine"));
                if (stateMachineRef.FileId == 0 || !document.StateMachines.TryGetValue(stateMachineRef.FileId, out YamlMappingNode? stateMachineMap))
                {
                    AddDiagnostic(
                        "UNITYANIM0001",
                        SourceImportDiagnosticSeverity.Warning,
                        $"Layer '{layerName}' references missing state machine fileID '{stateMachineRef.FileId}'.",
                        document.FilePath,
                        $"m_AnimatorLayers[{layerIndex}].m_StateMachine");
                    continue;
                }

                int blendingMode = SerializedAssetYamlReader.GetScalarInt(layerMap, "m_BlendingMode") ?? 0;
                // Unity evaluates base layer 0 at weight 1.0 even when m_DefaultWeight is serialized as 0.
                float defaultWeight = layerIndex == 0
                    ? 1.0f
                    : SerializedAssetYamlReader.GetScalarFloat(layerMap, "m_DefaultWeight") ?? 1.0f;

                EHumanoidBodyMask avatarMask = ResolveAvatarMask(
                    SerializedAssetYamlReader.ParseReference(SerializedAssetYamlReader.GetNode(layerMap, "m_Mask")),
                    document.FilePath,
                    $"m_AnimatorLayers[{layerIndex}].m_Mask");

                var layer = new AnimLayer
                {
                    ApplyType = blendingMode == 1 ? AnimLayer.EApplyType.Additive : AnimLayer.EApplyType.Override,
                    Weight = Math.Clamp(defaultWeight, 0.0f, 1.0f),
                    AvatarMask = avatarMask,
                };

                CompileStateMachine(document, stateMachineMap, layer, layerName);
                compiledLayers.Add(layer);
                CompiledLayerCount++;
            }

            return compiledLayers;
        }

        /// <summary>
        /// Isolates colliding animated float parameters when merging into a non-empty state machine,
        /// registers incoming parameters in <c>target.Variables</c>, and appends the compiled layers.
        /// </summary>
        public void CommitMergedGraph(List<AnimLayer> compiledLayers)
        {
            Dictionary<string, string>? parameterRewrites = null;
            if (options.IsolateCollidingAnimatedParameters && target.Layers.Count > 0 && compiledLayers.Count > 0)
            {
                parameterRewrites = BuildCollidingAnimatedParameterRewrites(compiledLayers);
                if (parameterRewrites.Count > 0)
                    RewriteLayerParameters(compiledLayers, parameterRewrites);
            }

            for (int i = 0; i < _pendingParameters.Count; i++)
            {
                PendingParameter param = _pendingParameters[i];
                string resolvedName = parameterRewrites is not null
                    && parameterRewrites.TryGetValue(param.Name, out string? rewritten)
                    ? rewritten
                    : param.Name;

                if (target.Variables.ContainsKey(resolvedName))
                    continue;

                switch (param.ParamType)
                {
                    case 1:
                        target.NewFloat(resolvedName, param.DefaultFloat);
                        CompiledParameterCount++;
                        break;
                    case 3:
                        target.NewInt(resolvedName, param.DefaultInt);
                        CompiledParameterCount++;
                        break;
                    case 4:
                        target.NewBool(resolvedName, param.DefaultBool);
                        CompiledParameterCount++;
                        break;
                    case 9:
                        target.NewBool(resolvedName, param.DefaultBool);
                        CompiledParameterCount++;
                        AddDiagnostic(
                            "UNITYANIM0004",
                            SourceImportDiagnosticSeverity.Info,
                            $"Trigger parameter '{param.Name}' was mapped to an AnimBool; automatic trigger consumption on transition is not emulated.",
                            param.SourcePath,
                            $"m_AnimatorParameters.{param.Name}");
                        break;
                    default:
                        AddDiagnostic(
                            "UNITYANIM0003",
                            SourceImportDiagnosticSeverity.Warning,
                            $"Unsupported Animator parameter type '{param.ParamType}' for '{param.Name}' was ignored.",
                            param.SourcePath,
                            $"m_AnimatorParameters.{param.Name}");
                        break;
                }
            }

            // Ensure every isolated animated float parameter exists in target.Variables even if the
            // source controller animated it by curve without listing it in m_AnimatorParameters.
            if (parameterRewrites is not null)
            {
                foreach (string rewrittenName in parameterRewrites.Values)
                {
                    if (!target.Variables.ContainsKey(rewrittenName))
                    {
                        target.NewFloat(rewrittenName, 0.0f);
                        CompiledParameterCount++;
                    }
                }
            }

            for (int i = 0; i < compiledLayers.Count; i++)
                target.Layers.Add(compiledLayers[i]);
        }

        private Dictionary<string, string> BuildCollidingAnimatedParameterRewrites(List<AnimLayer> incomingLayers)
        {
            var incomingAnimatedParams = new HashSet<string>(StringComparer.Ordinal);
            var visitedMotions = new HashSet<MotionBase>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < incomingLayers.Count; i++)
                CollectLayerAnimatedFloatParameters(incomingLayers[i], visitedMotions, incomingAnimatedParams);

            if (incomingAnimatedParams.Count == 0)
                return [];

            var existingAnimatedParams = new HashSet<string>(StringComparer.Ordinal);
            visitedMotions.Clear();
            for (int i = 0; i < target.Layers.Count; i++)
                CollectLayerAnimatedFloatParameters(target.Layers[i], visitedMotions, existingAnimatedParams);

            var collidingParams = new List<string>();
            foreach (string paramName in incomingAnimatedParams)
            {
                if (target.Variables.ContainsKey(paramName) || existingAnimatedParams.Contains(paramName))
                    collidingParams.Add(paramName);
            }

            if (collidingParams.Count == 0)
                return [];

            // Pick the lowest unused scope index so repeated merges stay deterministic and never collide.
            int scopeIndex = 1;
            while (HasAnyScopedParameterCollision(scopeIndex, collidingParams))
                scopeIndex++;

            string prefix = $"{AnimStateMachine.ScopedInternalParameterPrefix}{scopeIndex}/";
            var rewrites = new Dictionary<string, string>(collidingParams.Count, StringComparer.Ordinal);
            for (int i = 0; i < collidingParams.Count; i++)
                rewrites[collidingParams[i]] = $"{prefix}{collidingParams[i]}";

            return rewrites;
        }

        private bool HasAnyScopedParameterCollision(int scopeIndex, List<string> parameterNames)
        {
            string prefix = $"{AnimStateMachine.ScopedInternalParameterPrefix}{scopeIndex}/";
            for (int i = 0; i < parameterNames.Count; i++)
            {
                if (target.Variables.ContainsKey($"{prefix}{parameterNames[i]}"))
                    return true;
            }

            return false;
        }

        private static void CollectLayerAnimatedFloatParameters(
            AnimLayer layer,
            HashSet<MotionBase> visitedMotions,
            HashSet<string> parameterNames)
        {
            for (int i = 0; i < layer.States.Count; i++)
                CollectMotionAnimatedFloatParameters(layer.States[i].Motion, visitedMotions, parameterNames);
        }

        private static void CollectMotionAnimatedFloatParameters(
            MotionBase? motion,
            HashSet<MotionBase> visitedMotions,
            HashSet<string> parameterNames)
        {
            if (motion is null || !visitedMotions.Add(motion))
                return;

            switch (motion)
            {
                case AnimationClip clip:
                    CollectMemberAnimatedFloatParameters(clip.RootMember, parameterNames);
                    break;
                case BlendTree1D tree1D:
                    for (int i = 0; i < tree1D.Children.Count; i++)
                        CollectMotionAnimatedFloatParameters(tree1D.Children[i].Motion, visitedMotions, parameterNames);
                    break;
                case BlendTree2D tree2D:
                    for (int i = 0; i < tree2D.Children.Count; i++)
                        CollectMotionAnimatedFloatParameters(tree2D.Children[i].Motion, visitedMotions, parameterNames);
                    break;
                case BlendTreeDirect treeDirect:
                    for (int i = 0; i < treeDirect.Children.Count; i++)
                        CollectMotionAnimatedFloatParameters(treeDirect.Children[i].Motion, visitedMotions, parameterNames);
                    break;
            }
        }

        private static void CollectMemberAnimatedFloatParameters(AnimationMember? member, HashSet<string> parameterNames)
        {
            if (member is null)
                return;

            if (member.MemberType == EAnimationMemberType.Method
                && string.Equals(member.MemberName, "SetAnimatedFloatParameter", StringComparison.Ordinal)
                && member.MethodArguments.Length >= 2
                && member.MethodArguments[0] is string name
                && !string.IsNullOrEmpty(name))
            {
                parameterNames.Add(name);
            }

            for (int i = 0; i < member.Children.Count; i++)
                CollectMemberAnimatedFloatParameters(member.Children[i], parameterNames);
        }

        private static void RewriteLayerParameters(
            List<AnimLayer> layers,
            Dictionary<string, string> rewrites)
        {
            var visitedMotions = new HashSet<MotionBase>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < layers.Count; i++)
            {
                AnimLayer layer = layers[i];
                RewriteTransitions(layer.AnyState.Transitions, rewrites);
                for (int s = 0; s < layer.States.Count; s++)
                {
                    AnimState state = layer.States[s];
                    RewriteMotionParameters(state.Motion, visitedMotions, rewrites);
                    RewriteTransitions(state.Transitions, rewrites);
                    RewriteStateComponents(state, rewrites);
                }
            }
        }

        private static void RewriteMotionParameters(
            MotionBase? motion,
            HashSet<MotionBase> visitedMotions,
            Dictionary<string, string> rewrites)
        {
            if (motion is null || !visitedMotions.Add(motion))
                return;

            switch (motion)
            {
                case AnimationClip clip:
                    RewriteMemberAnimatedFloatParameters(clip.RootMember, rewrites);
                    break;
                case BlendTree1D tree1D:
                    if (tree1D.ParameterName is not null && rewrites.TryGetValue(tree1D.ParameterName, out string? rewritten1D))
                        tree1D.ParameterName = rewritten1D;
                    for (int i = 0; i < tree1D.Children.Count; i++)
                        RewriteMotionParameters(tree1D.Children[i].Motion, visitedMotions, rewrites);
                    break;
                case BlendTree2D tree2D:
                    if (tree2D.XParameterName is not null && rewrites.TryGetValue(tree2D.XParameterName, out string? rewrittenX))
                        tree2D.XParameterName = rewrittenX;
                    if (tree2D.YParameterName is not null && rewrites.TryGetValue(tree2D.YParameterName, out string? rewrittenY))
                        tree2D.YParameterName = rewrittenY;
                    for (int i = 0; i < tree2D.Children.Count; i++)
                        RewriteMotionParameters(tree2D.Children[i].Motion, visitedMotions, rewrites);
                    break;
                case BlendTreeDirect treeDirect:
                    for (int i = 0; i < treeDirect.Children.Count; i++)
                    {
                        BlendTreeDirect.Child child = treeDirect.Children[i];
                        if (child.WeightParameterName is not null
                            && rewrites.TryGetValue(child.WeightParameterName, out string? rewrittenWeight))
                        {
                            child.WeightParameterName = rewrittenWeight;
                        }
                        RewriteMotionParameters(child.Motion, visitedMotions, rewrites);
                    }
                    break;
            }
        }

        private static void RewriteMemberAnimatedFloatParameters(
            AnimationMember? member,
            Dictionary<string, string> rewrites)
        {
            if (member is null)
                return;

            if (member.MemberType == EAnimationMemberType.Method
                && string.Equals(member.MemberName, "SetAnimatedFloatParameter", StringComparison.Ordinal)
                && member.MethodArguments.Length >= 2
                && member.MethodArguments[0] is string name
                && rewrites.TryGetValue(name, out string? rewritten))
            {
                member.MethodArguments[0] = rewritten;
            }

            for (int i = 0; i < member.Children.Count; i++)
                RewriteMemberAnimatedFloatParameters(member.Children[i], rewrites);
        }

        private static void RewriteTransitions(
            IList<AnimStateTransition> transitions,
            Dictionary<string, string> rewrites)
        {
            for (int i = 0; i < transitions.Count; i++)
            {
                IList<AnimTransitionCondition> conditions = transitions[i].Conditions;
                for (int c = 0; c < conditions.Count; c++)
                {
                    AnimTransitionCondition condition = conditions[c];
                    if (condition.ParameterName is not null
                        && rewrites.TryGetValue(condition.ParameterName, out string? rewritten))
                    {
                        condition.ParameterName = rewritten;
                    }
                }
            }
        }

        private static void RewriteStateComponents(
            AnimState state,
            Dictionary<string, string> rewrites)
        {
            for (int i = 0; i < state.Components.Count; i++)
            {
                if (state.Components[i] is not AnimParameterDriverComponent driver)
                    continue;

                if (!string.IsNullOrEmpty(driver.DstParameterName)
                    && rewrites.TryGetValue(driver.DstParameterName, out string? rewrittenDst))
                {
                    driver.DstParameterName = rewrittenDst;
                }

                if (!string.IsNullOrEmpty(driver.SrcParameterName)
                    && rewrites.TryGetValue(driver.SrcParameterName, out string? rewrittenSrc))
                {
                    driver.SrcParameterName = rewrittenSrc;
                }
            }
        }

        private EHumanoidBodyMask ResolveAvatarMask(SourceAssetReference maskRef, string sourcePath, string propertyPath)
        {
            if (maskRef.FileId == 0 && string.IsNullOrWhiteSpace(maskRef.Guid))
                return EHumanoidBodyMask.All;

            if (string.IsNullOrWhiteSpace(maskRef.Guid))
                return EHumanoidBodyMask.All;

            string? maskPath = context.Resolver.Resolve(maskRef.Guid);
            if (string.IsNullOrWhiteSpace(maskPath) || !File.Exists(maskPath))
            {
                AddDiagnostic(
                    "UNITYANIM0011",
                    SourceImportDiagnosticSeverity.Warning,
                    $"AvatarMask GUID '{maskRef.Guid}' could not be resolved; layer defaulted to EHumanoidBodyMask.All.",
                    sourcePath,
                    propertyPath);
                return EHumanoidBodyMask.All;
            }

            if (_maskCache.TryGetValue(maskPath, out EHumanoidBodyMask cached))
                return cached;

            EHumanoidBodyMask parsed = UnityAvatarMaskReader.Read(maskPath);
            _maskCache[maskPath] = parsed;
            return parsed;
        }

        private void CompileStateMachine(
            UnityAnimatorControllerDocument document,
            YamlMappingNode stateMachineMap,
            AnimLayer layer,
            string layerName)
        {
            if (SerializedAssetYamlReader.GetNode(stateMachineMap, "m_ChildStateMachines") is YamlSequenceNode subMachines
                && subMachines.Children.Count > 0)
            {
                AddDiagnostic(
                    "UNITYANIM0006",
                    SourceImportDiagnosticSeverity.Warning,
                    $"Layer '{layerName}' contains {subMachines.Children.Count} nested sub-state machine(s) that are not flattened.",
                    document.FilePath,
                    $"{layerName}.m_ChildStateMachines");
            }

            if (SerializedAssetYamlReader.GetNode(stateMachineMap, "m_EntryTransitions") is YamlSequenceNode entryTransitions
                && entryTransitions.Children.Count > 0)
            {
                AddDiagnostic(
                    "UNITYANIM0007",
                    SourceImportDiagnosticSeverity.Warning,
                    $"Layer '{layerName}' contains {entryTransitions.Children.Count} entry transition(s) that were not converted.",
                    document.FilePath,
                    $"{layerName}.m_EntryTransitions");
            }

            layer.AnyState.Position = SerializedAssetYamlReader.GetVector2(stateMachineMap, "m_AnyStatePosition", Vector2.Zero);

            Dictionary<long, AnimState> statesByFileId = [];
            List<(long FileId, YamlMappingNode Mapping, AnimState State)> orderedStates = [];

            if (SerializedAssetYamlReader.GetNode(stateMachineMap, "m_ChildStates") is YamlSequenceNode childStates)
            {
                foreach (YamlNode childNode in childStates.Children)
                {
                    if (childNode is not YamlMappingNode entry)
                        continue;

                    SourceAssetReference stateRef = SerializedAssetYamlReader.ParseReference(
                        SerializedAssetYamlReader.GetNode(entry, "m_State"));
                    if (stateRef.FileId == 0 || !document.States.TryGetValue(stateRef.FileId, out YamlMappingNode? stateMap))
                        continue;

                    string stateName = SerializedAssetYamlReader.GetScalarString(stateMap, "m_Name") ?? $"State_{stateRef.FileId}";
                    Vector2 position = SerializedAssetYamlReader.GetVector2(entry, "m_Position", Vector2.Zero);

                    CheckUnsupportedStateSettings(document, stateMap, stateName, layerName);

                    MotionBase? motion = ResolveMotion(
                        document,
                        SerializedAssetYamlReader.ParseReference(SerializedAssetYamlReader.GetNode(stateMap, "m_Motion")),
                        $"{layerName}.{stateName}.m_Motion");

                    var state = new AnimState
                    {
                        Name = stateName,
                        Position = position,
                        Motion = motion,
                    };

                    AttachStateBehaviours(document, stateMap, state, $"{layerName}.{stateName}");

                    layer.States.Add(state);
                    statesByFileId[stateRef.FileId] = state;
                    orderedStates.Add((stateRef.FileId, stateMap, state));
                    CompiledStateCount++;
                }
            }

            SourceAssetReference defaultStateRef = SerializedAssetYamlReader.ParseReference(
                SerializedAssetYamlReader.GetNode(stateMachineMap, "m_DefaultState"));
            if (defaultStateRef.FileId != 0 && statesByFileId.TryGetValue(defaultStateRef.FileId, out AnimState? defaultState))
                layer.InitialState = defaultState;
            else if (layer.States.Count > 0)
                layer.InitialState = layer.States[0];

            // Wire state-to-state transitions now that every destination state exists in the layer.
            foreach ((_, YamlMappingNode stateMap, AnimState sourceState) in orderedStates)
            {
                if (SerializedAssetYamlReader.GetNode(stateMap, "m_Transitions") is not YamlSequenceNode transitions)
                    continue;

                CompileTransitions(document, transitions, sourceState, statesByFileId, $"{layerName}.{sourceState.Name}");
            }

            if (SerializedAssetYamlReader.GetNode(stateMachineMap, "m_AnyStateTransitions") is YamlSequenceNode anyTransitions)
                CompileTransitions(document, anyTransitions, layer.AnyState, statesByFileId, $"{layerName}.AnyState");
        }

        private void CheckUnsupportedStateSettings(
            UnityAnimatorControllerDocument document,
            YamlMappingNode stateMap,
            string stateName,
            string layerName)
        {
            if ((SerializedAssetYamlReader.GetScalarInt(stateMap, "m_WriteDefaultValues") ?? 1) == 0)
            {
                AddDiagnostic(
                    "UNITYANIM0002",
                    SourceImportDiagnosticSeverity.Warning,
                    $"State '{stateName}' in layer '{layerName}' uses WriteDefaults=Off; AnimStateMachine resets unauthored channels to defaults each frame.",
                    document.FilePath,
                    $"{layerName}.{stateName}.m_WriteDefaultValues");
            }

            if ((SerializedAssetYamlReader.GetScalarInt(stateMap, "m_SpeedParameterActive") ?? 0) != 0
                || (SerializedAssetYamlReader.GetScalarInt(stateMap, "m_MirrorParameterActive") ?? 0) != 0
                || (SerializedAssetYamlReader.GetScalarInt(stateMap, "m_CycleOffsetParameterActive") ?? 0) != 0
                || (SerializedAssetYamlReader.GetScalarInt(stateMap, "m_TimeParameterActive") ?? 0) != 0)
            {
                AddDiagnostic(
                    "UNITYANIM0008",
                    SourceImportDiagnosticSeverity.Warning,
                    $"State '{stateName}' in layer '{layerName}' enables parameter-driven Speed/Mirror/CycleOffset/Time controls that are not yet applied.",
                    document.FilePath,
                    $"{layerName}.{stateName}");
            }
        }

        private void AttachStateBehaviours(
            UnityAnimatorControllerDocument document,
            YamlMappingNode stateMap,
            AnimState state,
            string statePath)
        {
            if (SerializedAssetYamlReader.GetNode(stateMap, "m_StateMachineBehaviours") is not YamlSequenceNode behaviours)
                return;

            foreach (YamlNode item in behaviours.Children)
            {
                SourceAssetReference behaviourRef = SerializedAssetYamlReader.ParseReference(item);
                if (behaviourRef.FileId == 0 || !document.Behaviours.TryGetValue(behaviourRef.FileId, out YamlMappingNode? behaviourMap))
                    continue;

                SourceAssetReference scriptRef = SerializedAssetYamlReader.ParseReference(
                    SerializedAssetYamlReader.GetNode(behaviourMap, "m_Script"));

                if (string.Equals(scriptRef.Guid, AvatarSdkScriptGuid, StringComparison.OrdinalIgnoreCase)
                    && scriptRef.FileId == AvatarParameterDriverScriptFileId)
                {
                    AttachParameterDriverBehaviour(behaviourMap, state);
                    continue;
                }

                if (string.Equals(scriptRef.Guid, AvatarSdkScriptGuid, StringComparison.OrdinalIgnoreCase)
                    && scriptRef.FileId == AvatarTrackingControlScriptFileId)
                {
                    AttachTrackingControlBehaviour(behaviourMap, state);
                    continue;
                }

                AddDiagnostic(
                    "UNITYANIM0009",
                    SourceImportDiagnosticSeverity.Warning,
                    $"Unsupported StateMachineBehaviour script '{scriptRef.Guid}:{scriptRef.FileId}' on state '{state.Name}' was not converted.",
                    document.FilePath,
                    $"{statePath}.m_StateMachineBehaviours");
            }
        }

        private static void AttachParameterDriverBehaviour(YamlMappingNode behaviourMap, AnimState state)
        {
            bool localOnly = (SerializedAssetYamlReader.GetScalarInt(behaviourMap, "localOnly") ?? 0) != 0;
            if (SerializedAssetYamlReader.GetNode(behaviourMap, "parameters") is not YamlSequenceNode parameters)
                return;

            foreach (YamlNode item in parameters.Children)
            {
                if (item is not YamlMappingNode entry)
                    continue;

                string dstName = SerializedAssetYamlReader.GetScalarString(entry, "name") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dstName))
                    continue;

                int driverType = SerializedAssetYamlReader.GetScalarInt(entry, "type") ?? 0;
                AnimParameterDriverComponent.EOperation operation = driverType switch
                {
                    0 => AnimParameterDriverComponent.EOperation.Set,
                    1 => AnimParameterDriverComponent.EOperation.Add,
                    2 => AnimParameterDriverComponent.EOperation.Random,
                    3 => AnimParameterDriverComponent.EOperation.Copy,
                    _ => AnimParameterDriverComponent.EOperation.Set,
                };

                string srcName = SerializedAssetYamlReader.GetScalarString(entry, "source") ?? string.Empty;
                if (operation == AnimParameterDriverComponent.EOperation.Add && string.IsNullOrWhiteSpace(srcName))
                    srcName = dstName;

                AnimParameterDriverComponent driver = state.AddComponent<AnimParameterDriverComponent>();
                driver.ExecuteLocally = true;
                driver.ExecuteRemotely = !localOnly;
                driver.DstParameterName = dstName;
                driver.SrcParameterName = srcName;
                driver.Operation = operation;
                driver.ConstantValue = SerializedAssetYamlReader.GetScalarFloat(entry, "value") ?? 0.0f;
                driver.RandomMin = SerializedAssetYamlReader.GetScalarFloat(entry, "valueMin") ?? 0.0f;
                driver.RandomMax = SerializedAssetYamlReader.GetScalarFloat(entry, "valueMax") ?? 0.0f;
                driver.RandomChance = SerializedAssetYamlReader.GetScalarFloat(entry, "chance") ?? 0.5f;
                driver.ConvertRange = (SerializedAssetYamlReader.GetScalarInt(entry, "convertRange") ?? 0) != 0;
                driver.SourceMin = SerializedAssetYamlReader.GetScalarFloat(entry, "sourceMin") ?? 0.0f;
                driver.SourceMax = SerializedAssetYamlReader.GetScalarFloat(entry, "sourceMax") ?? 1.0f;
                driver.DestMin = SerializedAssetYamlReader.GetScalarFloat(entry, "destMin") ?? 0.0f;
                driver.DestMax = SerializedAssetYamlReader.GetScalarFloat(entry, "destMax") ?? 1.0f;
            }
        }

        private static void AttachTrackingControlBehaviour(YamlMappingNode behaviourMap, AnimState state)
        {
            TrackingControllerComponent tracking = state.AddComponent<TrackingControllerComponent>();
            tracking.TrackingModeHead = ParseTrackingMode(behaviourMap, "trackingHead");
            tracking.TrackingModeLeftHand = ParseTrackingMode(behaviourMap, "trackingLeftHand");
            tracking.TrackingModeRightHand = ParseTrackingMode(behaviourMap, "trackingRightHand");
            tracking.TrackingModeLeftFoot = ParseTrackingMode(behaviourMap, "trackingLeftFoot");
            tracking.TrackingModeRightFoot = ParseTrackingMode(behaviourMap, "trackingRightFoot");
            tracking.TrackingModeLeftFingers = ParseTrackingMode(behaviourMap, "trackingLeftFingers");
            tracking.TrackingModeRightFingers = ParseTrackingMode(behaviourMap, "trackingRightFingers");
            tracking.TrackingModeEyes = ParseTrackingMode(behaviourMap, "trackingEyes");
            tracking.TrackingModeMouth = ParseTrackingMode(behaviourMap, "trackingMouth");
        }

        private static TrackingControllerComponent.ETrackingMode ParseTrackingMode(YamlMappingNode behaviourMap, string key)
            => (SerializedAssetYamlReader.GetScalarInt(behaviourMap, key) ?? 0) switch
            {
                1 => TrackingControllerComponent.ETrackingMode.Tracking,
                2 => TrackingControllerComponent.ETrackingMode.Animation,
                _ => TrackingControllerComponent.ETrackingMode.Unchanged,
            };

        private void CompileTransitions(
            UnityAnimatorControllerDocument document,
            YamlSequenceNode transitionRefs,
            AnimStateBase ownerState,
            Dictionary<long, AnimState> statesByFileId,
            string ownerPath)
        {
            foreach (YamlNode item in transitionRefs.Children)
            {
                SourceAssetReference transitionRef = SerializedAssetYamlReader.ParseReference(item);
                if (transitionRef.FileId == 0 || !document.Transitions.TryGetValue(transitionRef.FileId, out YamlMappingNode? transitionMap))
                    continue;

                if ((SerializedAssetYamlReader.GetScalarInt(transitionMap, "m_Mute") ?? 0) != 0)
                    continue;

                if ((SerializedAssetYamlReader.GetScalarInt(transitionMap, "m_IsExit") ?? 0) != 0)
                {
                    AddDiagnostic(
                        "UNITYANIM0010",
                        SourceImportDiagnosticSeverity.Warning,
                        $"Exit transition on '{ownerPath}' has no native exit node and was skipped.",
                        document.FilePath,
                        $"{ownerPath}.m_Transitions");
                    continue;
                }

                SourceAssetReference dstStateMachineRef = SerializedAssetYamlReader.ParseReference(
                    SerializedAssetYamlReader.GetNode(transitionMap, "m_DstStateMachine"));
                if (dstStateMachineRef.FileId != 0)
                {
                    AddDiagnostic(
                        "UNITYANIM0010",
                        SourceImportDiagnosticSeverity.Warning,
                        $"Transition on '{ownerPath}' targeting sub-state machine '{dstStateMachineRef.FileId}' was skipped.",
                        document.FilePath,
                        $"{ownerPath}.m_Transitions");
                    continue;
                }

                SourceAssetReference dstStateRef = SerializedAssetYamlReader.ParseReference(
                    SerializedAssetYamlReader.GetNode(transitionMap, "m_DstState"));
                if (dstStateRef.FileId == 0 || !statesByFileId.TryGetValue(dstStateRef.FileId, out AnimState? destinationState))
                    continue;

                var transition = new AnimStateTransition
                {
                    Name = SerializedAssetYamlReader.GetScalarString(transitionMap, "m_Name") ?? string.Empty,
                    DestinationState = destinationState,
                    BlendDuration = SerializedAssetYamlReader.GetScalarFloat(transitionMap, "m_TransitionDuration") ?? 0.0f,
                    TransitionOffset = SerializedAssetYamlReader.GetScalarFloat(transitionMap, "m_TransitionOffset") ?? 0.0f,
                    ExitTime = SerializedAssetYamlReader.GetScalarFloat(transitionMap, "m_ExitTime") ?? 0.0f,
                    HasExitTime = (SerializedAssetYamlReader.GetScalarInt(transitionMap, "m_HasExitTime") ?? 0) != 0,
                    FixedDuration = (SerializedAssetYamlReader.GetScalarInt(transitionMap, "m_HasFixedDuration") ?? 1) != 0,
                    InterruptionSource = ParseInterruptionSource(SerializedAssetYamlReader.GetScalarInt(transitionMap, "m_InterruptionSource") ?? 0),
                    OrderedInterruption = (SerializedAssetYamlReader.GetScalarInt(transitionMap, "m_OrderedInterruption") ?? 1) != 0,
                    CanTransitionToSelf = (SerializedAssetYamlReader.GetScalarInt(transitionMap, "m_CanTransitionToSelf") ?? 0) != 0,
                };

                if (SerializedAssetYamlReader.GetNode(transitionMap, "m_Conditions") is YamlSequenceNode conditions)
                {
                    foreach (YamlNode condNode in conditions.Children)
                    {
                        if (condNode is not YamlMappingNode condMap)
                            continue;

                        string? parameterName = SerializedAssetYamlReader.GetScalarString(condMap, "m_ConditionEvent");
                        if (string.IsNullOrWhiteSpace(parameterName))
                            continue;

                        int mode = SerializedAssetYamlReader.GetScalarInt(condMap, "m_ConditionMode") ?? 1;
                        // Unity serializes "m_EventTreshold" (without the second 'h').
                        float threshold = SerializedAssetYamlReader.GetScalarFloat(condMap, "m_EventTreshold")
                            ?? SerializedAssetYamlReader.GetScalarFloat(condMap, "m_EventThreshold")
                            ?? 0.0f;

                        AnimTransitionCondition.EComparison comparison = mode switch
                        {
                            1 => AnimTransitionCondition.EComparison.IsTrue,
                            2 => AnimTransitionCondition.EComparison.IsFalse,
                            3 => AnimTransitionCondition.EComparison.GreaterThan,
                            4 => AnimTransitionCondition.EComparison.LessThan,
                            6 => AnimTransitionCondition.EComparison.Equal,
                            7 => AnimTransitionCondition.EComparison.NotEqual,
                            _ => AnimTransitionCondition.EComparison.IsTrue,
                        };

                        transition.Conditions.Add(new AnimTransitionCondition
                        {
                            ParameterName = parameterName,
                            Comparison = comparison,
                            ComparisonFloat = threshold,
                            ComparisonInt = float.IsFinite(threshold) ? (int)MathF.Round(threshold) : 0,
                            ComparisonBool = mode == 1,
                        });
                    }
                }

                ownerState.Transitions.Add(transition);
                CompiledTransitionCount++;
            }
        }

        private static ETransitionInterruptionSource ParseInterruptionSource(int source)
            => source switch
            {
                1 => ETransitionInterruptionSource.Current,
                2 => ETransitionInterruptionSource.Next,
                3 => ETransitionInterruptionSource.CurrentThenNext,
                4 => ETransitionInterruptionSource.NextThenCurrent,
                _ => ETransitionInterruptionSource.Neither,
            };

        private MotionBase? ResolveMotion(
            UnityAnimatorControllerDocument currentDocument,
            SourceAssetReference motionRef,
            string propertyPath)
        {
            if (motionRef.FileId == 0 && string.IsNullOrWhiteSpace(motionRef.Guid))
                return null;

            // Local BlendTree reference inside the current controller file.
            if (string.IsNullOrWhiteSpace(motionRef.Guid))
                return CompileBlendTreeFromDocument(currentDocument, motionRef.FileId, propertyPath);

            // External reference: either an .anim clip or a BlendTree sub-asset inside another .controller.
            string? assetPath = context.Resolver.Resolve(motionRef.Guid);
            if (string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
            {
                AddDiagnostic(
                    "UNITYANIM0012",
                    SourceImportDiagnosticSeverity.Warning,
                    $"Motion reference GUID '{motionRef.Guid}' (fileID {motionRef.FileId}) could not be resolved.",
                    currentDocument.FilePath,
                    propertyPath);
                return null;
            }

            string extension = Path.GetExtension(assetPath);
            if (extension.Equals(".anim", StringComparison.OrdinalIgnoreCase))
                return CompileClip(assetPath);

            if (extension.Equals(".controller", StringComparison.OrdinalIgnoreCase))
            {
                UnityAnimatorControllerDocument externalDocument = LoadControllerDocument(assetPath, motionRef.Guid);
                CollectParameters(externalDocument);
                CrossControllerMotionCount++;
                return CompileBlendTreeFromDocument(externalDocument, motionRef.FileId, propertyPath);
            }

            AddDiagnostic(
                "UNITYANIM0013",
                SourceImportDiagnosticSeverity.Warning,
                $"Unsupported motion asset '{Path.GetFileName(assetPath)}' (GUID '{motionRef.Guid}') was skipped.",
                currentDocument.FilePath,
                propertyPath);
            return null;
        }

        private AnimationClip CompileClip(string clipPath)
        {
            string normalizedPath = Path.GetFullPath(clipPath);
            if (_compiledClipsByPath.TryGetValue(normalizedPath, out AnimationClip? cached))
                return cached;

            AnimationClip clip = AnimYamlImporter.Import(normalizedPath, options.NodePathRewrites);
            _compiledClipsByPath[normalizedPath] = clip;
            CompiledClipCount++;
            return clip;
        }

        private MotionBase? CompileBlendTreeFromDocument(
            UnityAnimatorControllerDocument document,
            long fileId,
            string propertyPath)
        {
            (string DocumentPath, long FileId) key = (document.FilePath, fileId);
            if (_compiledMotions.TryGetValue(key, out MotionBase? cached))
                return cached;

            if (!_activeMotions.Add(key))
            {
                AddDiagnostic(
                    "UNITYANIM0014",
                    SourceImportDiagnosticSeverity.Warning,
                    $"Cyclic BlendTree reference detected at fileID '{fileId}' in '{Path.GetFileName(document.FilePath)}'.",
                    document.FilePath,
                    propertyPath);
                return null;
            }

            try
            {
                if (!document.BlendTrees.TryGetValue(fileId, out YamlMappingNode? blendTreeMap))
                {
                    AddDiagnostic(
                        "UNITYANIM0015",
                        SourceImportDiagnosticSeverity.Warning,
                        $"BlendTree fileID '{fileId}' was not found in '{Path.GetFileName(document.FilePath)}'.",
                        document.FilePath,
                        propertyPath);
                    _compiledMotions[key] = null;
                    return null;
                }

                MotionBase compiled = CompileBlendTree(document, blendTreeMap, propertyPath);
                _compiledMotions[key] = compiled;
                CompiledBlendTreeCount++;
                return compiled;
            }
            finally
            {
                _activeMotions.Remove(key);
            }
        }

        private MotionBase CompileBlendTree(
            UnityAnimatorControllerDocument document,
            YamlMappingNode blendTreeMap,
            string propertyPath)
        {
            string name = SerializedAssetYamlReader.GetScalarString(blendTreeMap, "m_Name") ?? "BlendTree";
            int blendType = SerializedAssetYamlReader.GetScalarInt(blendTreeMap, "m_BlendType") ?? 0;
            string blendParamX = SerializedAssetYamlReader.GetScalarString(blendTreeMap, "m_BlendParameter") ?? string.Empty;
            string blendParamY = SerializedAssetYamlReader.GetScalarString(blendTreeMap, "m_BlendParameterY") ?? string.Empty;
            bool normalizeBlendValues = (SerializedAssetYamlReader.GetScalarInt(blendTreeMap, "m_NormalizedBlendValues") ?? 0) != 0;
            YamlSequenceNode? children = SerializedAssetYamlReader.GetNode(blendTreeMap, "m_Childs") as YamlSequenceNode;

            switch (blendType)
            {
                case 0:
                    return CompileBlendTree1D(document, name, blendParamX, children, propertyPath);

                case 1:
                    return CompileBlendTree2D(document, name, BlendTree2D.EBlendType.SimpleDirectional, blendParamX, blendParamY, children, propertyPath);

                case 2:
                    return CompileBlendTree2D(document, name, BlendTree2D.EBlendType.FreeformDirectional, blendParamX, blendParamY, children, propertyPath);

                case 3:
                    return CompileBlendTree2D(document, name, BlendTree2D.EBlendType.FreeformCartesian, blendParamX, blendParamY, children, propertyPath);

                case 4:
                    return CompileBlendTreeDirect(document, name, normalizeBlendValues, children, propertyPath);

                default:
                    AddDiagnostic(
                        "UNITYANIM0016",
                        SourceImportDiagnosticSeverity.Warning,
                        $"Unsupported BlendTree m_BlendType '{blendType}' on '{name}'; falling back to BlendTree1D.",
                        document.FilePath,
                        propertyPath);
                    return CompileBlendTree1D(document, name, blendParamX, children, propertyPath);
            }
        }

        private BlendTree1D CompileBlendTree1D(
            UnityAnimatorControllerDocument document,
            string name,
            string parameterName,
            YamlSequenceNode? children,
            string propertyPath)
        {
            var tree = new BlendTree1D
            {
                Name = name,
                ParameterName = parameterName,
            };

            if (children is null)
                return tree;

            for (int i = 0; i < children.Children.Count; i++)
            {
                if (children.Children[i] is not YamlMappingNode childMap)
                    continue;

                MotionBase? childMotion = ResolveMotion(
                    document,
                    SerializedAssetYamlReader.ParseReference(SerializedAssetYamlReader.GetNode(childMap, "m_Motion")),
                    $"{propertyPath}.{name}[{i}]");

                tree.Children.Add(new BlendTree1D.Child
                {
                    Motion = childMotion,
                    Threshold = SerializedAssetYamlReader.GetScalarFloat(childMap, "m_Threshold") ?? 0.0f,
                    Speed = SerializedAssetYamlReader.GetScalarFloat(childMap, "m_TimeScale") ?? 1.0f,
                    CycleOffset = SerializedAssetYamlReader.GetScalarFloat(childMap, "m_CycleOffset") ?? 0.0f,
                    HumanoidMirror = (SerializedAssetYamlReader.GetScalarInt(childMap, "m_Mirror") ?? 0) != 0,
                });
            }

            return tree;
        }

        private BlendTree2D CompileBlendTree2D(
            UnityAnimatorControllerDocument document,
            string name,
            BlendTree2D.EBlendType blendType,
            string xParameterName,
            string yParameterName,
            YamlSequenceNode? children,
            string propertyPath)
        {
            var tree = new BlendTree2D
            {
                Name = name,
                BlendType = blendType,
                XParameterName = xParameterName,
                YParameterName = yParameterName,
            };

            if (children is null)
                return tree;

            for (int i = 0; i < children.Children.Count; i++)
            {
                if (children.Children[i] is not YamlMappingNode childMap)
                    continue;

                MotionBase? childMotion = ResolveMotion(
                    document,
                    SerializedAssetYamlReader.ParseReference(SerializedAssetYamlReader.GetNode(childMap, "m_Motion")),
                    $"{propertyPath}.{name}[{i}]");
                Vector2 position = SerializedAssetYamlReader.GetVector2(childMap, "m_Position", Vector2.Zero);

                tree.Children.Add(new BlendTree2D.Child
                {
                    Motion = childMotion,
                    PositionX = position.X,
                    PositionY = position.Y,
                    Speed = SerializedAssetYamlReader.GetScalarFloat(childMap, "m_TimeScale") ?? 1.0f,
                    CycleOffset = SerializedAssetYamlReader.GetScalarFloat(childMap, "m_CycleOffset") ?? 0.0f,
                    HumanoidMirror = (SerializedAssetYamlReader.GetScalarInt(childMap, "m_Mirror") ?? 0) != 0,
                });
            }

            return tree;
        }

        private BlendTreeDirect CompileBlendTreeDirect(
            UnityAnimatorControllerDocument document,
            string name,
            bool normalizeBlendValues,
            YamlSequenceNode? children,
            string propertyPath)
        {
            var tree = new BlendTreeDirect
            {
                Name = name,
                NormalizeBlendValues = normalizeBlendValues,
            };

            if (children is null)
                return tree;

            for (int i = 0; i < children.Children.Count; i++)
            {
                if (children.Children[i] is not YamlMappingNode childMap)
                    continue;

                MotionBase? childMotion = ResolveMotion(
                    document,
                    SerializedAssetYamlReader.ParseReference(SerializedAssetYamlReader.GetNode(childMap, "m_Motion")),
                    $"{propertyPath}.{name}[{i}]");
                string? weightParameter = SerializedAssetYamlReader.GetScalarString(childMap, "m_DirectBlendParameter");

                tree.Children.Add(new BlendTreeDirect.Child
                {
                    Motion = childMotion,
                    WeightParameterName = string.IsNullOrWhiteSpace(weightParameter) ? null : weightParameter,
                    Speed = SerializedAssetYamlReader.GetScalarFloat(childMap, "m_TimeScale") ?? 1.0f,
                    CycleOffset = SerializedAssetYamlReader.GetScalarFloat(childMap, "m_CycleOffset") ?? 0.0f,
                    HumanoidMirror = (SerializedAssetYamlReader.GetScalarInt(childMap, "m_Mirror") ?? 0) != 0,
                });
            }

            return tree;
        }

        private void AddDiagnostic(
            string code,
            SourceImportDiagnosticSeverity severity,
            string message,
            string sourcePath,
            string propertyPath)
        {
            if (!options.EmitUnsupportedFeatureDiagnostics && severity != SourceImportDiagnosticSeverity.Error)
                return;

            var diagnostic = new SourceImportDiagnostic
            {
                Code = code,
                Severity = severity,
                Category = SourceImportDiagnosticCategory.AvatarComponent,
                Message = message,
                SourcePath = sourcePath,
                PropertyPath = propertyPath,
            };
            Diagnostics.Add(diagnostic);
            if (forwardDiagnosticsToContext)
                context.Diagnostics.Add(diagnostic);
        }
    }
}
