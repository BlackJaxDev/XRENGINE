using System.Collections.Generic;
using System.Numerics;
using MemoryPack;
using XREngine.Core.Files;
using XREngine.Data.Core;
using static XREngine.Animation.AnimLayer;

namespace XREngine.Animation
{
    [MemoryPackable]
    public partial class AnimStateMachine : XRAsset
    {
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

        public void Initialize(object? rootObject)
        {
            foreach (var layer in Layers)
                layer?.Initialize(this, rootObject);
        }

        public void Deinitialize()
        {
            foreach (var layer in Layers)
                layer?.Deinitialize();
        }

        public void EvaluationTick(object? rootObject, float delta)
        {
            for (int i = 0; i < Layers.Count; ++i)
            {
                AnimLayer layer = Layers[i];
                layer.EvaluationTick(rootObject, delta, Variables);
                CombineAnimationValues(layer);
            }
            ApplyAnimationValues();
        }

        private void CombineAnimationValues(AnimLayer layer)
        {
            //Merge animation paths from the last layer into this layer
            // Take a snapshot of keys to avoid concurrent modification
            string[] currLayerKeys;
            lock (layer._animationValuesLock)
            {
                currLayerKeys = [.. layer._animatedValues.Keys];
            }

            //First layer is always the initial setter, can't be additive
            bool additive = layer.ApplyType == EApplyType.Additive;

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

                    //Does the value already exist?
                    if (_animationValues.TryGetValue(key, out object? currentValue))
                    {
                        _animationValues[key] = additive
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

        private static object? AddValues(object? currentValue, object? layerValue) => currentValue switch
        {
            float currentFloat when layerValue is float layerFloat => currentFloat + layerFloat,
            Vector2 currentVector2 when layerValue is Vector2 layerVector2 => currentVector2 + layerVector2,
            Vector3 currentVector when layerValue is Vector3 layerVector => currentVector + layerVector,
            Vector4 currentVector4 when layerValue is Vector4 layerVector4 => currentVector4 + layerVector4,
            Quaternion currentQuaternion when layerValue is Quaternion layerQuaternion => currentQuaternion * layerQuaternion,
            _ => currentValue, //Discrete value, just override it
        };

        public void ApplyAnimationValues()
        {
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
