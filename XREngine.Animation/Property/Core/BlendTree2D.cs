using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    public class BlendTree2D : BlendTree
    {
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

        public override void Tick(object? rootObject, float delta, IDictionary<string, AnimVar> variables, float weight)
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
                _children[0].Motion?.Tick(rootObject, delta, variables, weight);
                return;
            }

            // If we have only two children, use linear interpolation
            if (_children.Count == 2)
            {
                Child a = _children[0];
                Child b = _children[1];
                // Calculate the distance from the point to each child
                float dxA = a.PositionX - x;
                float dyA = a.PositionY - y;
                float distA = dxA * dxA + dyA * dyA;
                float dxB = b.PositionX - x;
                float dyB = b.PositionY - y;
                float distB = dxB * dxB + dyB * dyB;
                // Calculate weights based on distance
                float totalDist = distA + distB;
                if (totalDist > 0)
                {
                    float weightA = distB / totalDist;
                    float weightB = distA / totalDist;
                    a.Motion?.Tick(rootObject, delta, variables, weight * weightA);
                    b.Motion?.Tick(rootObject, delta, variables, weight * weightB);
                }
                return;
            }

            // Find the children that bound (x,y)
            FindBoundingChildren(x, y);

            // If we couldn't find bounding children, find the nearest child
            if (_boundingChildCount == 0)
            {
                Child nearest = FindNearestChild(x, y);
                nearest.Motion?.Tick(rootObject, delta, variables, weight);
                return;
            }

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
                    CalculateBaryCentricWeights(x, y);
                    break;
            }

            // Tick each motion with its calculated weight
            for (int i = 0; i < _weightCount; i++)
            {
                var childWeight = _childWeights[i];
                childWeight.Child.Motion?.Tick(rootObject, delta, variables, weight * childWeight.Weight);
            }
        }

        private Child FindNearestChild(float x, float y)
        {
            Child nearest = _children[0];
            float nearestDistSq = float.MaxValue;

            for (int i = 0; i < _children.Count; i++)
            {
                Child child = _children[i];
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

        private void FindBoundingChildren(float x, float y)
        {
            _boundingChildCount = 0;

            // If we don't have enough children, bail early
            if (_children.Count < 3)
                return;
            
            // Find indices of children that could potentially bound the point
            int leftIdx = -1;
            int rightIdx = -1;
            int bottomIdx = -1;
            int topIdx = -1;

            // Find closest left child
            for (int i = _sortedByX.Length - 1; i >= 0; i--)
            {
                if (_sortedByX[i].PositionX > x)
                    continue;
                
                leftIdx = i;
                break;
            }

            // Find closest right child
            for (int i = 0; i < _sortedByX.Length; i++)
            {
                if (_sortedByX[i].PositionX < x)
                    continue;
                
                rightIdx = i;
                break;
            }

            // Find closest bottom child
            for (int i = _sortedByY.Length - 1; i >= 0; i--)
            {
                if (_sortedByY[i].PositionY > y)
                    continue;
                
                bottomIdx = i;
                break;
            }

            // Find closest top child
            for (int i = 0; i < _sortedByY.Length; i++)
            {
                if (_sortedByY[i].PositionY < y)
                    continue;
                
                topIdx = i;
                break;
            }

            // If the point is outside the region covered by children, we can't bound it
            if (leftIdx < 0 || rightIdx < 0 || bottomIdx < 0 || topIdx < 0)
                return;

            // Find the quadrant corners using the available children
            Child? bottomLeft = null;
            Child? bottomRight = null;
            Child? topLeft = null;
            Child? topRight = null;

            float closestDistSq = float.MaxValue;

            // Find bottom-left
            for (int i = 0; i <= leftIdx; i++)
            {
                Child leftChild = _sortedByX[i];
                if (leftChild.PositionY > y)
                    continue;
                
                float dx = leftChild.PositionX - x;
                float dy = leftChild.PositionY - y;
                float distSq = dx * dx + dy * dy;
                if (bottomLeft == null || distSq < closestDistSq)
                {
                    bottomLeft = leftChild;
                    closestDistSq = distSq;
                }
            }

            // Find bottom-right
            closestDistSq = float.MaxValue;
            for (int i = rightIdx; i < _sortedByX.Length; i++)
            {
                Child rightChild = _sortedByX[i];
                if (rightChild.PositionY > y)
                    continue;
                
                float dx = rightChild.PositionX - x;
                float dy = rightChild.PositionY - y;
                float distSq = dx * dx + dy * dy;
                if (bottomRight == null || distSq < closestDistSq)
                {
                    bottomRight = rightChild;
                    closestDistSq = distSq;
                }
            }

            // Find top-left
            closestDistSq = float.MaxValue;
            for (int i = 0; i <= leftIdx; i++)
            {
                Child leftChild = _sortedByX[i];
                if (leftChild.PositionY < y)
                    continue;
                
                float dx = leftChild.PositionX - x;
                float dy = leftChild.PositionY - y;
                float distSq = dx * dx + dy * dy;
                if (topLeft == null || distSq < closestDistSq)
                {
                    topLeft = leftChild;
                    closestDistSq = distSq;
                }
            }

            // Find top-right
            closestDistSq = float.MaxValue;
            for (int i = rightIdx; i < _sortedByX.Length; i++)
            {
                Child rightChild = _sortedByX[i];
                if (rightChild.PositionY < y)
                    continue;
                
                float dx = rightChild.PositionX - x;
                float dy = rightChild.PositionY - y;
                float distSq = dx * dx + dy * dy;
                if (topRight == null || distSq < closestDistSq)
                {
                    topRight = rightChild;
                    closestDistSq = distSq;
                }
            }

            // Add non-null children to the bounding array
            _boundingChildCount = 0;
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

            // If we have fewer than 4 bounding children, fall back to nearest point
            if (_boundingChildCount < 4)
            {
                Child nearest = FindNearestBoundingChild(x, y);
                _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
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
                    // No bounding children, use the nearest point from all children
                    Child nearest = FindNearestChild(x, y);
                    _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
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

            // Triangle 1: first three points
            if (_boundingChildCount >= 3 && !foundTriangle)
            {
                Child a = _boundingChildren[0]!;
                Child b = _boundingChildren[1]!;
                Child c = _boundingChildren[2]!;

                Vector2 a2d = new(a.PositionX, a.PositionY);
                Vector2 b2d = new(b.PositionX, b.PositionY);
                Vector2 c2d = new(c.PositionX, c.PositionY);

                if (TryCalculateBarycentric(a, b, c, a2d, b2d, c2d, point))
                    foundTriangle = true;
            }

            // Triangle 2: the quad diagonal if we have 4 points
            if (_boundingChildCount >= 4 && !foundTriangle)
            {
                Child a = _boundingChildren[0]!;
                Child b = _boundingChildren[1]!;
                Child c = _boundingChildren[3]!;

                Vector2 a2d = new(a.PositionX, a.PositionY);
                Vector2 b2d = new(b.PositionX, b.PositionY);
                Vector2 c2d = new(c.PositionX, c.PositionY);

                if (TryCalculateBarycentric(a, b, c, a2d, b2d, c2d, point))
                    foundTriangle = true;
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

        private bool TryCalculateBarycentric(Child a, Child b, Child c, Vector2 a2d, Vector2 b2d, Vector2 c2d, Vector2 point)
        {
            // Calculate barycentric coordinates
            float denominator = (b2d.Y - c2d.Y) * (a2d.X - c2d.X) + (c2d.X - b2d.X) * (a2d.Y - c2d.Y);

            if (Math.Abs(denominator) < float.Epsilon)
                return false;

            float alpha = ((b2d.Y - c2d.Y) * (point.X - c2d.X) + (c2d.X - b2d.X) * (point.Y - c2d.Y)) / denominator;
            float beta = ((c2d.Y - a2d.Y) * (point.X - c2d.X) + (a2d.X - c2d.X) * (point.Y - c2d.Y)) / denominator;
            float gamma = 1.0f - alpha - beta;

            // If the point is inside this triangle
            if (alpha < -0.01f || beta < -0.01f || gamma < -0.01f)
                return false;

            // Normalize weights to ensure they sum to 1
            float sum = Math.Max(0, alpha) + Math.Max(0, beta) + Math.Max(0, gamma);
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
