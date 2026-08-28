using System.Collections.Generic;
using System.Numerics;
using MemoryPack;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Animation.Importers;
using static XREngine.Animation.AnimLayer;

namespace XREngine.Animation
{
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class AnimStateMachine : XRAsset
    {
        private static readonly string[] QuaternionComponentMethodStems =
        [
            "SetRootRotation",
            "SetAnimatedIKRotation",
        ];

        public enum AnimParameterType : byte
        {
            Bool = 1,
            Int = 2,
            Float = 3,
        }

        public readonly record struct AnimParameterSchemaEntry(
            string Name,
            AnimParameterType Type,
            bool BoolDefault,
            int IntDefault,
            float FloatDefault);

        /// <summary>
        /// Returns the minimal number of bits required to represent <paramref name="count"/> distinct values.
        /// For example, 0-&gt;0, 1-&gt;0, 2-&gt;1, 3-&gt;2, 4-&gt;2, 5-&gt;3.
        /// </summary>
        public static int GetMinimalBitCountForCount(int count)
        {
            if (count <= 1)
                return 0;
            return BitOperations.Log2((uint)(count - 1)) + 1;
        }

        private bool _animatePhysics = false;
        public bool AnimatePhysics
        {
            get => _animatePhysics;
            set => SetField(ref _animatePhysics, value);
        }

        private bool _applyRootMotion;
        private Vector3 _pivotPosition;
        private Vector3 _deltaPosition;

        private EventList<AnimLayer> _layers = [];
        public EventList<AnimLayer> Layers
        {
            get => _layers;
            set => SetField(ref _layers, value);
        }
        
        [MemoryPackIgnore]
        protected internal Dictionary<string, object?> _defaultValues = [];
        [MemoryPackIgnore]
        protected internal Dictionary<string, object?> _animationValues = [];
        [MemoryPackIgnore]
        protected internal readonly Dictionary<string, AnimationMember> _animatedCurves = [];
        [MemoryPackIgnore]
        private readonly object _animationValuesLock = new();

        /// <summary>
        /// Typed value store for this state machine, sized to the slot layout.
        /// </summary>
        [MemoryPackIgnore]
        internal AnimationValueStore ValueStore { get; } = new();

        [MemoryPackIgnore]
        private readonly HumanoidMotionContributionBuffer _importedHumanoidContributions = new();

        [MemoryPackIgnore]
        private ulong _humanoidMotionContinuityVersion;

        [MemoryPackIgnore]
        public ulong HumanoidMotionContinuityVersion => _humanoidMotionContinuityVersion;

        [MemoryPackIgnore]
        public EAnimMotionContinuityChange LastHumanoidMotionContinuityChange { get; private set; }

        /// <summary>
        /// Exact weighted Unity humanoid leaf samples from the most recent graph evaluation.
        /// The span remains valid until the next evaluation or deinitialization.
        /// </summary>
        public ReadOnlySpan<HumanoidMotionContribution> HumanoidMotionContributions
            => _importedHumanoidContributions.Items;

        public int HumanoidMotionContributionCapacity
            => _importedHumanoidContributions.Capacity;

        /// <summary>
        /// True when the current evaluation produced more active humanoid leaves than
        /// the graph's preallocated contribution contract can represent.
        /// </summary>
        public bool HumanoidMotionContributionsOverflowed
            => _importedHumanoidContributions.Overflowed;

        /// <summary>
        /// Shared slot layout describing the number of slots per type.
        /// Built during <see cref="Initialize"/> after all curves are registered.
        /// </summary>
        [MemoryPackIgnore]
        internal AnimationSlotLayout? SlotLayout { get; private set; }

        /// <summary>
        /// Dense array of all unique animated members, built during slot assignment.
        /// Used for O(1) iteration in <see cref="ApplyAnimationValues"/>.
        /// </summary>
        [MemoryPackIgnore]
        private AnimationMember[] _animatedMembersArray = [];

        public void Initialize(object? rootObject)
        {
            foreach (var layer in Layers)
                layer?.Initialize(this, rootObject);

            // After all motions are initialized and _animatedCurves is fully populated,
            // assign dense slot indices and size all typed stores.
            AssignSlots();
        }

        /// <summary>
        /// Builds the slot layout from all registered animated curves, assigns a dense typed slot
        /// to each unique AnimationMember, and sizes every store in the tree.
        /// </summary>
        private void AssignSlots()
        {
            RegisterImportedHumanoidMirroredBindings();
            var layout = new AnimationSlotLayout();
            var slotsByPath = new Dictionary<string, AnimSlot>(StringComparer.Ordinal);

            // Logical paths, rather than AnimationMember object identity, define slots. Different
            // clips commonly own distinct member instances for the same target path and must write
            // into the same state-machine slot when they blend.
            foreach (var kvp in _animatedCurves)
            {
                var member = kvp.Value;
                var type = member.DetermineValueType();
                AnimSlot slot = layout.AllocateSlot(type);
                member.Slot = slot;
                slotsByPath.Add(kvp.Key, slot);
            }

            layout.QuaternionFloatGroups = BuildQuaternionFloatSlotGroups(slotsByPath, _animatedCurves);
            SlotLayout = layout;

            // Build dense member array for ApplyAnimationValues
            _animatedMembersArray = [.. _animatedCurves.Values.Distinct()];

            // Size our own store
            ValueStore.Resize(layout);

            // Propagate layout to all layers and all motions in the tree
            int stateMachineContributionCapacity = 0;
            foreach (var layer in Layers)
            {
                if (layer is null)
                    continue;

                layer.SlotLayout = layout;
                layer.ValueStore.Resize(layout);

                int layerContributionCapacity = 0;
                foreach (var state in layer.States)
                {
                    PropagateLayoutToMotion(state?.Motion, layout, slotsByPath);
                    int stateContributionCapacity = state?.Motion?.PrepareImportedHumanoidContributionCapacity() ?? 0;
                    state?.PrepareRuntimeEvaluation(layout, stateContributionCapacity);
                    layerContributionCapacity += stateContributionCapacity;
                }
                int transitionContributionCapacity = checked(layerContributionCapacity * 2);
                layer.PrepareImportedHumanoidContributionCapacity(transitionContributionCapacity);
                stateMachineContributionCapacity += transitionContributionCapacity;
            }
            _importedHumanoidContributions.EnsureCapacity(stateMachineContributionCapacity);
        }

        private static void PropagateLayoutToMotion(
            MotionBase? motion,
            AnimationSlotLayout layout,
            IReadOnlyDictionary<string, AnimSlot> slotsByPath)
        {
            if (motion is null)
                return;

            foreach ((string path, AnimationMember member) in motion.AnimatedCurves)
                if (slotsByPath.TryGetValue(path, out AnimSlot slot))
                    member.Slot = slot;

            motion.SlotLayout = layout;
            motion.ValueStore.Resize(layout);
            motion.AnimatedMembersArray = [.. motion.AnimatedCurves.Values.Distinct()];

            // Recurse into blend tree children
            switch (motion)
            {
                case AnimationClip clip:
                    clip.PrepareImportedHumanoidMirrorSlotBindings(layout, slotsByPath);
                    clip.PrepareImportedHumanoidScalarQuaternionBindings(layout);
                    clip.PrepareAdditivePoseEvaluation(layout);
                    break;
                case BlendTree1D bt1d:
                    foreach (var child in bt1d.Children)
                        PropagateLayoutToMotion(child.Motion, layout, slotsByPath);
                    bt1d.PrepareRuntimeEvaluation(layout);
                    break;
                case BlendTree2D bt2d:
                    foreach (var child in bt2d.Children)
                        PropagateLayoutToMotion(child.Motion, layout, slotsByPath);
                    bt2d.PrepareRuntimeEvaluation(layout);
                    break;
                case BlendTreeDirect btd:
                    foreach (var child in btd.Children)
                        PropagateLayoutToMotion(child.Motion, layout, slotsByPath);
                    btd.PrepareRuntimeEvaluation(layout);
                    break;
            }
        }

        internal static AnimationQuaternionFloatSlotGroup[] BuildQuaternionFloatSlotGroups(
            IReadOnlyDictionary<string, AnimSlot> slotsByPath)
            => BuildQuaternionFloatSlotGroups(slotsByPath, membersByPath: null);

        private static AnimationQuaternionFloatSlotGroup[] BuildQuaternionFloatSlotGroups(
            IReadOnlyDictionary<string, AnimSlot> slotsByPath,
            IReadOnlyDictionary<string, AnimationMember>? membersByPath)
        {
            var groupsByTargetPath = new Dictionary<string, int[]>(StringComparer.Ordinal);
            foreach ((string path, AnimSlot slot) in slotsByPath)
            {
                if (slot.Type != EAnimValueType.Float
                    || !TryGetQuaternionFloatComponent(path, out string targetPath, out int componentIndex))
                    continue;

                if (!groupsByTargetPath.TryGetValue(targetPath, out int[]? indices))
                {
                    indices = [-1, -1, -1, -1];
                    groupsByTargetPath.Add(targetPath, indices);
                }
                indices[componentIndex] = slot.TypeIndex;
            }

            var result = new List<AnimationQuaternionFloatSlotGroup>(groupsByTargetPath.Count);
            foreach (int[] indices in groupsByTargetPath.Values)
            {
                var group = new AnimationQuaternionFloatSlotGroup(
                    indices[0],
                    indices[1],
                    indices[2],
                    indices[3]);
                if (group.IsValid)
                    result.Add(group);
            }

            if (membersByPath is null)
                return [.. result];

            Dictionary<ImportedAnimationQuaternionBindingKey, int[]> sourceBindingGroups = [];
            foreach ((string path, AnimationMember member) in membersByPath)
            {
                if (!slotsByPath.TryGetValue(path, out AnimSlot slot)
                    || slot.Type != EAnimValueType.Float
                    || member.MemberType != EAnimationMemberType.Method
                    || member.MemberName != "SetUnityAnimationFloat"
                    || member.MethodArguments.Length == 0
                    || member.MethodArguments[0] is not ImportedAnimationBindingDescriptor binding
                    || !ImportedAnimationQuaternionBindingKey.TryCreate(binding, out ImportedAnimationQuaternionBindingKey key))
                    continue;

                if (!sourceBindingGroups.TryGetValue(key, out int[]? indices))
                {
                    indices = [-1, -1, -1, -1];
                    sourceBindingGroups.Add(key, indices);
                }
                indices[binding.Component] = slot.TypeIndex;
            }

            foreach (int[] indices in sourceBindingGroups.Values)
            {
                var group = new AnimationQuaternionFloatSlotGroup(
                    indices[0],
                    indices[1],
                    indices[2],
                    indices[3]);
                if (group.IsValid)
                    result.Add(group);
            }
            return [.. result];
        }

        private static bool TryGetQuaternionFloatComponent(
            string path,
            out string targetPath,
            out int componentIndex)
        {
            for (int stemIndex = 0; stemIndex < QuaternionComponentMethodStems.Length; stemIndex++)
            {
                string stem = QuaternionComponentMethodStems[stemIndex];
                for (int index = 0; index < 4; index++)
                {
                    char suffix = index switch
                    {
                        0 => 'X',
                        1 => 'Y',
                        2 => 'Z',
                        _ => 'W',
                    };
                    string memberName = $"{stem}{suffix}";
                    int memberIndex = path.LastIndexOf(memberName, StringComparison.Ordinal);
                    if (memberIndex < 0)
                        continue;

                    int suffixStart = memberIndex + memberName.Length;
                    if (suffixStart >= path.Length || path[suffixStart] != ':')
                        continue;

                    targetPath = string.Concat(
                        path.AsSpan(0, memberIndex),
                        stem,
                        path.AsSpan(suffixStart));
                    componentIndex = index;
                    return true;
                }
            }

            const string propertyStem = "Quaternion";
            for (int index = 0; index < 4; index++)
            {
                char suffix = index switch
                {
                    0 => 'X',
                    1 => 'Y',
                    2 => 'Z',
                    _ => 'W',
                };
                string propertyName = $"{propertyStem}{suffix}";
                if (!path.EndsWith(propertyName, StringComparison.Ordinal))
                    continue;

                targetPath = path[..^1];
                componentIndex = index;
                return true;
            }

            targetPath = string.Empty;
            componentIndex = -1;
            return false;
        }

        public void Deinitialize()
        {
            foreach (var layer in Layers)
                layer?.Deinitialize();

            SlotLayout = null;
            _animatedMembersArray = [];
            _animatedCurves.Clear();
            _importedHumanoidContributions.Clear();
        }

        internal void NotifyHumanoidMotionContinuityChanged(EAnimMotionContinuityChange change)
        {
            LastHumanoidMotionContinuityChange = change;
            _humanoidMotionContinuityVersion = unchecked(_humanoidMotionContinuityVersion + 1UL);
        }

        public void ResetAnimatedState()
        {
            // Restore default values via typed store
            if (SlotLayout is not null)
            {
                foreach (var member in _animatedMembersArray)
                    member.ApplyAnimationValue(member.DefaultValue);

                ValueStore.Clear();
                foreach (var layer in Layers)
                {
                    if (layer is null) continue;
                    layer.ValueStore.Clear();
                }
            }
            else
            {
                // Legacy path
                var restoredMembers = new HashSet<AnimationMember>();
                foreach (var member in _animatedCurves.Values)
                {
                    if (!restoredMembers.Add(member))
                        continue;
                    member.ApplyAnimationValue(member.DefaultValue);
                }

                lock (_animationValuesLock)
                    _animationValues.Clear();

                foreach (var layer in Layers)
                {
                    if (layer is null) continue;
                    lock (layer._animationValuesLock)
                        layer._animatedValues.Clear();
                }
            }
        }

        public void EvaluationTick(object? rootObject, float delta)
        {
            EvaluateAnimationValues(rootObject, delta);
            ApplyAnimationValues();
        }

        /// <summary>
        /// Evaluates layers and blends their typed values without applying them to bound members.
        /// This lets runtime integrations establish one atomic humanoid body transaction around
        /// the final, already-blended sample.
        /// </summary>
        public void EvaluateAnimationValues(object? rootObject, float delta)
        {
            InitializeValueStoreFromDefaults();
            _importedHumanoidContributions.Clear();
            for (int i = 0; i < Layers.Count; ++i)
            {
                AnimLayer layer = Layers[i];
                layer.EvaluationTick(rootObject, delta, Variables);
                CombineAnimationValues(layer);
                CombineImportedHumanoidMotionContributions(layer);
            }
        }

        private void RegisterImportedHumanoidMirroredBindings()
        {
            KeyValuePair<string, AnimationMember>[] registered = [.. _animatedCurves];
            for (int i = 0; i < registered.Length; i++)
            {
                (string sourcePath, AnimationMember sourceMember) = registered[i];
                if (!AnimationClip.TryCreateImportedHumanoidMirroredBinding(
                        sourcePath,
                        sourceMember,
                        out string mirroredPath,
                        out AnimationMember? mirroredMember)
                    || mirroredMember is null
                    || _animatedCurves.ContainsKey(mirroredPath))
                    continue;

                _animatedCurves.Add(mirroredPath, mirroredMember);
            }
        }

        public void EvaluationTick(object? rootObject, long deltaTicks)
        {
            EvaluateAnimationValues(rootObject, deltaTicks);
            ApplyAnimationValues();
        }

        /// <inheritdoc cref="EvaluateAnimationValues(object?, float)"/>
        public void EvaluateAnimationValues(object? rootObject, long deltaTicks)
        {
            InitializeValueStoreFromDefaults();
            _importedHumanoidContributions.Clear();
            for (int i = 0; i < Layers.Count; ++i)
            {
                AnimLayer layer = Layers[i];
                layer.EvaluationTick(rootObject, deltaTicks, Variables);
                CombineAnimationValues(layer);
                CombineImportedHumanoidMotionContributions(layer);
            }
        }

        private void CombineImportedHumanoidMotionContributions(AnimLayer layer)
        {
            float layerWeight = float.IsFinite(layer.Weight)
                ? Math.Clamp(layer.Weight, 0.0f, 1.0f)
                : 0.0f;
            if (layerWeight <= 0.0f)
                return;

            if (layer.ApplyType == EApplyType.Additive)
            {
                _importedHumanoidContributions.AppendScaled(
                    layer.HumanoidContributions,
                    layerWeight,
                    EHumanoidMotionContributionType.Additive);
                return;
            }

            _importedHumanoidContributions.AttenuateOverride(1.0f - layerWeight);
            _importedHumanoidContributions.AppendScaled(
                layer.HumanoidContributions,
                layerWeight,
                EHumanoidMotionContributionType.Override);
        }

        /// <summary>
        /// Seeks the current and transitioning motions on every layer to one exact time.
        /// </summary>
        public void SeekActiveMotions(float timeSeconds, bool collectEvents = false)
        {
            for (int i = 0; i < Layers.Count; i++)
                Layers[i]?.SeekActiveMotionPlayback(timeSeconds, collectEvents);
        }

        /// <summary>Raised in deterministic layer, state, leaf, and source-event order.</summary>
        public event Action<ImportedAnimationEventOccurrence>? ImportedAnimationEventTriggered;

        internal void DispatchImportedAnimationEvents(AnimState? state)
        {
            if (state is null || state.ImportedAnimationEvents.Count == 0)
                return;

            foreach (ref readonly ImportedAnimationEventOccurrence occurrence in state.ImportedAnimationEvents.Items)
                ImportedAnimationEventTriggered?.Invoke(occurrence with { StateName = state.Name });
            state.ImportedAnimationEvents.Clear();
        }

        private void CombineAnimationValues(AnimLayer layer)
        {
            float layerWeight = float.IsFinite(layer.Weight)
                ? Math.Clamp(layer.Weight, 0.0f, 1.0f)
                : 0.0f;
            if (layerWeight <= 0.0f)
                return;

            // Typed store path: single bulk operation, no locks, no string hashing
            if (SlotLayout is not null && layer.SlotLayout is not null)
            {
                bool additive = layer.ApplyType == EApplyType.Additive;
                if (additive)
                    ValueStore.AddFrom(layer.ValueStore, layerWeight);
                else
                    ValueStore.OverrideFrom(layer.ValueStore, layerWeight);
                return;
            }

            // Legacy path: snapshot keys, merge with locks
            string[] currLayerKeys;
            lock (layer._animationValuesLock)
            {
                currLayerKeys = [.. layer._animatedValues.Keys];
            }

            bool additiveLegacy = layer.ApplyType == EApplyType.Additive;

            lock (_animationValuesLock)
            {
                foreach (var key in currLayerKeys)
                {
                    object? layerValue;
                    lock (layer._animationValuesLock)
                    {
                        if (!layer._animatedValues.TryGetValue(key, out layerValue))
                            continue;
                    }

                    if (_animationValues.TryGetValue(key, out object? currentValue))
                    {
                        _animationValues[key] = additiveLegacy
                            ? AddValues(currentValue, layerValue)
                            : layerValue;
                    }
                    else
                    {
                        _animationValues.TryAdd(key, layerValue);
                    }
                }
            }
        }

        private void InitializeValueStoreFromDefaults()
        {
            ValueStore.Clear();
            if (SlotLayout is null)
                return;

            for (int i = 0; i < _animatedMembersArray.Length; i++)
                _animatedMembersArray[i].WriteDefaultToStore(ValueStore);
        }

        private static object? AddValues(object? currentValue, object? layerValue) => currentValue switch
        {
            float currentFloat when layerValue is float layerFloat => currentFloat + layerFloat,
            Vector2 currentVector2 when layerValue is Vector2 layerVector2 => currentVector2 + layerVector2,
            Vector3 currentVector when layerValue is Vector3 layerVector => currentVector + layerVector,
            Vector4 currentVector4 when layerValue is Vector4 layerVector4 => currentVector4 + layerVector4,
            Quaternion currentQuaternion when layerValue is Quaternion layerQuaternion => currentQuaternion * layerQuaternion,
            _ => currentValue,
        };

        public void ApplyAnimationValues()
        {
            // Typed store path: iterate dense member array, apply from store (no boxing, no string lookup)
            if (SlotLayout is not null)
            {
                foreach (var member in _animatedMembersArray)
                    member.ApplyFromStore(ValueStore);
                return;
            }

            // Legacy path
            KeyValuePair<string, object?>[] snapshot;
            lock (_animationValuesLock)
            {
                snapshot = [.. _animationValues];
            }
            
            foreach (var kvp in snapshot)
                if (_animatedCurves.TryGetValue(kvp.Key, out var member))
                    member.ApplyAnimationValue(kvp.Value);
        }

        [MemoryPackIgnore]
        private EventDictionary<string, AnimVar> _variables = [];
        [MemoryPackIgnore]
        public EventDictionary<string, AnimVar> Variables
        {
            get => _variables;
            set => SetField(ref _variables, value);
        }

        // Persist variables as a dictionary; restore EventDictionary behavior on load.
        [MemoryPackInclude]
        private Dictionary<string, AnimVar> SerializedVariables
        {
            get => new(_variables);
            set => Variables = [.. value ?? []];
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Variables):
                        UnlinkVariables(Variables);
                        break;
                }
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Variables):
                    LinkVariables(Variables);
                    break;
            }
        }

        private void LinkVariables(EventDictionary<string, AnimVar>? variables)
        {
            if (variables is null)
                return;

            // "PostAnythingAdded/Removed" equivalents for EventDictionary.
            variables.Added += Variables_Added;
            variables.Removed += Variables_Removed;
            variables.Set += Variables_Set;
            variables.Cleared += Variables_Cleared;

            foreach (var kvp in variables)
                AttachVariable(kvp.Value);

            // Initial build (only when the Variables collection instance changes).
            HashToName.Clear();
            _hashToNames.Clear();
            _hashCollisionBucketCount = 0;
            _parameterIndexDirty = true;
            _orderedParameterNames = [];
            _parameterNameToIndex.Clear();
            foreach (var kvp in variables)
                AddHashName(kvp.Value);
        }

        private void UnlinkVariables(EventDictionary<string, AnimVar>? variables)
        {
            if (variables is null)
                return;

            variables.Added -= Variables_Added;
            variables.Removed -= Variables_Removed;
            variables.Set -= Variables_Set;
            variables.Cleared -= Variables_Cleared;

            foreach (var kvp in variables)
                DetachVariable(kvp.Value);

            HashToName.Clear();
            _hashToNames.Clear();
            _hashCollisionBucketCount = 0;
            _parameterIndexDirty = true;
            _orderedParameterNames = [];
            _parameterNameToIndex.Clear();
        }

        private void AttachVariable(AnimVar? variable)
        {
            if (variable is null)
                return;

            variable.StateMachine = this;
            variable.PropertyChanged -= Value_PropertyChanged;
            variable.PropertyChanged += Value_PropertyChanged;
        }

        private void DetachVariable(AnimVar? variable)
        {
            if (variable is null)
                return;

            variable.PropertyChanged -= Value_PropertyChanged;
            if (ReferenceEquals(variable.StateMachine, this))
                variable.StateMachine = null;
        }

        private void Variables_Added(string key, AnimVar value)
        {
            AttachVariable(value);
            AddHashName(value);
            MarkParameterIndexDirty();
        }

        private void Variables_Removed(string key, AnimVar value)
        {
            // EventDictionary.Clear() fires Removed for each item after the backing dictionary
            // is already empty. Make clear/removal O(1) for our hash maps.
            if (Variables.Count == 0)
            {
                HashToName.Clear();
                _hashToNames.Clear();
                _hashCollisionBucketCount = 0;
                _parameterIndexDirty = true;
                _orderedParameterNames = [];
                _parameterNameToIndex.Clear();
            }
            else
            {
                RemoveHashName(value);
                MarkParameterIndexDirty();
            }
            DetachVariable(value);
        }

        private void Variables_Set(string key, AnimVar oldValue, AnimVar newValue)
        {
            RemoveHashName(oldValue);
            DetachVariable(oldValue);
            AttachVariable(newValue);
            AddHashName(newValue);
            MarkParameterIndexDirty();
        }

        private void Variables_Cleared()
        {
            HashToName.Clear();
            _hashToNames.Clear();
            _hashCollisionBucketCount = 0;
            _parameterIndexDirty = true;
            _orderedParameterNames = [];
            _parameterNameToIndex.Clear();
        }

        private void Value_PropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (sender is AnimVar variable)
            {
                if (e.PropertyName == nameof(AnimVar.ParameterName) &&
                    e.PreviousValue is string oldName &&
                    e.NewValue is string newName)
                {
                    UpdateHashToNameOnRename(variable, oldName, newName);
                }
                VariableChanged?.Invoke(variable);
            }
        }

        /// <summary>
        /// Invokes the VariableChanged event for all variables in the state machine.
        /// </summary>
        public void InvokeAllVariablesChanged()
        {
            foreach (var variable in Variables)
                VariableChanged?.Invoke(variable.Value);
        }

        [MemoryPackIgnore]
        public XREvent<AnimVar>? VariableChanged;

        /// <summary>
        /// If true, animations that animate the root object will move this transform.
        /// </summary>
        public bool ApplyRootMotion
        {
            get => _applyRootMotion;
            set => SetField(ref _applyRootMotion, value);
        }
        public Vector3 PivotPosition
        {
            get => _pivotPosition;
            set => SetField(ref _pivotPosition, value);
        }
        public Vector3 DeltaPosition
        {
            get => _deltaPosition;
            set => SetField(ref _deltaPosition, value);
        }

        public Dictionary<ushort, string> HashToName { get; } = [];

        // Collision-aware map: a hash may map to multiple names.
        [MemoryPackIgnore]
        private readonly Dictionary<ushort, SortedSet<string>> _hashToNames = [];

        [MemoryPackIgnore]
        private int _hashCollisionBucketCount;

        public bool HasAnyHashCollisions => _hashCollisionBucketCount > 0;

        public bool HasHashCollision(ushort hash)
            => _hashToNames.TryGetValue(hash, out var names) && names.Count > 1;

        public IReadOnlyCollection<string> GetNamesForHash(ushort hash)
            => _hashToNames.TryGetValue(hash, out var names) ? names : [];

        [MemoryPackIgnore]
        private bool _parameterIndexDirty = true;

        [MemoryPackIgnore]
        private int _parameterSchemaVersion;

        public int ParameterSchemaVersion => _parameterSchemaVersion;

        [MemoryPackIgnore]
        private string[] _orderedParameterNames = [];

        [MemoryPackIgnore]
        private readonly Dictionary<string, int> _parameterNameToIndex = new(StringComparer.Ordinal);

        private void MarkParameterIndexDirty()
        {
            _parameterIndexDirty = true;
            _parameterSchemaVersion++;
        }

        private void EnsureParameterIndexCache()
        {
            if (!_parameterIndexDirty)
                return;

            var names = new List<string>(Variables.Count);
            foreach (var kvp in Variables)
            {
                if (kvp.Value is null)
                    continue;
                names.Add(kvp.Value.ParameterName);
            }

            names.Sort(StringComparer.Ordinal);
            _orderedParameterNames = [.. names];

            _parameterNameToIndex.Clear();
            for (int i = 0; i < _orderedParameterNames.Length; i++)
            {
                // Be defensive: if duplicates exist, keep the first index.
                _parameterNameToIndex.TryAdd(_orderedParameterNames[i], i);
            }

            _parameterIndexDirty = false;
        }

        public int ParameterNameIdBitCount
        {
            get
            {
                EnsureParameterIndexCache();
                return GetMinimalBitCountForCount(_orderedParameterNames.Length);
            }
        }

        public IReadOnlyList<string> GetOrderedParameterNamesSnapshot()
        {
            EnsureParameterIndexCache();
            return _orderedParameterNames;
        }

        public IReadOnlyList<AnimParameterSchemaEntry> GetOrderedParameterSchemaSnapshot()
        {
            EnsureParameterIndexCache();

            // Build a mapping from ParameterName -> (key, var) since dictionary keys may not track renames.
            var map = new Dictionary<string, AnimVar>(StringComparer.Ordinal);
            foreach (var kvp in Variables)
            {
                if (kvp.Value is null)
                    continue;
                map.TryAdd(kvp.Value.ParameterName, kvp.Value);
            }

            var result = new List<AnimParameterSchemaEntry>(_orderedParameterNames.Length);
            foreach (var name in _orderedParameterNames)
            {
                if (!map.TryGetValue(name, out var var))
                    continue;

                if (var is AnimBool b)
                    result.Add(new AnimParameterSchemaEntry(name, AnimParameterType.Bool, b.Value, 0, 0f));
                else if (var is AnimInt i)
                    result.Add(new AnimParameterSchemaEntry(name, AnimParameterType.Int, false, i.Value, 0f));
                else if (var is AnimFloat f)
                    result.Add(new AnimParameterSchemaEntry(name, AnimParameterType.Float, false, 0, f.Value));
            }

            return result;
        }

        public void ApplyReplicatedParameterSchema(IEnumerable<AnimParameterSchemaEntry> schemaEntries, int schemaVersion)
        {
            // Apply (create/update) parameters so that indexed replication can resolve names to actual vars.
            var names = new List<string>();
            foreach (var entry in schemaEntries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                names.Add(entry.Name);

                // Find existing variable by ParameterName (key may differ).
                string? existingKey = null;
                AnimVar? existingVar = null;
                foreach (var kvp in Variables)
                {
                    if (kvp.Value is null)
                        continue;
                    if (string.Equals(kvp.Value.ParameterName, entry.Name, StringComparison.Ordinal))
                    {
                        existingKey = kvp.Key;
                        existingVar = kvp.Value;
                        break;
                    }
                }

                AnimVar? desired = existingVar;
                bool typeMatches = entry.Type switch
                {
                    AnimParameterType.Bool => existingVar is AnimBool,
                    AnimParameterType.Int => existingVar is AnimInt,
                    AnimParameterType.Float => existingVar is AnimFloat,
                    _ => false,
                };

                if (!typeMatches)
                {
                    if (existingKey is not null)
                        Variables.Remove(existingKey);

                    desired = entry.Type switch
                    {
                        AnimParameterType.Bool => new AnimBool(entry.Name, entry.BoolDefault),
                        AnimParameterType.Int => new AnimInt(entry.Name, entry.IntDefault),
                        AnimParameterType.Float => new AnimFloat(entry.Name, entry.FloatDefault),
                        _ => null,
                    };

                    if (desired is not null)
                        Variables[entry.Name] = desired;
                }
                else if (existingKey is not null && !string.Equals(existingKey, entry.Name, StringComparison.Ordinal))
                {
                    // Normalize key to match ParameterName so CHANGE_INDEX lookups work.
                    Variables.Remove(existingKey);
                    Variables[entry.Name] = desired!;
                }
            }

            names.Sort(StringComparer.Ordinal);
            _orderedParameterNames = [.. names];

            _parameterNameToIndex.Clear();
            for (int i = 0; i < _orderedParameterNames.Length; i++)
                _parameterNameToIndex.TryAdd(_orderedParameterNames[i], i);

            // Override any local bumps caused by Variables mutations during apply.
            _parameterIndexDirty = false;
            _parameterSchemaVersion = schemaVersion;
        }

        public bool TryGetParameterIndex(string parameterName, out int index)
        {
            EnsureParameterIndexCache();
            return _parameterNameToIndex.TryGetValue(parameterName, out index);
        }

        public bool TryGetParameterNameByIndex(int index, out string? parameterName)
        {
            EnsureParameterIndexCache();
            if ((uint)index < (uint)_orderedParameterNames.Length)
            {
                parameterName = _orderedParameterNames[index];
                return true;
            }
            parameterName = null;
            return false;
        }

        private void AddHashName(AnimVar? variable)
        {
            if (variable is null)
                return;
            AddHashName(variable.Hash, variable.ParameterName);
        }

        private void AddHashName(ushort hash, string name)
        {
            if (!_hashToNames.TryGetValue(hash, out var names))
            {
                names = new SortedSet<string>(StringComparer.Ordinal);
                _hashToNames.Add(hash, names);
            }

            int beforeCount = names.Count;
            if (names.Add(name))
            {
                // Transition from 1 -> 2 means this hash now has a collision.
                if (beforeCount == 1)
                    _hashCollisionBucketCount++;
            }

            // Preserve existing primary mapping if present; otherwise seed it.
            if (!HashToName.ContainsKey(hash))
                HashToName[hash] = name;
        }

        private void RemoveHashName(AnimVar? variable)
        {
            if (variable is null)
                return;
            RemoveHashName(variable.Hash, variable.ParameterName);
        }

        private void RemoveHashName(ushort hash, string name)
        {
            if (!_hashToNames.TryGetValue(hash, out var names))
                return;

            int beforeCount = names.Count;
            if (!names.Remove(name))
                return;

            // Transition from 2 -> 1 means this hash is no longer colliding.
            if (beforeCount == 2)
                _hashCollisionBucketCount--;

            if (names.Count == 0)
                _hashToNames.Remove(hash);

            if (!HashToName.TryGetValue(hash, out var primary) || primary != name)
                return;

            if (names.Count > 0)
            {
                foreach (var replacement in names)
                {
                    HashToName[hash] = replacement;
                    return;
                }
            }
            else
            {
                HashToName.Remove(hash);
            }
        }

        private void UpdateHashToNameOnRename(AnimVar variable, string oldName, string newName)
        {
            ushort oldHash = AnimVar.CreateSmallHash(oldName);
            ushort newHash = AnimVar.CreateSmallHash(newName);

            RemoveHashName(oldHash, oldName);
            AddHashName(newHash, newName);

            // If the rename changed the primary entry for oldHash, RemoveHashName handled it.
            // Ensure the renamed variable is always represented as primary for its new hash.
            HashToName[newHash] = newName;

            MarkParameterIndexDirty();
        }

        public void SetInt(string index, int value)
        {
            if (Variables.TryGetValue(index, out AnimVar? var))
                var.IntValue = value;
        }

        public void SetFloat(string index, float value)
        {
            if (Variables.TryGetValue(index, out AnimVar? var))
                var.FloatValue = value;
        }

        public void SetBool(string index, bool value)
        {
            if (Variables.TryGetValue(index, out AnimVar? var))
                var.BoolValue = value;
        }

        public AnimStateTransition? GetCurrentTransition(int layerIndex)
        {
            if (layerIndex < 0 || layerIndex >= Layers.Count)
                return null;
            var layer = Layers[layerIndex];
            if (layer is null)
                return null;
            return layer.CurrentTransition;
        }

        public void NewFloat(string name, float defaultValue)
        {
            if (Variables.TryGetValue(name, out AnimVar? var))
                var.FloatValue = defaultValue;
            else
                Variables.Add(name, new AnimFloat(name, defaultValue));
        }

        public void NewInt(string name, int defaultValue)
        {
            if (Variables.TryGetValue(name, out AnimVar? var))
                var.IntValue = defaultValue;
            else
                Variables.Add(name, new AnimInt(name, defaultValue));
        }

        public void NewBool(string name, bool defaultValue)
        {
            if (Variables.TryGetValue(name, out AnimVar? var))
                var.BoolValue = defaultValue;
            else
                Variables.Add(name, new AnimBool(name, defaultValue));
        }

        public void DeleteVariable(string name)
        {
            Variables.Remove(name);
        }

        public void DeleteAllVariables()
        {
            Variables.Clear();
        }

        public void ResetVariableStates()
        {
            foreach (var variable in Variables)
            {
                if (variable.Value is AnimBool)
                    variable.Value.BoolValue = false;
                else if (variable.Value is AnimFloat)
                    variable.Value.FloatValue = 0.0f;
                else if (variable.Value is AnimInt)
                    variable.Value.IntValue = 0;
            }
        }
    }
}
