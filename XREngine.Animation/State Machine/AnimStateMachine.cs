using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data.Core;
using static XREngine.Animation.AnimLayer;

namespace XREngine.Animation
{
    public class AnimStateMachine : XRAsset
    {
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
        
        protected internal Dictionary<string, object?> _defaultValues = [];
        protected internal Dictionary<string, object?> _animationValues = [];
        protected internal readonly Dictionary<string, AnimationMember> _animatedCurves = [];

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
            IEnumerable<string> currLayerKeys = layer._animatedValues.Keys;

            //First layer is always the initial setter, can't be additive
            bool additive = layer.ApplyType == EApplyType.Additive;

            foreach (var key in currLayerKeys)
            {
                //Does the value already exist?
                if (_animationValues.TryGetValue(key, out object? currentValue))
                {
                    if (!layer._animatedValues.TryGetValue(key, out var layerValue))
                        continue;
                    
                    _animationValues[key] = additive
                        ? AddValues(currentValue, layerValue)
                        : layerValue;
                }
                else if (layer._animatedValues.TryGetValue(key, out var layerValue))
                    _animationValues.Add(key, layerValue);
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
            foreach (var kvp in _animationValues)
                if (_animatedCurves.TryGetValue(kvp.Key, out var member))
                    member.ApplyAnimationValue(kvp.Value);
        }

        private EventDictionary<string, AnimVar> _variables = [];
        public EventDictionary<string, AnimVar> Variables
        {
            get => _variables;
            set => SetField(ref _variables, value);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Variables):
                        foreach (KeyValuePair<string, AnimVar> variable in Variables)
                            variable.Value.PropertyChanged -= Value_PropertyChanged;
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
                    foreach (KeyValuePair<string, AnimVar> variable in Variables)
                        variable.Value.PropertyChanged += Value_PropertyChanged;
                    break;
            }
        }

        private void Value_PropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (sender is AnimVar variable)
                VariableChanged?.Invoke(variable);
        }

        /// <summary>
        /// Invokes the VariableChanged event for all variables in the state machine.
        /// </summary>
        public void InvokeAllVariablesChanged()
        {
            foreach (var variable in Variables)
                VariableChanged?.Invoke(variable.Value);
        }

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
