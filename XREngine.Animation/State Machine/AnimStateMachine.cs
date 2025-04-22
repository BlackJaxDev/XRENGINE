using System.Numerics;
using XREngine.Core.Files;

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

        public void Initialize()
        {
            foreach (var layer in Layers)
                layer?.Initialize(this);
        }

        public void Deinitialize()
        {
            foreach (var layer in Layers)
                layer?.Deinitialize();
        }

        public void EvaluationTick(object? rootObject, float delta)
        {
            for (int i = 0; i < Layers.Count; ++i)
                Layers[i].EvaluationTick(rootObject, delta, Variables);
        }

        private EventDictionary<string, AnimVar> _variables = [];
        public EventDictionary<string, AnimVar> Variables
        {
            get => _variables;
            set => SetField(ref _variables, value);
        }
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
                Variables[name] = new AnimFloat(defaultValue);
        }

        public void NewInt(string name, int defaultValue)
        {
            if (Variables.TryGetValue(name, out AnimVar? var))
                var.IntValue = defaultValue;
            else
                Variables[name] = new AnimInt(defaultValue);
        }

        public void NewBool(string name, bool defaultValue)
        {
            if (Variables.TryGetValue(name, out AnimVar? var))
                var.BoolValue = defaultValue;
            else
                Variables[name] = new AnimBool(defaultValue);
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
