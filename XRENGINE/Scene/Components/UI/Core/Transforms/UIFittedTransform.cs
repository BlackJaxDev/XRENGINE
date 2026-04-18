using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Aligns a bounded UI component within its parent using special alignment settings.
    /// </summary>
    [XRTransformEditor("XREngine.Editor.TransformEditors.UIFittedTransformEditor")]
    public class UIFittedTransform : UIBoundableTransform
    {
        public enum EFitType
        {
            None,
            Stretch,
            Center,
            Fill,
        }
        private EFitType _fitType = EFitType.None;
        /// <summary>
        /// How to fit the component within its parent.
        /// </summary>
        public EFitType FitType
        {
            get => _fitType;
            set => SetField(ref _fitType, value);
        }
        protected override void GetActualBounds(BoundingRectangleF parentBounds, out Vector2 trans, out Vector2 size)
        {
            base.GetActualBounds(parentBounds, out trans, out size);
            switch (FitType)
            {
                case EFitType.None:
                    break;
                case EFitType.Stretch:
                    size = parentBounds.Extents;
                    break;
                case EFitType.Center:
                    {
                        float aspect = GetAspect();
                        float parentAspect = parentBounds.Width / parentBounds.Height;
                        if (aspect > parentAspect)
                        {
                            size.X = parentBounds.Width;
                            size.Y = parentBounds.Width / aspect;
                        }
                        else
                        {
                            size.Y = parentBounds.Height;
                            size.X = parentBounds.Height * aspect;
                        }
                    }
                    break;
                case EFitType.Fill:
                    {
                        float aspect = GetAspect();
                        float parentAspect = parentBounds.Width / parentBounds.Height;
                        if (aspect > parentAspect)
                        {
                            size.Y = parentBounds.Height;
                            size.X = parentBounds.Height * aspect;
                        }
                        else
                        {
                            size.X = parentBounds.Width;
                            size.Y = parentBounds.Width / aspect;
                        }
                        break;
                    }
            }
        }
    }
}
