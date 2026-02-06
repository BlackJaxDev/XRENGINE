using Extensions;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Contains multiple children, but only one child is visible at a time.
    /// </summary>
    public class UITabTransform : UIBoundableTransform
    {
        private int _selectedIndex = 0;
        public int SelectedIndex
        {
            get => _selectedIndex;
            set => SetField(ref _selectedIndex, value.Clamp(0, Children.Count - 1));
        }

        private float _tabHeight = 30.0f;
        public float TabHeight
        {
            get => _tabHeight;
            set => SetField(ref _tabHeight, value);
        }

        public void SelectNext()
        {
            int index = SelectedIndex + 1;
            if (index >= Children.Count)
                index = 0;
            SelectedIndex = index;
        }

        public void SelectPrevious()
        {
            int index = SelectedIndex - 1;
            if (index < 0)
                index = Children.Count - 1;
            SelectedIndex = index;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(SelectedIndex):
                    InvalidateLayout();
                    break;
            }
        }

        protected override void ArrangeChildren(BoundingRectangleF childRegion)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                if (Children[i] is not UIBoundableTransform bc)
                    continue;

                if (i == SelectedIndex)
                {
                    UILayoutSystem.FitLayout(bc, childRegion);
                    bc.Visibility = EVisibility.Visible;
                }
                else
                {
                    bc.Visibility = EVisibility.Collapsed;
                }
            }
        }

        protected override void OnResizeChildComponents(BoundingRectangleF parentRegion)
        {
            //lock (Children)
            //{
                for (int i = 0; i < Children.Count; i++)
                {
                    if (Children[i] is not UITransform child)
                        continue;
                    
                    if (i == SelectedIndex)
                    {
                        child.FitLayout(parentRegion);
                        child.Visibility = EVisibility.Visible;
                    }
                    else
                    {
                        child.Visibility = EVisibility.Collapsed;
                    }
                }
            //}
        }
    }
    /// <summary>
    /// Same as a boundable transform, but supports docking another boundable transform to the center (creating a new tab), top, bottom, left, right.
    /// </summary>
    public class UIDockableTransform : UIBoundableTransform
    {
        private float _dragDropThreshold = 0.25f;
        public float DragDropThreshold
        {
            get => _dragDropThreshold;
            set => SetField(ref _dragDropThreshold, value);
        }

        private EDropLocationFlags _dropTarget = EDropLocationFlags.None;
        /// <summary>
        /// This is the current drop target for a dragged object.
        /// When this is set, the UI will display a visual indicator of where the object will be dropped.
        /// </summary>
        public EDropLocationFlags DropTarget
        {
            get => _dropTarget;
            private set => SetField(ref _dropTarget, value);
        }

        public void DragDrop(Vector2 worldPoint)
        {
            Vector2 localPoint = WorldToLocal(worldPoint);

            //Determine if the localPoint is center, left, right, top, or bottom of the bounds.
            float dropThreshold = DragDropThreshold;
            var bounds = GetActualBounds();
            EDropLocationFlags flags = EDropLocationFlags.None;
            CompX(localPoint, dropThreshold, bounds, ref flags);
            CompY(localPoint, dropThreshold, bounds, ref flags);
            DropTarget = flags;
        }

        public void Drop(UIBoundableTransform boundable)
        {
            if (boundable is null)
                return;

            if (DropTarget.HasFlag(EDropLocationFlags.Center))
                DockCenter(boundable);
            else if (DropTarget.HasFlag(EDropLocationFlags.Left))
                DockLeft(boundable);
            else if (DropTarget.HasFlag(EDropLocationFlags.Right))
                DockRight(boundable);
            else if (DropTarget.HasFlag(EDropLocationFlags.Top))
                DockTop(boundable);
            else if (DropTarget.HasFlag(EDropLocationFlags.Bottom))
                DockBottom(boundable);
        }

        private void DockCenter(UIBoundableTransform boundable)
        {

        }

        private void DockBottom(UIBoundableTransform boundable)
        {

        }

        private void DockTop(UIBoundableTransform boundable)
        {

        }

        private void DockRight(UIBoundableTransform boundable)
        {

        }

        private void DockLeft(UIBoundableTransform boundable)
        {

        }

        public event Action<UIBoundableTransform, EDropLocationFlags>? DropTargetChanged;

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(DropTarget):
                    DropTargetChanged?.Invoke(this, DropTarget);
                    break;
            }
        }

        private static void CompY(Vector2 localPoint, float dropThreshold, BoundingRectangleF bounds, ref EDropLocationFlags flags)
        {
            var y = localPoint.Y;
            var miny = bounds.MinY;
            var maxy = bounds.MaxY;
            //normalize y to 0-1
            y = (y - miny) / (maxy - miny);
            if (y > 0.0f && y < 1.0f)
            {
                if (y < dropThreshold)
                {
                    //bottom
                    flags |= EDropLocationFlags.Bottom;
                }
                else if (y > 1.0f - dropThreshold)
                {
                    //top
                    flags |= EDropLocationFlags.Top;
                }
                else
                {
                    //center
                    flags |= EDropLocationFlags.Center;
                }
            }
        }
        private static void CompX(Vector2 localPoint, float dropThreshold, BoundingRectangleF bounds, ref EDropLocationFlags flags)
        {
            var x = localPoint.X;
            var minx = bounds.MinX;
            var maxx = bounds.MaxX;
            //normalize x to 0-1
            x = (x - minx) / (maxx - minx);
            if (x > 0.0f && x < 1.0f)
            {
                if (x < dropThreshold)
                {
                    //left
                    flags |= EDropLocationFlags.Left;
                }
                else if (x > 1.0f - dropThreshold)
                {
                    //right
                    flags |= EDropLocationFlags.Right;
                }
                else
                {
                    //center
                    flags |= EDropLocationFlags.Center;
                }
            }
        }
    }
}
