using MemoryPack;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class BlendTree1D : BlendTree
    {
        public override string ToString()
            => $"BlendTree1D: {Name} ({ParameterName})";

        private string _parameterName = string.Empty;
        public string ParameterName
        {
            get => _parameterName;
            set => SetField(ref _parameterName, value);
        }

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
            public MotionBase? Motion
            {
                get => _motion;
                set => SetField(ref _motion, value);
            }

            private float _speed = 1.0f;
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

            private float _threshold = 0.0f;
            public float Threshold
            {
                get => _threshold;
                set => SetField(ref _threshold, value);
            }

            private bool _humanoidMirror = false;
            public bool HumanoidMirror
            {
                get => _humanoidMirror;
                set => SetField(ref _humanoidMirror, value);
            }
        }

        private EventList<Child> _children = [];
        public EventList<Child> Children
        {
            get => _children;
            set => SetField(ref _children, value);
        }

        internal void PrepareRuntimeEvaluation(AnimationSlotLayout layout)
        {
            for (int i = 0; i < Children.Count; i++)
                Children[i].RuntimeValueStore.Resize(layout);
        }

        [MemoryPackIgnore]
        private readonly Comparer<Child> _childComparer = Comparer<Child>.Create((a, b) => a.Threshold.CompareTo(b.Threshold));

        private bool _needsSort = true;
        public bool NeedsSort
        {
            get => _needsSort;
            private set => SetField(ref _needsSort, value);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Children):
                        if (_children is null)
                            return change;
                        for (int i = 0; i < _children.Count; i++)
                            _children[i].PropertyChanged -= Child_PropertyChanged;
                        _children.PostAnythingAdded -= ChildAdded;
                        _children.PostAnythingRemoved -= ChildRemoved;
                        break;
                }
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Children):
                    NeedsSort = true;
                    if (_children is null)
                        return;
                    for (int i = 0; i < _children.Count; i++)
                        _children[i].PropertyChanged += Child_PropertyChanged;
                    _children.PostAnythingAdded += ChildAdded;
                    _children.PostAnythingRemoved += ChildRemoved;
                    break;
            }
        }

        private void ChildAdded(Child item)
        {
            item.PropertyChanged += Child_PropertyChanged;
            NeedsSort = true;
        }
        private void ChildRemoved(Child item)
        {
            item.PropertyChanged -= Child_PropertyChanged;
            NeedsSort = true;
        }

        private void Child_PropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Child.Threshold))
                NeedsSort = true;
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
            if (_children.Count == 0)
                return;

            if (_children.Count == 1)
            {
                CopyChildAnimationValues(_children[0], variables, weight, normalizedTime, additive);
                return;
            }

            if (_needsSort)
            {
                _needsSort = false;
                _children.Sort(_childComparer);
            }

            float parameterValue = variables.TryGetValue(ParameterName, out AnimVar? var) ? var.FloatValue : 0.0f;

            if (!float.IsFinite(parameterValue) || parameterValue <= _children[0].Threshold)
            {
                CopyChildAnimationValues(_children[0], variables, weight, normalizedTime, additive);
                return;
            }

            int lastIndex = _children.Count - 1;
            if (parameterValue >= _children[lastIndex].Threshold)
            {
                CopyChildAnimationValues(_children[lastIndex], variables, weight, normalizedTime, additive);
                return;
            }

            Child min, max;

            if (_children.Count == 2)
            {
                min = _children[0];
                max = _children[1];
            }
            else
            {
                // Binary search to find the index just above the parameter value
                int l = 0;
                int r = lastIndex;
                int m;
                while (l < r)
                {
                    m = (l + r) / 2;
                    if (Children[m].Threshold < parameterValue)
                        l = m + 1;
                    else
                        r = m;
                }

                // If exact match, use just this motion
                //if (Children[l].Threshold == parameterValue)
                //{
                //    Children[l].Tick(rootObject, delta, variables, weight);
                //    return;
                //}

                // For blending between thresholds, min should be the LOWER threshold
                // l now points to the motion with threshold >= parameterValue
                min = Children[l - 1];
                max = Children[l];
            }

            float interval = max.Threshold - min.Threshold;
            if (!float.IsFinite(interval) || MathF.Abs(interval) <= float.Epsilon)
            {
                CopyChildAnimationValues(min, variables, weight, normalizedTime, additive);
                return;
            }

            float blend = Math.Clamp((parameterValue - min.Threshold) / interval, 0.0f, 1.0f);
            BlendChildAnimationValues(min, max, blend, variables, weight, normalizedTime, additive);
        }

        private void CopyChildAnimationValues(
            Child child,
            IDictionary<string, AnimVar> variables,
            float weight,
            double? normalizedTime,
            bool additive)
        {
            if (SlotLayout is null)
            {
                child.Motion?.GetAnimationValues(this, variables, weight);
                return;
            }

            if (!EvaluateChildAnimationValues(child, variables, normalizedTime, additive))
                return;
            ValueStore.CopyFrom(child.RuntimeValueStore);
        }

        private void BlendChildAnimationValues(
            Child first,
            Child second,
            float blend,
            IDictionary<string, AnimVar> variables,
            float weight,
            double? normalizedTime,
            bool additive)
        {
            if (SlotLayout is null)
            {
                Blend(first.Motion, second.Motion, blend, variables, weight);
                return;
            }

            bool hasFirst = EvaluateChildAnimationValues(first, variables, normalizedTime, additive);
            bool hasSecond = EvaluateChildAnimationValues(second, variables, normalizedTime, additive);
            if (!hasFirst)
            {
                if (hasSecond)
                    ValueStore.CopyFrom(second.RuntimeValueStore);
                return;
            }
            if (!hasSecond)
            {
                ValueStore.CopyFrom(first.RuntimeValueStore);
                return;
            }

            AnimationValueStore.Lerp(
                first.RuntimeValueStore,
                second.RuntimeValueStore,
                blend,
                ValueStore);
        }

        private static bool EvaluateChildAnimationValues(
            Child child,
            IDictionary<string, AnimVar> variables,
            double? normalizedTime,
            bool additive)
        {
            if (child.Motion is not MotionBase motion)
                return false;

            if (normalizedTime is double time)
                motion.EvaluateAnimationValuesAtNormalizedStateTime(
                    variables,
                    ResolveChildNormalizedPhase(time, child.Speed, child.CycleOffset),
                    additive);
            else
                motion.GetAnimationValues(null, variables, 1.0f);
            child.RuntimeValueStore.CopyFrom(motion.ValueStore);
            return true;
        }

        internal double GetEffectiveDurationSecondsCore(IDictionary<string, AnimVar> variables)
        {
            if (_children.Count == 0)
                return 0.0;

            if (_needsSort)
            {
                _needsSort = false;
                _children.Sort(_childComparer);
            }

            float parameterValue = variables.TryGetValue(ParameterName, out AnimVar? variable)
                ? variable.FloatValue
                : 0.0f;
            if (!float.IsFinite(parameterValue) || parameterValue <= _children[0].Threshold)
                return GetChildDuration(_children[0], variables);

            int lastIndex = _children.Count - 1;
            if (parameterValue >= _children[lastIndex].Threshold)
                return GetChildDuration(_children[lastIndex], variables);

            int upperIndex = 1;
            while (upperIndex < _children.Count && _children[upperIndex].Threshold < parameterValue)
                upperIndex++;

            Child lower = _children[upperIndex - 1];
            Child upper = _children[upperIndex];
            float interval = upper.Threshold - lower.Threshold;
            if (!float.IsFinite(interval) || MathF.Abs(interval) <= float.Epsilon)
                return GetChildDuration(lower, variables);

            float upperWeight = Math.Clamp((parameterValue - lower.Threshold) / interval, 0.0f, 1.0f);
            return BlendChildDurations(
                GetChildDuration(lower, variables),
                1.0f - upperWeight,
                GetChildDuration(upper, variables),
                upperWeight);
        }

        private static double GetChildDuration(Child child, IDictionary<string, AnimVar> variables)
        {
            if (child.Motion is not MotionBase motion)
                return 0.0;
            if (!float.IsFinite(child.Speed) || MathF.Abs(child.Speed) <= float.Epsilon)
                return double.PositiveInfinity;
            return motion.GetEffectiveDurationSeconds(variables) / Math.Abs(child.Speed);
        }

        private static double BlendChildDurations(double first, float firstWeight, double second, float secondWeight)
        {
            if ((firstWeight > 0.0f && double.IsPositiveInfinity(first))
                || (secondWeight > 0.0f && double.IsPositiveInfinity(second)))
                return double.PositiveInfinity;

            double totalWeight = Math.Max(0.0f, firstWeight) + Math.Max(0.0f, secondWeight);
            return totalWeight > double.Epsilon
                ? (first * Math.Max(0.0f, firstWeight) + second * Math.Max(0.0f, secondWeight)) / totalWeight
                : 0.0;
        }
    }
}
