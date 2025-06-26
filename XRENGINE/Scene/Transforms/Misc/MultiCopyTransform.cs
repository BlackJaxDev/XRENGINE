using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Scene.Transforms
{
    public class MultiCopyTransform : TransformBase
    {
        public EventList<WeightedSource> Sources { get; } = [];

        public MultiCopyTransform() : this(null) { }
        public MultiCopyTransform(TransformBase? parent)
            : base(parent)
        {
            Sources.PostAnythingAdded += OnSourceAdded;
            Sources.PostAnythingRemoved += OnSourceRemoved;
        }

        public class WeightedSource(TransformBase transform, float weight) : XRBase
        {
            private float _weight = weight;
            private TransformBase _transform = transform;

            public TransformBase Transform
            {
                get => _transform;
                set => SetField(ref _transform, value);
            }
            public float Weight
            {
                get => _weight;
                set => SetField(ref _weight, value);
            }

            public static implicit operator (TransformBase transform, float weight)(WeightedSource value)
                => (value.Transform, value.Weight);

            public static implicit operator WeightedSource((TransformBase transform, float weight) value)
                => new(value.transform, value.weight);
        }

        private void OnSourceAdded(WeightedSource item)
        {
            item.PropertyChanging += SourcePropertyChanging;
            item.PropertyChanged += SourcePropertyChanged;
            LinkTransform(item);
        }

        private void OnSourceRemoved(WeightedSource item)
        {
            item.PropertyChanging -= SourcePropertyChanging;
            item.PropertyChanged -= SourcePropertyChanged;
            UnlinkTransform(item);
        }

        private void SourcePropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
            if (sender is not WeightedSource item)
                return;

            switch (e.PropertyName)
            {
                case nameof(WeightedSource.Transform):
                    UnlinkTransform(item);
                    break;
            }
        }

        private void SourcePropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (sender is not WeightedSource item)
                return;

            switch (e.PropertyName)
            {
                case nameof(WeightedSource.Transform):
                    LinkTransform(item);
                    break;
                case nameof(WeightedSource.Weight):
                    MarkWorldModified();
                    break;
            }
        }

        private void UnlinkTransform(WeightedSource item)
        {
            if (item.Transform is not null)
                item.Transform.WorldMatrixChanged -= OnSourceMatrixChanged;
            MarkWorldModified();
        }

        private void LinkTransform(WeightedSource item)
        {
            if (item.Transform is not null)
                item.Transform.WorldMatrixChanged += OnSourceMatrixChanged;
            MarkWorldModified();
        }

        private void OnSourceMatrixChanged(TransformBase @base, Matrix4x4 worldMatrix)
            => MarkWorldModified();
        
        protected override Matrix4x4 CreateWorldMatrix()
        {
            Matrix4x4 matrix = Matrix4x4.Identity;
            foreach (var source in Sources)
                matrix += source.Transform.WorldMatrix * source.Weight;
            return matrix;
        }

        protected override Matrix4x4 CreateLocalMatrix()
            => Matrix4x4.Identity;
    }
}