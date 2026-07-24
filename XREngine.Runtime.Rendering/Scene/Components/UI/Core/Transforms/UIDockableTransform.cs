//using System.ComponentModel;
//using System.Numerics;

//namespace XREngine.Rendering.UI
//{
//    //[Flags]
//    //public enum EAnchorFlags
//    //{
//    //    None,
//    //    Left,
//    //    Right,
//    //    Top,
//    //    Bottom,
//    //}
//    /// <summary>
//    ///Component that uses a variety of positioning parameters
//    ///to calculate the region's position and dimensions upon resize.
//    /// </summary>
//    public class UIDockableTransform : UIBoundableTransform
//    {
//        /// <summary>
//        /// The X value of the right boundary line.
//        /// Only moves the right edge by resizing width.
//        /// </summary>
//        public float MaxX
//        {
//            get => BottomLeftTranslation.X + (Width < 0 ? 0 : Width);
//            set
//            {
//                CheckProperDimensions();
//                Width = value - BottomLeftTranslation.X;
//            }
//        }
//        /// <summary>
//        /// The Y value of the top boundary line.
//        /// Only moves the top edge by resizing height.
//        /// </summary>
//        public float MaxY
//        {
//            get => BottomLeftTranslation.Y + (Height < 0 ? 0 : Height);
//            set
//            {
//                CheckProperDimensions();
//                Height = value - BottomLeftTranslation.Y;
//            }
//        }
//        /// <summary>
//        /// The X value of the left boundary line.
//        /// Only moves the left edge by resizing width.
//        /// </summary>
//        public float MinX
//        {
//            get => BottomLeftTranslation.X + (Width < 0 ? Width : 0);
//            set
//            {
//                CheckProperDimensions();
//                float origX = MaxX;
//                SizeablePosX.SetResultingValueNoUpdate(value, ParentBounds);
//                Width = origX - BottomLeftTranslation.X;
//            }
//        }
//        /// <summary>
//        /// The Y value of the bottom boundary line.
//        /// Only moves the bottom edge by resizing height.
//        /// </summary>
//        public float MinY
//        {
//            get => BottomLeftTranslation.Y + (Height < 0 ? Height : 0);
//            set
//            {
//                CheckProperDimensions();
//                float origY = MaxY;
//                SizeablePosY.SetResultingValueNoUpdate(value, ParentBounds);
//                Height = origY - BottomLeftTranslation.Y;
//            }
//        }
//        /// <summary>
//        /// Checks that the width and height are positive values. Will move the location of the rectangle to fix this.
//        /// </summary>
//        public void CheckProperDimensions()
//        {
//            if (Width < 0)
//            {
//                LocalTranslation.X += Width;
//                Width = -Width;
//            }
//            if (Height < 0)
//            {
//                LocalTranslation.Y += Height;
//                Height = -Height;
//            }
//        }
//        public override Vector2 Size
//        {
//            get => base.Size;
//            set
//            {
//                SizeableWidth.SetResultingValueNoUpdate(value.X, ParentBounds);
//                SizeableHeight.SetResultingValueNoUpdate(value.Y, ParentBounds);
//                Resize();
//            }
//        }
//        public override Vector3 LocalTranslation
//        {
//            get => base.LocalTranslation;
//            set
//            {
//                SizeablePosX.SetResultingValueNoUpdate(value.X, ParentBounds);
//                SizeablePosY.SetResultingValueNoUpdate(value.Y, ParentBounds);
//                Resize();
//            }
//        }
//        public UIDockableTransform() : base()
//        {
//            SizeableHeight.ParameterChanged += Resize;
//            SizeableWidth.ParameterChanged += Resize;
//            SizeablePosX.ParameterChanged += Resize;
//            SizeablePosY.ParameterChanged += Resize;
//            _sizeableElements = new ISizeable[]
//            {
//                SizeableWidth,
//                SizeableHeight,
//                SizeablePosX,
//                SizeablePosY,
//                //Padding,
//                //Anchor,
//            };
//        }
        
//        private readonly ISizeable[] _sizeableElements;

//        //[EditInPlace]
//        [Category("Transform")]
//        public UIFloat SizeableWidth { get; }
//        //[EditInPlace]
//        [Category("Transform")]
//        public UIFloat SizeableHeight { get; }
//        //[EditInPlace]
//        [Category("Transform")]
//        public UIFloat SizeablePosX { get; }
//        //[EditInPlace]
//        [Category("Transform")]
//        public UIFloat SizeablePosY { get; }
//        //[EditInPlace]
//        //[Category("Transform")]
//        //public SizeableElementQuad Padding { get; } = new SizeableElementQuad();
//        //[EditInPlace]
//        //[Category("Transform")]
//        //public SizeableElementQuad Anchor { get; } = new SizeableElementQuad();

//        private EUIDockStyle _dockStyle = EUIDockStyle.None;
//        //private EAnchorFlags _anchorFlags = EAnchorFlags.None;

