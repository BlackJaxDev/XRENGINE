using MemoryPack;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class BlendTreeDirect : BlendTree
    {
        public override string ToString()
            => $"BlendTreeDirect: {Name}";

        [MemoryPackable]
        public partial class Child : XRBase
        {
            [MemoryPackIgnore]
            internal AnimationValueStore RuntimeValueStore { get; } = new();

            private Guid _motionOccurrenceId = Guid.NewGuid();
            public Guid MotionOccurrenceId
            {
                get => _motionOccurrenceId;
                set => SetField(ref _motionOccurrenceId, value == Guid.Empty ? Guid.NewGuid() : value);
            }

            private MotionBase? _motion;
            /// <summary>
            /// The motion to play when this child is active.
            /// </summary>
            public MotionBase? Motion
            {
                get => _motion;
                set => SetField(ref _motion, value);
            }

            private string? _weightParameterName = null;
            /// <summary>
            /// The name of the parameter that controls the weight of this motion.
            /// If null, the weight is 1.0f.
            /// </summary>
            public string? WeightParameterName
            {
                get => _weightParameterName;
                set => SetField(ref _weightParameterName, value);
            }

            private float _speed = 1.0f;
            /// <summary>
            /// The speed at which the motion plays back.
            /// </summary>
            public float Speed
            {
                get => _speed;
                set => SetField(ref _speed, value);
            }

            private float _cycleOffset;
            /// <summary>
            /// Normalized phase offset applied to this child occurrence.
            /// </summary>
            public float CycleOffset
            {
                get => _cycleOffset;
                set => SetField(ref _cycleOffset, value);
            }

            private bool _humanoidMirror = false;
            /// <summary>
            /// Whether or not to mirror the motion for humanoid characters.
            /// </summary>
            public bool HumanoidMirror
            {
                get => _humanoidMirror;
                set => SetField(ref _humanoidMirror, value);
            }
        }

        private List<Child> _children = [];
        public List<Child> Children
        {
            get => _children;
            set => SetField(ref _children, value);
        }

        [MemoryPackIgnore]
        private AnimationValueStore?[] _activeChildStores = [];

        [MemoryPackIgnore]
        private float[] _activeChildWeights = [];

        internal void PrepareRuntimeEvaluation(AnimationSlotLayout layout)
        {
            _activeChildStores = Children.Count > 0
                ? new AnimationValueStore?[Children.Count]
                : [];
            _activeChildWeights = Children.Count > 0
                ? new float[Children.Count]
                : [];
            for (int i = 0; i < Children.Count; i++)
                Children[i].RuntimeValueStore.Resize(layout);
        }

        public override void SetDefaults()
        {
            base.SetDefaults();
            foreach (var child in Children)
                child.Motion?.SetDefaults();
        }

        public override void Tick(float delta)
        {
            foreach (var child in Children)
                child.Motion?.Tick(delta * child.Speed);
        }

        public override void Tick(long deltaTicks)
        {
            foreach (var child in Children)
                child.Motion?.Tick(ScaleStopwatchTicks(deltaTicks, child.Speed));
        }

        public override void BlendChildMotionAnimationValues(IDictionary<string, AnimVar> variables, float weight)
            => BlendChildMotionAnimationValues(variables, weight, null, additive: false);

        internal void EvaluateAnimationValuesAtNormalizedStateTimeCore(
            IDictionary<string, AnimVar> variables,
            double normalizedTime,
            bool additive)
        {
            ValueStore.Clear();
            BlendChildMotionAnimationValues(variables, 1.0f, normalizedTime, additive);
        }

        private void BlendChildMotionAnimationValues(
            IDictionary<string, AnimVar> variables,
            float weight,
            double? normalizedTime,
            bool additive)
        {
            if (SlotLayout is null)
            {
                foreach (Child child in Children)
                    child.Motion?.GetAnimationValues(this, variables, weight * ReadChildWeight(child, variables));
                return;
            }

            if (_activeChildStores.Length != Children.Count)
                throw new InvalidOperationException(
                    "BlendTreeDirect children changed after initialization; reinitialize the owning state machine.");

            int activeCount = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                Child child = Children[i];
                float childWeight = ReadChildWeight(child, variables);
                if (childWeight <= 0.0f || child.Motion is not MotionBase motion)
                    continue;

                if (normalizedTime is double time)
                    motion.EvaluateAnimationValuesAtNormalizedStateTime(
                        variables,
                        ResolveChildNormalizedPhase(time, child.Speed, child.CycleOffset),
                        additive);
                else
                    motion.GetAnimationValues(null, variables, 1.0f);
                child.RuntimeValueStore.CopyFrom(motion.ValueStore);
                _activeChildStores[activeCount] = child.RuntimeValueStore;
                _activeChildWeights[activeCount] = childWeight;
                activeCount++;
            }

            AnimationValueStore.WeightedBlend(
                _activeChildStores,
                _activeChildWeights,
                activeCount,
                NormalizeBlendValues,
                ValueStore);
        }

        internal double GetEffectiveDurationSecondsCore(IDictionary<string, AnimVar> variables)
        {
            double weightedDuration = 0.0;
            double totalWeight = 0.0;
            for (int i = 0; i < Children.Count; i++)
            {
                Child child = Children[i];
                float childWeight = ReadChildWeight(child, variables);
                if (childWeight <= 0.0f || child.Motion is not MotionBase motion)
                    continue;
                if (!float.IsFinite(child.Speed) || MathF.Abs(child.Speed) <= float.Epsilon)
                    return double.PositiveInfinity;

                double duration = motion.GetEffectiveDurationSeconds(variables) / Math.Abs(child.Speed);
                if (double.IsPositiveInfinity(duration))
                    return double.PositiveInfinity;
                weightedDuration += duration * childWeight;
                totalWeight += childWeight;
            }
            return totalWeight > double.Epsilon ? weightedDuration / totalWeight : 0.0;
        }
    }
}
