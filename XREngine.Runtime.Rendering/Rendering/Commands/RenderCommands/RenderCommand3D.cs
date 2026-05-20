using System.Numerics;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Commands
{
    public interface IRenderCommand
    {
        float RenderDistance { get; set; }
        int RenderPass { get; set; }
        bool Enabled { get; set; }

        public event Action? PreRender;
        public event Action? PostRender;
    }
    public abstract class RenderCommand3D(int renderPass) : RenderCommand(renderPass)
    {
        private float _renderDistance = 0.0f;
        /// <summary>
        /// Used to determine what order to render in.
        /// Opaque objects closer to the camera should be drawn first,
        /// whereas translucent objects farther from the camera should be drawn first.
        /// The sorting order depends on how this command's requested render pass is set up in the camera's render pipeline.
        /// </summary>
        public float RenderDistance
        {
            get => _renderDistance;
            set => SetField(ref _renderDistance, value);
        }

        public void UpdateRenderDistance(Vector3 thisWorldPosition, IRuntimeRenderCamera camera)
            => RenderDistance = (camera.Transform.RenderTranslation - thisWorldPosition).LengthSquared();
        public override int CompareTo(RenderCommand? other)
        {
            int distanceCompare = RenderDistance.CompareTo((other as RenderCommand3D)?.RenderDistance ?? 0.0f);
            if (distanceCompare != 0)
                return distanceCompare;

            int sortCompare = SortOrderKey.CompareTo(other?.SortOrderKey ?? long.MaxValue);
            if (sortCompare != 0)
                return sortCompare;

            return ReferenceEquals(this, other)
                ? 0
                : RuntimeHelpers.GetHashCode(this).CompareTo(RuntimeHelpers.GetHashCode(other));
        }

        public RenderCommand3D()
            : this(0) { }
    }
}
