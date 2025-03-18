using System.Collections.Concurrent;

namespace XREngine.Data.Animation
{
    public class StateMachine
    {
        private ConcurrentDictionary<string, IAnimationState> _states;
        private IAnimationState? _currentState;

        public StateMachine()
            => _states = new ConcurrentDictionary<string, IAnimationState>();

        public void AddState(IAnimationState state)
            => _states.TryAdd(state.Name, state);

        public void SwitchState(string newStateName)
        {
            if (!_states.TryGetValue(newStateName, out IAnimationState? newState))
                return;
            
            _currentState?.Exit();
            _currentState = newState;
            _currentState.Enter();
        }

        public IAnimationState? RemoveState(string stateName)
        {
            _states.Remove(stateName, out IAnimationState? state);
            return state;
        }

        public bool RemoveState(string stateName, out IAnimationState? state)
            => _states.Remove(stateName, out state);

        public void Update(float deltaTime)
            => _currentState?.Update(deltaTime);

        private void CheckTransitions()
        {
            if (_currentState is not null)
                foreach (var transition in _currentState.Transitions)
                    if (transition?.CheckConditions() ?? false)
                    {
                        SwitchState(transition.TargetState.Name);
                        break;
                    }
        }
    }
}
