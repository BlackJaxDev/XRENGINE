using Extensions;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Geometry;
using XREngine.Scene;
using static XREngine.Rendering.UI.UIDualSplitTransform;

namespace XREngine.Rendering.UI
{
    public class UIDockingRootTransform : UIBoundableTransform
    {
        private float _leftSizeWidth = 300.0f;
        private float _rightSizeWidth = 300.0f;
        private float _bottomSizeHeight = 200.0f;

        //first child = center
        //second child = left
        //third child = right
        //fourth child = bottom

        public UIBoundableTransform? Center
            => SceneNode?.Transform.Children.TryGet(0) as UIBoundableTransform;
        public UIBoundableTransform? Left
            => SceneNode?.Transform.Children.TryGet(1) as UIBoundableTransform;
        public UIBoundableTransform? Right
            => SceneNode?.Transform.Children.TryGet(2) as UIBoundableTransform;
        public UIBoundableTransform? Bottom
            => SceneNode?.Transform.Children.TryGet(3) as UIBoundableTransform;

        public float LeftSizeWidth
        {
            get => _leftSizeWidth;
            set => SetField(ref _leftSizeWidth, value);
        }
        public float RightSizeWidth
        {
            get => _rightSizeWidth;
            set => SetField(ref _rightSizeWidth, value);
        }
        public float BottomSizeHeight
        {
            get => _bottomSizeHeight;
            set => SetField(ref _bottomSizeHeight, value);
        }

        /// <summary>
        /// New layout path: arranges docked children (center, left, right, bottom)
        /// using the centralized layout system.
        /// </summary>
        protected override void ArrangeChildren(BoundingRectangleF childRegion)
        {
            var paddedRegion = ApplyPadding(childRegion);

            if (SceneNode is not null)
            {
                while (SceneNode.Transform.Children.Count < 4)
                {
                    var node = SceneNode.NewChild();
                    var tfm = node.SetTransform<UIBoundableTransform>();
                    tfm.Name = $"DockingRootChild{SceneNode.Transform.Children.Count - 1}";
                    tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
                    tfm.MinAnchor = new Vector2(0.0f, 0.0f);
                }
            }

            if (Center?.PlacementInfo is UIDockingPlacementInfo aInfo)
                aInfo.BottomLeft = new Vector2(LeftSizeWidth, BottomSizeHeight);
            if (Center is not null)
                UILayoutSystem.FitLayout(Center, new BoundingRectangleF(paddedRegion.X + LeftSizeWidth, paddedRegion.Y + BottomSizeHeight, paddedRegion.Width - LeftSizeWidth - RightSizeWidth, paddedRegion.Height));

            if (Left?.PlacementInfo is UIDockingPlacementInfo bInfo)
                bInfo.BottomLeft = new Vector2(0.0f, BottomSizeHeight);
            if (Left is not null)
                UILayoutSystem.FitLayout(Left, new BoundingRectangleF(paddedRegion.X, paddedRegion.Y + BottomSizeHeight, LeftSizeWidth, paddedRegion.Height));

            if (Right?.PlacementInfo is UIDockingPlacementInfo cInfo)
                cInfo.BottomLeft = new Vector2(paddedRegion.Width - RightSizeWidth, BottomSizeHeight);
            if (Right is not null)
                UILayoutSystem.FitLayout(Right, new BoundingRectangleF(paddedRegion.X + paddedRegion.Width - RightSizeWidth, paddedRegion.Y, RightSizeWidth, paddedRegion.Height));

            if (Bottom?.PlacementInfo is UIDockingPlacementInfo dInfo)
                dInfo.BottomLeft = new Vector2(0.0f, paddedRegion.Height - BottomSizeHeight);
            if (Bottom is not null)
                UILayoutSystem.FitLayout(Bottom, new BoundingRectangleF(paddedRegion.X, paddedRegion.Y + paddedRegion.Height - BottomSizeHeight, paddedRegion.Width, BottomSizeHeight));
        }

        /// <summary>
        /// Old layout path: called from OnLocalMatrixChanged.
        /// Kept for compatibility.
        /// </summary>
        protected override void OnResizeChildComponents(BoundingRectangleF parentRegion)
        {
            if (SceneNode is not null)
            {
                while (SceneNode.Transform.Children.Count < 4)
                {
                    var node = SceneNode.NewChild();
                    var tfm = node.SetTransform<UIBoundableTransform>();
                    tfm.Name = $"DockingRootChild{SceneNode.Transform.Children.Count - 1}";
                    tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
                    tfm.MinAnchor = new Vector2(0.0f, 0.0f);
                }
                while (SceneNode.Transform.Children.Count > 4)
                {
                    //SceneNode.Transform.Children.RemoveAt(SceneNode.Transform.Children.Count - 1);
                }
            }

            if (Center?.PlacementInfo is UIDockingPlacementInfo aInfo)
                aInfo.BottomLeft = new Vector2(LeftSizeWidth, BottomSizeHeight);
            Center?.FitLayout(new BoundingRectangleF(parentRegion.X + LeftSizeWidth, parentRegion.Y + BottomSizeHeight, parentRegion.Width - LeftSizeWidth - RightSizeWidth, parentRegion.Height));

            if (Left?.PlacementInfo is UIDockingPlacementInfo bInfo)
                bInfo.BottomLeft = new Vector2(0.0f, BottomSizeHeight);
            Left?.FitLayout(new BoundingRectangleF(parentRegion.X, parentRegion.Y + BottomSizeHeight, LeftSizeWidth, parentRegion.Height));

            if (Right?.PlacementInfo is UIDockingPlacementInfo cInfo)
                cInfo.BottomLeft = new Vector2(parentRegion.Width - RightSizeWidth, BottomSizeHeight);
            Right?.FitLayout(new BoundingRectangleF(parentRegion.X + parentRegion.Width - RightSizeWidth, parentRegion.Y, RightSizeWidth, parentRegion.Height));

            if (Bottom?.PlacementInfo is UIDockingPlacementInfo dInfo)
                dInfo.BottomLeft = new Vector2(0.0f, parentRegion.Height - BottomSizeHeight);
            Bottom?.FitLayout(new BoundingRectangleF(parentRegion.X, parentRegion.Y + parentRegion.Height - BottomSizeHeight, parentRegion.Width, BottomSizeHeight));
        }

        public override void VerifyPlacementInfo(UITransform childTransform, ref UIChildPlacementInfo? placementInfo)
        {
            if (placementInfo is not UIDockingPlacementInfo)
                placementInfo = new UIDockingPlacementInfo(this);
        }
    }

    public class UIDockingPlacementInfo(UITransform owner) : UIChildPlacementInfo(owner)
    {
        public Vector2 BottomLeft { get; set; }

        public override Matrix4x4 GetRelativeItemMatrix()
            => Matrix4x4.CreateTranslation(BottomLeft.X, BottomLeft.Y, 0.0f);
    }

    [RequiresTransform(typeof(UIDockingRootTransform))]
    public class UIDockingRootComponent : UIComponent
    {
        public UIDockingRootTransform DockingTransform => TransformAs<UIDockingRootTransform>(true)!;

        public UIDockingRootComponent()
        {
            for (var i = 0; i < 4; i++)
            {
                var node = SceneNode.NewChild();
                var tfm = node.SetTransform<UIBoundableTransform>();
                tfm.Name = $"DockingRootChild{i}";
                tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
                tfm.MinAnchor = new Vector2(0.0f, 0.0f);
            }
        }
    }
}
