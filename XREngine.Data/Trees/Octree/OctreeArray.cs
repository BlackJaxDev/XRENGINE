using XREngine.Extensions;
using System.Numerics;

namespace XREngine.Data.Trees
{
    public class OctreeArray<T>
    {
        private class OctreeNode
        {
            public T? Value { get; set; }
            public bool IsLeaf { get; set; }
            public int ChildIndex { get; set; }  // Index of the first child in the children array

            public OctreeNode()
            {
                IsLeaf = true;
                ChildIndex = -1;
            }
        }

        private readonly OctreeNode[] _nodes;
        private int _nodeCount;
        private readonly float _minDimension;
        private readonly float _maxDimension;
        private readonly int _maxDepth;
        private readonly int _maxNodes;

        public OctreeArray(float minDimension, float maxDimension)
        {
            _minDimension = minDimension;
            _maxDimension = maxDimension;
            _maxDepth = CalculateMaxDepth(minDimension, maxDimension);

            // Calculate maximum number of nodes in the tree
            _maxNodes = (int)Math.Pow(8, _maxDepth);
            _nodes = new OctreeNode[_maxNodes];
            _nodes.Fill();

            _nodeCount = 1; // Start with one root node
            BuildTree(0, maxDimension, 0);
        }

        private static int CalculateMaxDepth(float minDimension, float maxDimension)
        {
            int depth = 0;
            float dimension = maxDimension;
            while (dimension > minDimension)
            {
                dimension *= 0.5f;
                depth++;
            }
            return depth;
        }

        private void BuildTree(int nodeIndex, float currentDimension, int currentDepth)
        {
            if (currentDimension <= _minDimension || currentDepth == _maxDepth)
                return;

            _nodes[nodeIndex].IsLeaf = false;
            _nodes[nodeIndex].ChildIndex = _nodeCount;

            _nodeCount += 8;
            float childDimension = currentDimension * 0.5f;

            for (int i = 0; i < 8; i++)
                BuildTree(_nodes[nodeIndex].ChildIndex + i, childDimension, currentDepth + 1);
        }

        // Method to set value at a specific position
        public void SetValueAtPosition(T value, Vector3 pos)
            => SetValueAtPosition(0, value, pos, _maxDimension);

        private void SetValueAtPosition(int nodeIndex, T value, Vector3 pos, float currentDimension)
        {
            if (currentDimension <= _minDimension || _nodes[nodeIndex].IsLeaf)
            {
                _nodes[nodeIndex].Value = value;
                return;
            }

            float childDimension = currentDimension * 0.5f;
            SetValueAtPosition(
                _nodes[nodeIndex].ChildIndex + GetChildIndex(pos, currentDimension),
                value,
                new Vector3(
                    pos.X % childDimension,
                    pos.Y % childDimension,
                    pos.Z % childDimension),
                childDimension);
        }

        private static int GetChildIndex(Vector3 pos, float currentDimension)
        {
            int index = 0;
            float halfDim = currentDimension * 0.5f;

            if (pos.X >= halfDim)
                index |= 1;

            if (pos.Y >= halfDim)
                index |= 2;

            if (pos.Z >= halfDim)
                index |= 4;

            return index;
        }

        // Method to get value at a specific position
        public T? GetValueAtPosition(Vector3 pos)
            => GetValueAtPosition(0, pos, _maxDimension);

        private T? GetValueAtPosition(int nodeIndex, Vector3 pos, float currentDimension)
        {
            if (_nodes[nodeIndex].IsLeaf || currentDimension <= _minDimension)
                return _nodes[nodeIndex].Value;
            
            float childDimension = currentDimension * 0.5f;
            return GetValueAtPosition(
                _nodes[nodeIndex].ChildIndex + GetChildIndex(pos, currentDimension),
                new Vector3(
                    pos.X % childDimension,
                    pos.Y % childDimension,
                    pos.Z % childDimension),
                childDimension);
        }
    }
}
