using Extensions;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    public class BlendTree2D : BlendTree
    {
        public override string ToString()
            => $"BlendTree2D: {Name} ({XParameterName}, {YParameterName})";

        public class Child : XRBase
        {
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

        private Child[] _sortedByX = [];
        private Child[] _sortedByY = [];
        private bool _needsSort = true;

        // Pre-allocated arrays to store bounding children
        private readonly Child?[] _boundingChildren = new Child?[4];
        private int _boundingChildCount;

        // Pre-allocated weight storage
        private struct ChildWeight
        {
            public Child Child;
            public float Weight;
        }
        private readonly ChildWeight[] _childWeights = new ChildWeight[4];
        private int _weightCount;

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
            /// A barycentric blend is created with the nearest 3 children, relative to the center (0, 0).
            /// </summary>
            Directional,
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

        public override void Tick(float delta)
        {
            foreach (var child in Children)
                child.Motion?.Tick(delta * child.Speed);
        }

        public override void BlendChildMotionAnimationValues(IDictionary<string, AnimVar> variables, float weight)
        {
            float x = 0.0f;
            float y = 0.0f;
            if (variables.TryGetValue(XParameterName, out AnimVar? xVar))
                x = xVar.FloatValue;
            if (variables.TryGetValue(YParameterName, out AnimVar? yVar))
                y = yVar.FloatValue;

            if (_needsSort || _sortedByX.Length != _children.Count || _sortedByY.Length != _children.Count)
                UpdateSortedArrays();

            // If we have no children, there's nothing to blend
            if (_children.Count == 0)
                return;

            // If we have only one child, just tick it directly
            if (_children.Count == 1)
            {
                _children[0].Motion?.GetAnimationValues(this, variables, weight);
                return;
            }

            // If we have only two children, use linear interpolation
            if (_children.Count == 2)
            {
                switch (BlendType)
                {
                    case EBlendType.Cartesian:
                        // Use linear interpolation along the direct line
                        CalculateLinearWeightsNoBounding(x, y);
                        break;
                    default:
                        // Use inverse-distance weighting for other blend types
                        CalculateInverseDistanceWeightsNoBounding(x, y);
                        break;
                }
            }
            else
            {
                // Find the children that bound (x,y)
                FindBoundingChildren(x, y);

                // If we couldn't find any bounding children, there's must be none
                if (_boundingChildCount == 0)
                    return;

                // Calculate weights for each child based on blend type
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
            }

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
            }
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

        private void CalculateDirectionalWeights(float x, float y)
        {
            _weightCount = 0;

            // For directional blending, we need at least 3 children
            if (_boundingChildCount < 3)
            {
                Child nearest = FindNearestBoundingChild(x, y);
                _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
                return;
            }

            // Create a vector from origin (0,0) to the target point
            Vector2 targetDir = new(x, y);
            float targetMagnitude = targetDir.Length();

            if (targetMagnitude < 0.0001f)
            {
                // If we're at the origin, use the nearest child
                Child nearest = FindNearestBoundingChild(x, y);
                _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
                return;
            }

            // Normalize the direction
            Vector2 targetDirNorm = targetDir / targetMagnitude;

            // Find the three most relevant children based on angle to target
            List<(Child child, float angle, float distance)> childAngles = new();

            for (int i = 0; i < _boundingChildCount; i++)
            {
                Child child = _boundingChildren[i]!;
                Vector2 childPos = new(child.PositionX, child.PositionY);
                float childDist = childPos.Length();

                if (childDist < 0.0001f)
                    continue; // Skip points at origin

                Vector2 childDir = childPos / childDist;
                float dot = Vector2.Dot(targetDirNorm, childDir);
                float angle = MathF.Acos(dot.Clamp(-1f, 1f));

                childAngles.Add((child, angle, childDist));
            }

            // Sort by angle
            childAngles.Sort((a, b) => a.angle.CompareTo(b.angle));

            // If we don't have enough children, fall back to nearest
            if (childAngles.Count < 3)
            {
                Child nearest = FindNearestBoundingChild(x, y);
                _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
                return;
            }

            // Get the 3 children that form a triangle containing the target direction
            Child[] triangleChildren = new Child[3];
            bool foundTriangle = false;

            for (int i = 0; i < childAngles.Count; i++)
            {
                int j = (i + 1) % childAngles.Count;
                int k = (i + 2) % childAngles.Count;

                triangleChildren[0] = childAngles[i].child;
                triangleChildren[1] = childAngles[j].child;
                triangleChildren[2] = childAngles[k].child;

                // Create vectors for these children
                Vector2 a2d = new(triangleChildren[0].PositionX, triangleChildren[0].PositionY);
                Vector2 b2d = new(triangleChildren[1].PositionX, triangleChildren[1].PositionY);
                Vector2 c2d = new(triangleChildren[2].PositionX, triangleChildren[2].PositionY);

                // Scale the target vector to match the average magnitude of the triangle vectors
                float avgMagnitude = (a2d.Length() + b2d.Length() + c2d.Length()) / 3;
                Vector2 scaledTarget = targetDirNorm * (avgMagnitude * targetMagnitude / MathF.Max(1.0f, targetMagnitude));

                if (TryCalculateBarycentric(
                    triangleChildren[0], triangleChildren[1], triangleChildren[2],
                    a2d, b2d, c2d, scaledTarget))
                {
                    foundTriangle = true;
                    break;
                }
            }

            if (!foundTriangle)
            {
                // Fall back to nearest if no triangle contains the direction
                Child nearest = FindNearestBoundingChild(x, y);
                _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
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

        private readonly Comparer<Child?> _xComp = Comparer<Child?>.Create(static (a, b) =>
        {
            if (a == null && b == null)
                return 0;
            if (a == null)
                return 1;
            if (b == null)
                return -1;
            return a.PositionX.CompareTo(b.PositionX);
        });
        private readonly Comparer<Child?> _yComp = Comparer<Child?>.Create(static (a, b) =>
        {
            if (a == null && b == null)
                return 0;
            if (a == null)
                return 1;
            if (b == null)
                return -1;
            return a.PositionY.CompareTo(b.PositionY);
        });

        private void FindBoundingChildren(float x, float y)
        {
            _boundingChildCount = 0;

            // Early exit if there are not enough children
            if (_children.Count < 3)
                return;

            // Binary search for approximate indices
            int leftIdx = Array.BinarySearch(_sortedByX, new Child { PositionX = x }, _xComp);
            if (leftIdx < 0) 
                leftIdx = ~leftIdx - 1;

            //int bottomIdx = Array.BinarySearch(_sortedByY, new Child { PositionY = y }, _yComp);
            //if (bottomIdx < 0) 
            //    bottomIdx = ~bottomIdx - 1;

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

            // Find bottom-right and top-left from the remaining children
            Child[] remaining = new Child[2];
            int remainingCount = 0;

            for (int i = 0; i < _boundingChildCount; i++)
            {
                Child child = _boundingChildren[i]!;
                if (child != bottomLeft && child != topRight && remainingCount < 2)
                    remaining[remainingCount++] = child;
            }

            if (remainingCount == 2)
            {
                if (remaining[0].PositionX > remaining[1].PositionX)
                {
                    bottomRight = remaining[0];
                    topLeft = remaining[1];
                }
                else
                {
                    bottomRight = remaining[1];
                    topLeft = remaining[0];
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
