using XREngine.Data.Core;
using XREngine.Core.Attributes;

namespace XREngine.Rendering.UI
{
    // This base class wraps a custom, named state machine input value.
    [XRTypeRedirect("XREngine.Scene.Components.UI.StateMachineInput")]
    public abstract class StateMachineInput : XRBase
    {
        private string? _target;
        public string? Target
        {
            get => _target;  // Must be null-checked before use.
            set
            {
                if (SetField(ref _target, value))
                    Apply();
            }
        }

        private WeakReference<RiveUIComponent?> _rivePlayer = new(null);
        protected WeakReference<RiveUIComponent?> RivePlayer => _rivePlayer;

        // Sets _rivePlayer to the given rivePlayer object and applies our input value to the state
        // machine. Does nothing if _rivePlayer was already equal to rivePlayer.
        internal void SetRivePlayer(WeakReference<RiveUIComponent?> rivePlayer)
        {
            _rivePlayer = rivePlayer;
            Apply();
        }

        protected void Apply()
        {
            if (!string.IsNullOrEmpty(_target) && _rivePlayer.TryGetTarget(out var rivePlayer))
                Apply(rivePlayer, _target);
        }

        // Applies our input value to the rivePlayer's state machine.
        // rivePlayer and inputName are guaranteed to not be null or empty.
        protected abstract void Apply(RiveUIComponent? rivePlayer, string inputName);
    }
}
