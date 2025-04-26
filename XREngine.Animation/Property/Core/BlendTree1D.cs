using XREngine.Data.Core;

namespace XREngine.Animation
{
    public class BlendTree1D : BlendTree
    {
        public override string ToString()
            => $"BlendTree1D: {Name} ({ParameterName})";

        private string _parameterName = string.Empty;
        public string ParameterName
        {
            get => _parameterName;
            set => SetField(ref _parameterName, value);
        }

        public class Child : XRBase
        {
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

        public override void Tick(float delta)
        {
            foreach (var child in Children)
                child.Motion?.Tick(delta * child.Speed);
        }

        public override void BlendChildMotionAnimationValues(IDictionary<string, AnimVar> variables, float weight)
        {
            if (_children.Count == 0)
                return;

            if (_children.Count == 1)
            {
                _children[0].Motion?.GetAnimationValues(this, variables, 1.0f);
                return;
            }

            if (_needsSort)
            {
                _needsSort = false;
                _children.Sort(_childComparer);
            }

            float parameterValue = variables.TryGetValue(ParameterName, out AnimVar? var) ? var.FloatValue : 0.0f;

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
                int r = Children.Count - 1;
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

            Blend(min.Motion, max.Motion, (parameterValue - min.Threshold) / (max.Threshold - min.Threshold), variables, weight);
        }
    }
}
