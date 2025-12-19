using System.Numerics;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// Copies the world matrix of another transform.
    /// Useful for creating a hierarchy of transforms that are not directly connected.
    /// </summary>
    /// <param name="parent"></param>
    public class CopyTransform : TransformBase
    {
        public CopyTransform() { }
        public CopyTransform(TransformBase parent)
            : base(parent) { }

        private TransformBase? _source;
        public TransformBase? Source
        {
            get => _source;
            set => SetField(ref _source, value);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Source):
                        Source?.WorldMatrixChanged -= OnSourceMatrixChanged;
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
                case nameof(Source):
                    if (Source is not null)
                        Source.WorldMatrixChanged += OnSourceMatrixChanged;
                    break;
            }
        }

        private void OnSourceMatrixChanged(TransformBase @base, Matrix4x4 worldMatrix)
            => MarkWorldModified();
        
        protected override Matrix4x4 CreateWorldMatrix()
            => Source is null
                ? Parent?.WorldMatrix ?? Matrix4x4.Identity
                : Source.WorldMatrix;

        protected override Matrix4x4 CreateLocalMatrix()
            => Matrix4x4.Identity;
    }
}