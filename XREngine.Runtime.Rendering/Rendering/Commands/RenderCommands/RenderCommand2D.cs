using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Commands
{
    public abstract class RenderCommand2D : RenderCommand, IRenderCommand
    {
        public int ZIndex { get; set; }
        float IRenderCommand.RenderDistance
        {
            get => ZIndex;
            set => ZIndex = (int)value;
        }

        public override int CompareTo(RenderCommand? other)
        {
            int zCompare = ZIndex.CompareTo((other as RenderCommand2D)?.ZIndex ?? 0);
            if (zCompare != 0)
                return zCompare;

            int sortCompare = SortOrderKey.CompareTo(other?.SortOrderKey ?? long.MaxValue);
            if (sortCompare != 0)
                return sortCompare;

            return ReferenceEquals(this, other)
                ? 0
                : RuntimeHelpers.GetHashCode(this).CompareTo(RuntimeHelpers.GetHashCode(other));
        }

        public RenderCommand2D()
            : base(0) { }

        public RenderCommand2D(int renderPass)
            : base(renderPass) { }

        public RenderCommand2D(int renderPass, int zIndex)
            : base(renderPass) => ZIndex = zIndex;
    }
}
