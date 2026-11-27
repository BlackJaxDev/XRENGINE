using XREngine.Extensions;
using MemoryPack;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class BlendTree2D : BlendTree
    {
        public override string ToString()
            => $"BlendTree2D: {Name} ({XParameterName}, {YParameterName})";

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

            private float _positionX = 0.0f;
            /// <summary>
            /// The X position of the motion in the blend tree.
            /// </summary>
            public float PositionX
            {
                get => _positionX;
                set => SetField(ref _positionX, value);
            }

            private float _positionY = 0.0f;
            /// <summary>
            /// The Y position of the motion in the blend tree.
            /// </summary>
            public float PositionY
            {
                get => _positionY;
                set => SetField(ref _positionY, value);
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

        internal void PrepareRuntimeEvaluation(AnimationSlotLayout layout)
        {
            EnsureScratchCapacity(_children.Count);
            for (int i = 0; i < Children.Count; i++)
                Children[i].RuntimeValueStore.Resize(layout);
        }

        [MemoryPackIgnore]
        private Child[] _sortedByX = [];
        [MemoryPackIgnore]
        private Child[] _sortedByY = [];
        [MemoryPackIgnore]
        private bool _needsSort = true;

        // Pre-allocated arrays to store bounding children
        [MemoryPackIgnore]
        private readonly Child?[] _boundingChildren = new Child?[4];
        [MemoryPackIgnore]
        private int _boundingChildCount;

        // Pre-allocated weight and blend storage (sized to Children.Count for zero per-frame allocations)
        private struct ChildWeight
        {
            public Child Child;
            public float Weight;
        }
        [MemoryPackIgnore]
        private ChildWeight[] _childWeights = [];
        [MemoryPackIgnore]
        private int _weightCount;
        [MemoryPackIgnore]
        private AnimationValueStore?[] _activeChildStores = [];
        [MemoryPackIgnore]
        private float[] _activeChildWeights = [];
        [MemoryPackIgnore]
        private readonly Child?[] _directionalChildren = new Child?[4];
        [MemoryPackIgnore]
        private readonly float[] _directionalAngleDistances = new float[4];
        [MemoryPackIgnore]
        private readonly float[] _directionalWeights = new float[3];

        private void EnsureScratchCapacity(int childCount)
        {
            int required = Math.Max(4, childCount);
            if (_childWeights.Length < required)
                _childWeights = new ChildWeight[required];
            if (_activeChildStores.Length < required)
                _activeChildStores = new AnimationValueStore?[required];
            if (_activeChildWeights.Length < required)
                _activeChildWeights = new float[required];
        }

        private string _xParameterName = string.Empty;
        public string XParameterName
        {
            get => _xParameterName;
            set => SetField(ref _xParameterName, value);
        }

        private string _yParameterName = string.Empty;
        public string YParameterName
        {
            get => _yParameterName;
            set => SetField(ref _yParameterName, value);
        }

        public enum EBlendType
        {
            /// <summary>
            /// The nearest 4 children are used to calculate a bilinear blend.
            /// </summary>
            Cartesian,
            /// <summary>
            /// The nearest 3 children are used to calculate a barycentric blend.
            /// </summary>
            Barycentric,
            /// <summary>
            /// A directional blend using angular distances relative to the origin (0, 0).
            /// </summary>
            Directional,
            /// <summary>
            /// Unity 2D Freeform Cartesian blend using Cartesian Gradient Band Interpolation.
            /// </summary>
            FreeformCartesian,
            /// <summary>
            /// Unity 2D Freeform Directional blend using Polar Gradient Band Interpolation.
            /// </summary>
            FreeformDirectional,
            /// <summary>
            /// Unity 2D Simple Directional blend using Polar Gradient Band Interpolation.
            /// </summary>
            SimpleDirectional,
        }

        private EBlendType _blendType = EBlendType.Barycentric;
        public EBlendType BlendType
        {
            get => _blendType;
            set => SetField(ref _blendType, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Children):
                    UpdateSortedArrays();
                    break;
            }
        }

        private void UpdateSortedArrays()
        {
            _needsSort = false;
            EnsureScratchCapacity(_children.Count);
            if (_children.Count == 0)
            {
                _sortedByX = [];
                _sortedByY = [];
            }
            else
            {
                // Allocations are fine during setup, just not during Tick
                _sortedByX = new Child[_children.Count];
                _sortedByY = new Child[_children.Count];

                for (int i = 0; i < _children.Count; i++)
                {
                    _sortedByX[i] = _children[i];
                    _sortedByY[i] = _children[i];
                }

                Array.Sort(_sortedByX, (a, b) => a.PositionX.CompareTo(b.PositionX));
                Array.Sort(_sortedByY, (a, b) => a.PositionY.CompareTo(b.PositionY));
            }
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
            float x = 0.0f;
            float y = 0.0f;
            if (variables.TryGetValue(XParameterName, out AnimVar? xVar))
                x = xVar.FloatValue;
            if (variables.TryGetValue(YParameterName, out AnimVar? yVar))
                y = yVar.FloatValue;

            if (!TryCalculateWeights(x, y))
                return;

            if (_weightCount == 1)
            {
                CopyChildAnimationValues(_childWeights[0].Child, variables, weight, normalizedTime, additive);
                return;
            }

            BlendWeightedChildAnimationValues(variables, weight, normalizedTime, additive);
        }

        internal bool TryCalculateWeights(float x, float y)
        {
            x = float.IsFinite(x) ? x : 0.0f;
            y = float.IsFinite(y) ? y : 0.0f;

            if (_needsSort || _sortedByX.Length != _children.Count || _sortedByY.Length != _children.Count)
                UpdateSortedArrays();
            EnsureScratchCapacity(_children.Count);

            _weightCount = 0;
            if (_children.Count == 0)
                return false;

            if (_children.Count == 1)
            {
                _childWeights[_weightCount++] = new ChildWeight { Child = _children[0], Weight = 1.0f };
                return true;
            }

            switch (BlendType)
            {
                case EBlendType.FreeformCartesian:
                    CalculateCartesianGradientBandWeights(x, y);
                    return _weightCount > 0;
                case EBlendType.FreeformDirectional:
                case EBlendType.SimpleDirectional:
                    CalculatePolarGradientBandWeights(x, y);
                    return _weightCount > 0;
            }

            if (_children.Count == 2)
            {
                if (BlendType == EBlendType.Cartesian)
                    CalculateLinearWeightsNoBounding(x, y);
                else
                    CalculateInverseDistanceWeightsNoBounding(x, y);
                return _weightCount > 0;
            }

            FindBoundingChildren(x, y);
            if (_boundingChildCount == 0)
                return false;

            switch (BlendType)
            {
                case EBlendType.Barycentric:
                    CalculateBaryCentricWeights(x, y);
                    break;
                case EBlendType.Cartesian:
                    CalculateCartesianWeights(x, y);
                    break;
                case EBlendType.Directional:
                    CalculateDirectionalWeights(x, y);
                    break;
            }

            return _weightCount > 0;
        }

        private void BlendWeightedChildAnimationValues(
            IDictionary<string, AnimVar> variables,
            float weight,
            double? normalizedTime,
            bool additive)
        {
            if (SlotLayout is null)
            {
                switch (_weightCount)
                {
                    case 1:
                        _childWeights[0].Child.Motion?.GetAnimationValues(this, variables, weight);
                        return;
                    case 2:
                        Blend(
                            _childWeights[0].Child.Motion,
                            _childWeights[1].Child.Motion,
                            _childWeights[1].Weight,
                            variables, weight);
                        return;
                    case 3:
                        Blend(
                            _childWeights[0].Child.Motion, _childWeights[0].Weight,
                            _childWeights[1].Child.Motion, _childWeights[1].Weight,
                            _childWeights[2].Child.Motion, _childWeights[2].Weight,
                            variables, weight);
                        return;
                    case 4:
                        Blend(
                            _childWeights[0].Child.Motion, _childWeights[0].Weight,
                            _childWeights[1].Child.Motion, _childWeights[1].Weight,
                            _childWeights[2].Child.Motion, _childWeights[2].Weight,
                            _childWeights[3].Child.Motion, _childWeights[3].Weight,
                            variables, weight);
                        return;
                    default:
                        for (int i = 0; i < _weightCount; i++)
                            _childWeights[i].Child.Motion?.GetAnimationValues(this, variables, weight * _childWeights[i].Weight);
                        return;
                }
            }

            for (int i = 0; i < _weightCount; i++)
                EvaluateChildAnimationValues(_childWeights[i].Child, variables, normalizedTime, additive);

            switch (_weightCount)
            {
                case 1:
                    ValueStore.CopyFrom(_childWeights[0].Child.RuntimeValueStore);
                    return;
                case 2:
                    AnimationValueStore.Lerp(
                        _childWeights[0].Child.RuntimeValueStore,
                        _childWeights[1].Child.RuntimeValueStore,
                        _childWeights[1].Weight,
                        ValueStore);
                    return;
                case 3:
                    AnimationValueStore.TriLerp(
                        _childWeights[0].Child.RuntimeValueStore,
                        _childWeights[1].Child.RuntimeValueStore,
                        _childWeights[2].Child.RuntimeValueStore,
                        _childWeights[0].Weight,
                        _childWeights[1].Weight,
                        _childWeights[2].Weight,
                        ValueStore);
                    return;
                case 4:
                    AnimationValueStore.QuadLerp(
                        _childWeights[0].Child.RuntimeValueStore,
                        _childWeights[1].Child.RuntimeValueStore,
                        _childWeights[2].Child.RuntimeValueStore,
                        _childWeights[3].Child.RuntimeValueStore,
                        _childWeights[0].Weight,
                        _childWeights[1].Weight,
                        _childWeights[2].Weight,
                        _childWeights[3].Weight,
                        ValueStore);
                    return;
                default:
                    int activeCount = 0;
                    for (int i = 0; i < _weightCount; i++)
                    {
                        float childWeight = _childWeights[i].Weight;
                        if (childWeight <= 0.0f || _childWeights[i].Child.Motion is null)
                            continue;
                        _activeChildStores[activeCount] = _childWeights[i].Child.RuntimeValueStore;
                        _activeChildWeights[activeCount] = childWeight;
                        activeCount++;
                    }
                    AnimationValueStore.WeightedBlend(
                        _activeChildStores,
                        _activeChildWeights,
                        activeCount,
                        normalizeWeights: true,
                        ValueStore);
                    return;
            }
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

            if (EvaluateChildAnimationValues(child, variables, normalizedTime, additive))
                ValueStore.CopyFrom(child.RuntimeValueStore);
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
            float x = variables.TryGetValue(XParameterName, out AnimVar? xVariable)
                ? xVariable.FloatValue
                : 0.0f;
            float y = variables.TryGetValue(YParameterName, out AnimVar? yVariable)
                ? yVariable.FloatValue
                : 0.0f;

            if (!TryCalculateWeights(x, y))
                return 0.0;
            if (_weightCount == 1)
                return GetChildDuration(_childWeights[0].Child, variables);

            double weightedDuration = 0.0;
            double totalWeight = 0.0;
            for (int i = 0; i < _weightCount; i++)
            {
                float childWeight = Math.Max(0.0f, _childWeights[i].Weight);
                if (childWeight <= 0.0f)
                    continue;
                double duration = GetChildDuration(_childWeights[i].Child, variables);
                if (double.IsPositiveInfinity(duration))
                    return double.PositiveInfinity;
                weightedDuration += duration * childWeight;
                totalWeight += childWeight;
            }
            return totalWeight > double.Epsilon ? weightedDuration / totalWeight : 0.0;
        }

        private static double GetChildDuration(Child child, IDictionary<string, AnimVar> variables)
        {
            if (child.Motion is not MotionBase motion)
                return 0.0;
            if (!float.IsFinite(child.Speed) || MathF.Abs(child.Speed) <= float.Epsilon)
                return double.PositiveInfinity;
            return motion.GetEffectiveDurationSeconds(variables) / Math.Abs(child.Speed);
        }

        private void CalculateLinearWeightsNoBounding(float x, float y)
        {
            _weightCount = 0;
            Child a = _children[0];
            Child b = _children[1];
            // Get positions as vectors
            Vector2 aPos = new(a.PositionX, a.PositionY);
            Vector2 bPos = new(b.PositionX, b.PositionY);
            Vector2 point = new(x, y);
            // Project the point onto the line between a and b
            Vector2 line = bPos - aPos;
            float lineLength = line.Length();
            if (lineLength < float.Epsilon)
            {
                // Points are essentially the same, use only one
                _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = 1.0f };
                return;
            }
            // Normalize the line vector
            Vector2 lineNormalized = line / lineLength;
            // Calculate the projection of (point - aPos) onto the normalized line
            Vector2 toPoint = point - aPos;
            float projectionLength = Vector2.Dot(toPoint, lineNormalized);
            // Calculate normalized position along the line (0 = at a, 1 = at b)
            float t = Math.Clamp(projectionLength / lineLength, 0, 1);
            // Calculate weights for a and b
            _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = 1.0f - t };
            _childWeights[_weightCount++] = new ChildWeight { Child = b, Weight = t };
        }

        private void CalculateInverseDistanceWeightsNoBounding(float x, float y)
        {
            _weightCount = 0;
            Child a = _children[0];
            Child b = _children[1];

            // Calculate the distance from the point to each child
            float dxA = a.PositionX - x;
            float dyA = a.PositionY - y;
            float distA = MathF.Sqrt(dxA * dxA + dyA * dyA);
            float dxB = b.PositionX - x;
            float dyB = b.PositionY - y;
            float distB = MathF.Sqrt(dxB * dxB + dyB * dyB);

            // If we're exactly on one of the points
            if (distA < float.Epsilon)
            {
                _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = 1.0f };
                return;
            }
            else if (distB < float.Epsilon)
            {
                _childWeights[_weightCount++] = new ChildWeight { Child = b, Weight = 1.0f };
                return;
            }

            // Use inverse distance weighting
            float weightA = 1.0f / distA;
            float weightB = 1.0f / distB;
            float totalWeight = weightA + weightB;

            // Normalize weights
            weightA /= totalWeight;
            weightB /= totalWeight;

            _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = weightA };
            _childWeights[_weightCount++] = new ChildWeight { Child = b, Weight = weightB };
        }

        private void CalculateCartesianGradientBandWeights(float x, float y)
        {
            _weightCount = 0;
            int childCount = _children.Count;
            float weightSum = 0.0f;

            for (int i = 0; i < childCount; i++)
            {
                Child childI = _children[i];
                float ipX = x - childI.PositionX;
                float ipY = y - childI.PositionY;
                float minInfluence = 1.0f;

                for (int j = 0; j < childCount; j++)
                {
                    if (j == i)
                        continue;

                    Child childJ = _children[j];
                    float ijX = childJ.PositionX - childI.PositionX;
                    float ijY = childJ.PositionY - childI.PositionY;
                    float denom = ijX * ijX + ijY * ijY;
                    if (denom <= 1.0e-12f)
                        continue;

                    float h = 1.0f - (ipX * ijX + ipY * ijY) / denom;
                    if (h < minInfluence)
                    {
                        minInfluence = h;
                        if (minInfluence <= 0.0f)
                            break;
                    }
                }

                if (minInfluence > 0.0f)
                {
                    _childWeights[_weightCount++] = new ChildWeight { Child = childI, Weight = minInfluence };
                    weightSum += minInfluence;
                }
            }

            NormalizeOrFallbackToNearest(x, y, weightSum);
        }

        private void CalculatePolarGradientBandWeights(float x, float y)
        {
            const float magEpsilon = 1.0e-6f;
            const float angleWeight = 2.0f;

            _weightCount = 0;
            int childCount = _children.Count;
            Vector2 sample = new(x, y);
            float sampleMag = sample.Length();
            float weightSum = 0.0f;

            for (int i = 0; i < childCount; i++)
            {
                Child childI = _children[i];
                Vector2 posI = new(childI.PositionX, childI.PositionY);
                float magI = posI.Length();
                float minInfluence = 1.0f;

                for (int j = 0; j < childCount; j++)
                {
                    if (j == i)
                        continue;

                    Child childJ = _children[j];
                    Vector2 posJ = new(childJ.PositionX, childJ.PositionY);
                    float magJ = posJ.Length();
                    float avgMag = 0.5f * (magI + magJ);
                    if (avgMag <= magEpsilon)
                        continue;

                    float magIJ = (magJ - magI) / avgMag;
                    float magIP = (sampleMag - magI) / avgMag;
                    float angIJ = magI <= magEpsilon || magJ <= magEpsilon
                        ? 0.0f
                        : SignedAngle(posI, posJ);
                    float angIP = magI <= magEpsilon || sampleMag <= magEpsilon
                        ? 0.0f
                        : SignedAngle(posI, sample);

                    float vijY = angleWeight * angIJ;
                    float vipY = angleWeight * angIP;
                    float denom = magIJ * magIJ + vijY * vijY;
                    if (denom <= 1.0e-12f)
                        continue;

                    float h = 1.0f - (magIP * magIJ + vipY * vijY) / denom;
                    if (h < minInfluence)
                    {
                        minInfluence = h;
                        if (minInfluence <= 0.0f)
                            break;
                    }
                }

                if (minInfluence > 0.0f)
                {
                    _childWeights[_weightCount++] = new ChildWeight { Child = childI, Weight = minInfluence };
                    weightSum += minInfluence;
                }
            }

            NormalizeOrFallbackToNearest(x, y, weightSum);
        }

        private void NormalizeOrFallbackToNearest(float x, float y, float weightSum)
        {
            if (weightSum > 1.0e-12f && float.IsFinite(weightSum))
            {
                float invSum = 1.0f / weightSum;
                for (int i = 0; i < _weightCount; i++)
                    _childWeights[i].Weight *= invSum;
                return;
            }

            _weightCount = 0;
            Child nearest = _children[0];
            float nearestDistSq = float.MaxValue;
            for (int i = 0; i < _children.Count; i++)
            {
                Child candidate = _children[i];
                float dx = candidate.PositionX - x;
                float dy = candidate.PositionY - y;
                float distSq = dx * dx + dy * dy;
                if (distSq < nearestDistSq)
                {
                    nearestDistSq = distSq;
                    nearest = candidate;
                }
            }

            _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
        }

        private static float SignedAngle(Vector2 from, Vector2 to)
        {
            float dot = from.X * to.X + from.Y * to.Y;
            float det = from.X * to.Y - from.Y * to.X;
            return MathF.Atan2(det, dot);
        }

        private void CalculateDirectionalWeights(float x, float y)
        {
            _weightCount = 0;

            // Create a vector from origin (0,0) to the target point
            Vector2 targetDir = new(x, y);
            float targetMagnitude = targetDir.Length();

            // If target is at origin, use the nearest bounding child
            if (targetMagnitude < 0.0001f)
            {
                Child nearest = FindNearestBoundingChild(x, y);
                _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
                return;
            }

            // Normalize the target direction
            Vector2 targetDirNorm = targetDir / targetMagnitude;

            // Find bounding children in the forward hemisphere without heap allocations.
            int directionalCount = 0;
            for (int i = 0; i < _boundingChildCount; i++)
            {
                Child child = _boundingChildren[i]!;
                Vector2 childPos = new(child.PositionX, child.PositionY);
                float childMagnitude = childPos.Length();

                // Skip points at origin
                if (childMagnitude < 0.0001f)
                    continue;

                Vector2 childDirNorm = childPos / childMagnitude;
                float dot = Vector2.Dot(targetDirNorm, childDirNorm);
                float angleDist = MathF.Acos(Math.Clamp(dot, -1.0f, 1.0f));

                if (angleDist <= MathF.PI / 2)
                {
                    // Insertion sort into the 4-element scratch arrays by ascending angle distance.
                    int insert = directionalCount;
                    while (insert > 0 && _directionalAngleDistances[insert - 1] > angleDist)
                    {
                        _directionalChildren[insert] = _directionalChildren[insert - 1];
                        _directionalAngleDistances[insert] = _directionalAngleDistances[insert - 1];
                        insert--;
                    }
                    _directionalChildren[insert] = child;
                    _directionalAngleDistances[insert] = angleDist;
                    directionalCount++;
                }
            }

            // If no children are in the forward hemisphere, use the nearest bounding child
            if (directionalCount == 0)
            {
                Child nearest = FindNearestBoundingChild(x, y);
                _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
                return;
            }

            // Take up to 3 closest directional children
            int count = Math.Min(3, directionalCount);
            float weightSum = 0.0f;

            // Check for exact match
            if (count > 0 && _directionalAngleDistances[0] < EPSILON)
            {
                _childWeights[_weightCount++] = new ChildWeight
                {
                    Child = _directionalChildren[0]!,
                    Weight = 1.0f
                };
                return;
            }

            for (int i = 0; i < count; i++)
            {
                _directionalWeights[i] = 1.0f / (_directionalAngleDistances[i] + EPSILON);
                weightSum += _directionalWeights[i];
            }

            if (weightSum > EPSILON)
            {
                for (int i = 0; i < count; i++)
                {
                    _childWeights[_weightCount++] = new ChildWeight
                    {
                        Child = _directionalChildren[i]!,
                        Weight = _directionalWeights[i] / weightSum
                    };
                }
            }
            else
            {
                _childWeights[_weightCount++] = new ChildWeight
                {
                    Child = _directionalChildren[0]!,
                    Weight = 1.0f
                };
            }
        }

        //private Child FindNearestChild(float x, float y)
        //{
        //    Child nearest = _children[0];
        //    float nearestDistSq = float.MaxValue;

        //    for (int i = 0; i < _children.Count; i++)
        //    {
        //        Child child = _children[i];
        //        float dx = child.PositionX - x;
        //        float dy = child.PositionY - y;
        //        float distSq = dx * dx + dy * dy;

        //        if (distSq < nearestDistSq)
        //        {
        //            nearestDistSq = distSq;
        //            nearest = child;
        //        }
        //    }

        //    return nearest;
        //}

        private int BinarySearchByX(float x)
        {
            int low = 0;
            int high = _sortedByX.Length - 1;
            int leftIdx = -1;
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                if (_sortedByX[mid].PositionX <= x)
                {
                    leftIdx = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }
            return leftIdx;
        }

        private void FindBoundingChildren(float x, float y)
        {
            _boundingChildCount = 0;

            // Early exit if there are not enough children
            if (_children.Count < 3)
                return;

            // Allocation-free binary search for the last child with PositionX <= x.
            int leftIdx = BinarySearchByX(x);

            // Initialize quadrant children
            Child? bottomLeft = null, bottomRight = null, topLeft = null, topRight = null;
            float closestDistSq;

            // Find bottom-left and top-left in one pass
            closestDistSq = float.MaxValue;
            for (int i = 0; i <= leftIdx; i++)
            {
                Child child = _sortedByX[i];
                float dx = child.PositionX - x;
                float dy = child.PositionY - y;
                float distSq = dx * dx + dy * dy;

                if (child.PositionY <= y && (bottomLeft == null || distSq < closestDistSq))
                {
                    bottomLeft = child;
                    closestDistSq = distSq;
                }
                else if (child.PositionY > y && (topLeft == null || distSq < closestDistSq))
                {
                    topLeft = child;
                    closestDistSq = distSq;
                }
            }

            // Find bottom-right and top-right in one pass
            closestDistSq = float.MaxValue;
            for (int i = leftIdx + 1; i < _sortedByX.Length; i++)
            {
                Child child = _sortedByX[i];
                float dx = child.PositionX - x;
                float dy = child.PositionY - y;
                float distSq = dx * dx + dy * dy;

                if (child.PositionY <= y && (bottomRight == null || distSq < closestDistSq))
                {
                    bottomRight = child;
                    closestDistSq = distSq;
                }
                else if (child.PositionY > y && (topRight == null || distSq < closestDistSq))
                {
                    topRight = child;
                    closestDistSq = distSq;
                }
            }

            // Add non-null children to the bounding array
            if (bottomLeft != null)
                _boundingChildren[_boundingChildCount++] = bottomLeft;
            if (bottomRight != null)
                _boundingChildren[_boundingChildCount++] = bottomRight;
            if (topLeft != null)
                _boundingChildren[_boundingChildCount++] = topLeft;
            if (topRight != null)
                _boundingChildren[_boundingChildCount++] = topRight;
        }

        private void CalculateCartesianWeights(float x, float y)
        {
            _weightCount = 0;

            switch (_boundingChildCount)
            {
                case 0:
                    return;
                case 1:
                    _childWeights[_weightCount++] = new ChildWeight { Child = _boundingChildren[0]!, Weight = 1.0f };
                    return;
                case 2:
                    CalculateLinearWeights(x, y);
                    return;
                case 3:
                    CalculateBaryCentricWeights(x, y);
                    return;
            }

            // Sort for bilinear interpolation
            Child bottomLeft = _boundingChildren[0]!;
            Child topRight = _boundingChildren[0]!;
            Child bottomRight = _boundingChildren[0]!;
            Child topLeft = _boundingChildren[0]!;

            float minSum = float.MaxValue;
            float maxSum = float.MinValue;

            // Find bottom-left (min X+Y) and top-right (max X+Y)
            for (int i = 0; i < _boundingChildCount; i++)
            {
                Child child = _boundingChildren[i]!;
                float sum = child.PositionX + child.PositionY;

                if (sum < minSum)
                {
                    minSum = sum;
                    bottomLeft = child;
                }

                if (sum > maxSum)
                {
                    maxSum = sum;
                    topRight = child;
                }
            }

            // Find bottom-right and top-left from the remaining children without allocating an array.
            Child? remaining0 = null;
            Child? remaining1 = null;
            int remainingCount = 0;

            for (int i = 0; i < _boundingChildCount; i++)
            {
                Child child = _boundingChildren[i]!;
                if (child != bottomLeft && child != topRight && remainingCount < 2)
                {
                    if (remainingCount == 0)
                        remaining0 = child;
                    else
                        remaining1 = child;
                    remainingCount++;
                }
            }

            if (remainingCount == 2)
            {
                if (remaining0!.PositionX > remaining1!.PositionX)
                {
                    bottomRight = remaining0;
                    topLeft = remaining1;
                }
                else
                {
                    bottomRight = remaining1;
                    topLeft = remaining0;
                }
            }

            // Calculate bilinear interpolation weights
            float x1 = bottomLeft.PositionX;
            float y1 = bottomLeft.PositionY;
            float x2 = topRight.PositionX;
            float y2 = topRight.PositionY;

            float xDelta = x2 - x1;
            float yDelta = y2 - y1;
            if (Math.Abs(xDelta) < EPSILON || Math.Abs(yDelta) < EPSILON)
            {
                // Degenerate case, just use the nearest child
                Child nearest = FindNearestBoundingChild(x, y);
                _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
                return;
            }

            float normalizedX = (x - x1) / (x2 - x1);
            float normalizedY = (y - y1) / (y2 - y1);

            // Clamp to ensure we're inside the unit square
            normalizedX = Math.Clamp(normalizedX, 0f, 1f);
            normalizedY = Math.Clamp(normalizedY, 0f, 1f);

            _childWeights[_weightCount++] = new ChildWeight
            {
                Child = bottomLeft,
                Weight = (1 - normalizedX) * (1 - normalizedY)
            };
            _childWeights[_weightCount++] = new ChildWeight
            {
                Child = bottomRight,
                Weight = normalizedX * (1 - normalizedY)
            };
            _childWeights[_weightCount++] = new ChildWeight
            {
                Child = topLeft,
                Weight = (1 - normalizedX) * normalizedY
            };
            _childWeights[_weightCount++] = new ChildWeight
            {
                Child = topRight,
                Weight = normalizedX * normalizedY
            };
        }

        private Child FindNearestBoundingChild(float x, float y)
        {
            if (_boundingChildCount == 0)
                return _children[0]; // Fallback if no bounding children

            Child nearest = _boundingChildren[0]!;
            float nearestDistSq = float.MaxValue;

            for (int i = 0; i < _boundingChildCount; i++)
            {
                Child child = _boundingChildren[i]!;
                float dx = child.PositionX - x;
                float dy = child.PositionY - y;
                float distSq = dx * dx + dy * dy;

                if (distSq < nearestDistSq)
                {
                    nearestDistSq = distSq;
                    nearest = child;
                }
            }

            return nearest;
        }

        private void CalculateBaryCentricWeights(float x, float y)
        {
            _weightCount = 0;

            // Handle cases based on number of bounding children
            switch (_boundingChildCount)
            {
                case 0:
                    // No bounding children
                    return;
                case 1:
                    // Only one bounding child, use it with full weight
                    _childWeights[_weightCount++] = new ChildWeight { Child = _boundingChildren[0]!, Weight = 1.0f };
                    return;
                case 2:
                    // Linear interpolation between two points
                    CalculateLinearWeights(x, y);
                    return;
            }

            // The target point
            Vector2 point = new(x, y);

            // Try to find a triangle containing the point
            bool foundTriangle = false;

            // Try all possible triangles from the bounding children
            if (_boundingChildCount >= 3)
            {
                // Try all possible triangle combinations
                for (int i = 0; i < _boundingChildCount && !foundTriangle; i++)
                {
                    for (int j = i + 1; j < _boundingChildCount && !foundTriangle; j++)
                    {
                        for (int k = j + 1; k < _boundingChildCount && !foundTriangle; k++)
                        {
                            Child a = _boundingChildren[i]!;
                            Child b = _boundingChildren[j]!;
                            Child c = _boundingChildren[k]!;

                            Vector2 a2d = new(a.PositionX, a.PositionY);
                            Vector2 b2d = new(b.PositionX, b.PositionY);
                            Vector2 c2d = new(c.PositionX, c.PositionY);

                            if (TryCalculateBarycentric(a, b, c, a2d, b2d, c2d, point))
                                foundTriangle = true;
                        }
                    }
                }
            }

            // If we couldn't find a containing triangle, use the nearest point
            if (!foundTriangle)
            {
                Child nearest = FindNearestBoundingChild(x, y);
                _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
            }
        }

        private void CalculateLinearWeights(float x, float y)
        {
            Child a = _boundingChildren[0]!;
            Child b = _boundingChildren[1]!;

            // Get positions as vectors
            Vector2 aPos = new(a.PositionX, a.PositionY);
            Vector2 bPos = new(b.PositionX, b.PositionY);
            Vector2 point = new(x, y);

            // Project the point onto the line between a and b
            Vector2 line = bPos - aPos;
            float lineLength = line.Length();

            if (lineLength < EPSILON)
            {
                // Points are essentially the same, use only one
                _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = 1.0f };
                return;
            }

            // Normalize the line vector
            Vector2 lineNormalized = line / lineLength;

            // Calculate the projection of (point - aPos) onto the normalized line
            Vector2 toPoint = point - aPos;
            float projectionLength = Vector2.Dot(toPoint, lineNormalized);

            // Calculate normalized position along the line (0 = at a, 1 = at b)
            float t = Math.Clamp(projectionLength / lineLength, 0, 1);

            // Calculate weights for a and b
            _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = 1.0f - t };
            _childWeights[_weightCount++] = new ChildWeight { Child = b, Weight = t };
        }

        const float EPSILON = 0.001f;

        private bool TryCalculateBarycentric(Child a, Child b, Child c, Vector2 a2d, Vector2 b2d, Vector2 c2d, Vector2 point)
        {
            // Calculate barycentric coordinates
            float denominator = (b2d.Y - c2d.Y) * (a2d.X - c2d.X) + (c2d.X - b2d.X) * (a2d.Y - c2d.Y);

            if (Math.Abs(denominator) < EPSILON)
                return false;

            float alpha = ((b2d.Y - c2d.Y) * (point.X - c2d.X) + (c2d.X - b2d.X) * (point.Y - c2d.Y)) / denominator;
            float beta = ((c2d.Y - a2d.Y) * (point.X - c2d.X) + (a2d.X - c2d.X) * (point.Y - c2d.Y)) / denominator;
            float gamma = 1.0f - alpha - beta;

            // If the point is outside the triangle beyond our tolerance
            if (alpha < -EPSILON || beta < -EPSILON || gamma < -EPSILON)
                return false;

            // If we're inside or very close to the edge, use the barycentric coordinates
            // but ensure they're normalized and positive
            float sum = Math.Max(0, alpha) + Math.Max(0, beta) + Math.Max(0, gamma);

            // Avoid division by zero
            if (sum < EPSILON)
                return false;

            float normalizedAlpha = Math.Max(0, alpha) / sum;
            float normalizedBeta = Math.Max(0, beta) / sum;
            float normalizedGamma = Math.Max(0, gamma) / sum;

            _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = normalizedAlpha };
            _childWeights[_weightCount++] = new ChildWeight { Child = b, Weight = normalizedBeta };
            _childWeights[_weightCount++] = new ChildWeight { Child = c, Weight = normalizedGamma };
            return true;
        }
    }
}
