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
        public event Action<RenderBone>? RenderTransformUpdated;
        public TransformBase Transform { get; }
        public Matrix4x4 InvBindMatrix { get; }

        public RenderBone(TransformBase source, Matrix4x4 invBindMtx, uint index)
        {
            Index = index;
            Transform = source;
            Transform.RenderWorldMatrixChanged += OnRenderTransformChanged;
            InvBindMatrix = invBindMtx;
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Transform):
                        Transform.RenderWorldMatrixChanged -= OnRenderTransformChanged;
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
                    Transform.RenderWorldMatrixChanged += OnRenderTransformChanged;
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

        public List<VertexWeightGroup> TargetWeights
        {
            get => _targetWeights;
            set => SetField(ref _targetWeights, value);
        }

        public void OnRenderTransformChanged(TransformBase tfm)
            => RenderTransformUpdated?.Invoke(this);
    }
}