//        [Category("Transform")]
//        public EUIDockStyle DockStyle
//        {
//            get => _dockStyle;
//            set
//            {
//                IgnoreResizes = true;
//                switch (value)
//                {
//                    case EUIDockStyle.None:
//                        SizeablePosX.SetSizingPixels(SizeablePosX.Value);
//                        SizeablePosY.SetSizingPixels(SizeablePosY.Value);
//                        SizeableWidth.SetSizingPixels(SizeableWidth.Value);
//                        SizeableHeight.SetSizingPixels(SizeableHeight.Value);
//                        break;
//                    case EUIDockStyle.Fill:
//                        SizeablePosX.SetSizingPixels(0.0f);
//                        SizeablePosY.SetSizingPixels(0.0f);
//                        SizeableWidth.SetSizingPercentageOfParent(1.0f);
//                        SizeableHeight.SetSizingPercentageOfParent(1.0f);
//                        break;
//                    case EUIDockStyle.Left:
//                        SizeablePosX.SetSizingPixels(0.0f);
//                        SizeablePosY.SetSizingPixels(0.0f);
//                        SizeableWidth.SetSizingPercentageOfParent(1.0f);
//                        SizeableHeight.SetSizingPercentageOfParent(1.0f);
//                        break;
//                    case EUIDockStyle.Right:
//                        SizeablePosX.SetSizingPixels(0.0f);
//                        SizeablePosY.SetSizingPixels(0.0f);
//                        SizeableWidth.SetSizingPercentageOfParent(1.0f);
//                        SizeableHeight.SetSizingPercentageOfParent(1.0f);
//                        break;
//                    case EUIDockStyle.Top:
//                        SizeablePosX.SetSizingPixels(0.0f);
//                        SizeablePosY.SetSizingPixels(0.0f);
//                        SizeableWidth.SetSizingPercentageOfParent(1.0f);
//                        SizeableHeight.SetSizingPercentageOfParent(1.0f);
//                        break;
//                    case EUIDockStyle.Bottom:
//                        SizeablePosX.SetSizingPixels(0.0f);
//                        SizeablePosY.SetSizingPixels(0.0f);
//                        SizeableWidth.SetSizingPercentageOfParent(1.0f);
//                        SizeableHeight.SetSizingPercentageOfParent(1.0f);
//                        break;
//                }
//                IgnoreResizes = false;
//                _dockStyle = value;
//                Resize();
//            }
//        }
//        //[Category("Transform")]
//        //public EAnchorFlags SideAnchorFlags
//        //{
//        //    get => _anchorFlags;
//        //    set
//        //    {
//        //        _anchorFlags = value;
//        //        PerformResize();
//        //    }
//        //}

//        [Browsable(false)]
//        public bool Docked => _dockStyle != EUIDockStyle.None;

//        public override unsafe void ArrangeChildren(Vector2 translation, Vector2 parentBounds)
//        {
//            if (IgnoreResizes)
//                return;

//            IgnoreResizes = true;

//            ParentBounds = parentBounds;
            
//            Size.X = SizeableWidth.Value;
//            Size.Y = SizeableHeight.Value;
//            LocalTranslation.X = SizeablePosX.Value;
//            LocalTranslation.Y = SizeablePosY.Value;
            
//            RecalcLocalTransform();

//            Vector2 bounds = Size.Raw;
//            foreach (ISceneComponent c in _children)
//                if (c is IUIComponent uic)
//                    uic.ArrangeChildren(translation, bounds);

//            IgnoreResizes = false;
//        }
//        //private BoundingRectangleF RegionDockComplement(BoundingRectangleF parentRegion, BoundingRectangleF region)
//        //{
//        //    if (parentRegion.MaxX != region.MaxX)
//        //        return BoundingRectangleF.FromMinMaxSides(
//        //            region.MaxX, parentRegion.MaxX,
//        //            parentRegion.MinY, parentRegion.MaxY,
//        //            0.0f, 0.0f);
//        //    if (parentRegion.MinX != region.MinX)
//        //        return BoundingRectangleF.FromMinMaxSides(
//        //            parentRegion.MinX, region.MinX,
//        //            parentRegion.MinY, parentRegion.MaxY,
//        //            0.0f, 0.0f);
//        //    if (parentRegion.MaxY != region.MaxY)
//        //        return BoundingRectangleF.FromMinMaxSides(
//        //            parentRegion.MinX, parentRegion.MaxX,
//        //            region.MaxY, parentRegion.MaxY,
//        //            0.0f, 0.0f);
//        //    if (parentRegion.MinY != region.MinY)
//        //        return BoundingRectangleF.FromMinMaxSides(
//        //            parentRegion.MinX, parentRegion.MaxX,
//        //            parentRegion.MinY, region.MinY,
//        //            0.0f, 0.0f);
//        //    return BoundingRectangleF.Empty;
//        //}
//    }
//}
