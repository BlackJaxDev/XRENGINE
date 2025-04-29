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
            RegisterTick(ETickGroup.Normal, ETickOrder.Animation, EvaluationTick);
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, EvaluationTick);
            StateMachine.Deinitialize();
        }

        protected internal void EvaluationTick()
            => StateMachine.EvaluationTick(this, Engine.Delta);

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
