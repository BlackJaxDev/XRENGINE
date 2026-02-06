using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Rendering.UI
{
    public abstract class UIChildPlacementInfo(UITransform owner) : XRBase
    {
        public UITransform Owner { get; } = owner;

        private bool _relativePositioningChanged = true;
        public bool RelativePositioningChanged
        {
            get => _relativePositioningChanged;
            set => SetField(ref _relativePositioningChanged, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            // When any positioning property changes (Offset, BottomOrLeftOffset, etc.),
            // mark relative positioning as changed so the owning transform's local matrix
            // is recalculated. Without this, split/list offset changes go undetected when
            // the child's relative position within its sub-region doesn't change (e.g.,
            // stretched children in a vertical split during height-only resize).
            if (propName != nameof(RelativePositioningChanged))
                RelativePositioningChanged = true;
        }

        public abstract Matrix4x4 GetRelativeItemMatrix();
    }
}
