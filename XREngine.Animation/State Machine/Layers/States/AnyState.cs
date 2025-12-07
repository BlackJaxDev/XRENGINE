
using MemoryPack;

namespace XREngine.Animation
{
    [MemoryPackable]
    public partial class AnyState : AnimStateBase
    {
        public override string ToString() => "AnyState";

        [MemoryPackConstructor]
        public AnyState() { }
        public AnyState(params AnimStateTransition[] transitions) : base(transitions) { }
        public AnyState(IEnumerable<AnimStateTransition> transitions) : base(transitions) { }
        public AnyState(EventList<AnimStateTransition> transitions) : base(transitions) { }
    }
}
