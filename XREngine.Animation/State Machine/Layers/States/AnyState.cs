
namespace XREngine.Animation
{
    public class AnyState : AnimStateBase
    {
        public override string ToString() => "AnyState";

        public AnyState() { }
        public AnyState(params AnimStateTransition[] transitions) : base(transitions) { }
        public AnyState(IEnumerable<AnimStateTransition> transitions) : base(transitions) { }
        public AnyState(EventList<AnimStateTransition> transitions) : base(transitions) { }
    }
}
