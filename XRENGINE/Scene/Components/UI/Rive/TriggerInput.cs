namespace XREngine.Scene.Components.UI
{
    public class TriggerInput : StateMachineInput
    {
        public void Fire()
        {
            if (!string.IsNullOrEmpty(Target) && RivePlayer.TryGetTarget(out var rivePlayer))
                rivePlayer.FireTrigger(Target);
        }

        // Triggers don't have any persistent data to apply.
        protected override void Apply(RiveUIComponent? rivePlayer, string inputName) { }
    }
}
