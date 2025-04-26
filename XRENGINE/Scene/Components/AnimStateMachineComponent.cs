using XREngine.Animation;
using XREngine.Components;

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
    }
}
