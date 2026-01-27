using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering
{
    /// <summary>
    /// This class wraps a transform and stores information pertaining to rendering a mesh with that transform.
    /// </summary>
    public class RenderBone : XRBase
    {
        /// <summary>
        /// Index of the bone in the shader.
        /// Starts at 1. 0 is reserved for the identity matrix.
        /// </summary>
        public uint Index
        {
            get => _index;
            set => SetField(ref _index, value);
        }
        public TransformBase Transform
        {
            get => _transform;
            private set => SetField(ref _transform, value);
        }
        public Matrix4x4 InvBindMatrix { get; }

        public RenderBone(TransformBase source, Matrix4x4 invBindMtx, uint index)
        {
            Index = index;
            InvBindMatrix = invBindMtx;
            _transform = source;
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Transform):
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
                case nameof(Transform):
                    break;
            }
        }

        private Dictionary<uint, List<int>> _influencedVertices = [];
        public Dictionary<uint, List<int>> InfluencedVertices
        {
            get => _influencedVertices;
            set => SetField(ref _influencedVertices, value);
        }

        private List<VertexWeightGroup> _targetWeights = [];
        private uint _index;
        private TransformBase _transform;

        public List<VertexWeightGroup> TargetWeights
        {
            get => _targetWeights;
            set => SetField(ref _targetWeights, value);
        }
        
    }
}