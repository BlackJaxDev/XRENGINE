using Extensions;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Scene.Components.Animation;

namespace XREngine.Scene.Components
{
    public class AnimStateMachineComponent : XRComponent
    {
        private AnimStateMachine _stateMachine = new();
        public AnimStateMachine StateMachine
        {
            get => _stateMachine;
            set => SetField(ref _stateMachine, value);
        }

        private HumanoidComponent? _humanoid;
        public HumanoidComponent? Humanoid
        {
            get => _humanoid;
            set => SetField(ref _humanoid, value);
        }

        private HumanoidComponent? GetHumanoidComponent()
            => Humanoid ?? (TryGetSiblingComponent<HumanoidComponent>(out var humanoid) ? humanoid : null);

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            StateMachine.Initialize(this);
            StateMachine.VariableChanged += VariableChanged;
            RegisterTick(ETickGroup.Normal, ETickOrder.Animation, EvaluationTick);
        }

        private readonly HashSet<AnimVar> _changedLastEval = [];

        private void VariableChanged(AnimVar? var)
        {
            if (var is null)
                return;

            _changedLastEval.Add(var);
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, EvaluationTick);
            StateMachine.Deinitialize();
            StateMachine.VariableChanged -= VariableChanged;
        }

        protected internal void EvaluationTick()
        {
            StateMachine.EvaluationTick(this, Engine.Delta);
            ReplicateModifiedVariables();
            _changedLastEval.Clear();
        }

        private void ReplicateModifiedVariables()
        {
            int bitCount = 0;
            foreach (var variable in _changedLastEval)
            {
                if (variable is null)
                    continue;

                bitCount += variable.CalcBitCount();
            }
            if (bitCount == 0)
                return;

            byte[] data = new byte[bitCount.Align(8) / 8];
            int bitOffset = 0;
            foreach (var variable in _changedLastEval)
                variable?.WriteBits(data, ref bitOffset);
            
            EnqueueDataReplication("PARAMS", data, false, true);
        }

        public override void ReceiveData(string id, object? data)
        {
            base.ReceiveData(id, data);
        }

        public void SetFloat(string name, float value)
        {
            var sm = StateMachine;
            if (sm.Variables.TryGetValue(name, out var variable))
                variable.FloatValue = value;
        }
        public void SetInt(string name, int value)
        {
            var sm = StateMachine;
            if (sm.Variables.TryGetValue(name, out var variable))
                variable.IntValue = value;
        }
        public void SetBool(string name, bool value)
        {
            var sm = StateMachine;
            if (sm.Variables.TryGetValue(name, out var variable))
                variable.BoolValue = value;
        }

        public void SetHumanoidValue(EHumanoidValue name, float value)
            => GetHumanoidComponent()?.SetValue(name, value);
    }
}
