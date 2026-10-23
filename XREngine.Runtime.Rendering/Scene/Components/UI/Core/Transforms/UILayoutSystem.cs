using XREngine.Rendering.UI.State;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Centralized static class for all UI layout operations.
    /// Consolidates measure/arrange logic from UITransform, UIBoundableTransform, and UICanvasTransform.
    /// </summary>
    public static class UILayoutSystem
    {
        // Controlled by RuntimeEngine.EditorPreferences.Debug.EnableUILayoutDebugLogging
        public static bool EnableDebugLogging = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void LogUI(string message)
        {
            if (EnableDebugLogging)
                Debug.Log(ELogCategory.UI, message);
        }

        #region Canvas Layout Entry Points

        /// <summary>
        /// Requests the immutable canvas layout path.
        /// </summary>
        public static void UpdateCanvasLayout(UICanvasTransform canvas)
        {
            canvas.UpdateLayoutAsync();
        }

        /// <summary>Schedule the owning canvas without applying live geometry.</summary>
        internal static void RequestAttachedLayout(UITransform transform)
        {
            UICanvasTransform canvas = transform.GetMutationCanvas() ??
                throw new InvalidOperationException("Arrange requires an attached UI canvas.");
            canvas.UpdateLayoutAsync();
        }

        /// <summary>Read accepted canvas geometry or measure a detached tree with the value kernel.</summary>
        internal static Vector2 MeasureAcceptedOrDetached(UITransform transform, Vector2 availableSize)
        {
            UICanvasTransform? canvas = transform.GetMutationCanvas();
            if (canvas is null)
            {
                UILayoutSnapshot input = UILayoutSnapshot.CaptureRoot(transform, availableSize, []);
                return UILayoutValueKernel.RunPaged(input, null, null)[0].DesiredSize;
            }

            canvas.UpdateLayoutAsync();
            ReadOnlySpan<UIElementIdentity> identities = canvas.CurrentLayoutIdentities.Span;
            UILayoutOutputPageView? pages = canvas.CurrentLayoutOutputPages;
            for (int i = 0; i < identities.Length; i++)
            {
                if (identities[i] != transform.ElementIdentity)
                    continue;
                if (pages is not null)
                    return pages[i].DesiredSize;
                ReadOnlySpan<UILayoutNodeOutput> flat = canvas.CurrentLayoutOutputs.Span;
                if (i < flat.Length)
                    return flat[i].DesiredSize;
            }
            throw new InvalidOperationException("The canvas has no accepted layout result for this transform yet.");
        }

        /// <summary>
        /// Invalidates the layout of a canvas.
        /// </summary>
        public static void InvalidateCanvasLayout(UICanvasTransform canvas)
        {
            InvalidateLocal(canvas, true);
            UIRuntimeCounters.Add(UIWorkCounter.GlobalInvalidations);
            // Accepted child slots are part of the arrange cache key. A new canvas
            // constraint therefore visits only descendants whose allocation changes.
        }

        /// <summary>
        /// Recursively invalidates the layout version of all child transforms.
        /// This ensures children will re-arrange when parent bounds change.
        /// </summary>
        public static void InvalidateChildrenRecursive(TransformBase transform)
        {
            foreach (var child in transform.Children)
            {
                if (child is UITransform uiChild)
                {
                    uiChild.ForceInvalidateArrange();
                    InvalidateChildrenRecursive(child);
                }
            }
        }

        #endregion

        #region Transform Invalidation

        /// <summary>
        /// Marks a transform's layout as needing recalculation.
        /// Propagates to the parent canvas for batched processing.
        /// </summary>
        public static void InvalidateLayout(UITransform transform)
        {
            InvalidateLocal(transform, true);
            transform.RaiseLayoutInvalidated();
        }

        /// <summary>Invalidates measure dependencies without scanning descendants.</summary>
        public static void InvalidateMeasure(UITransform transform)
            => InvalidateLocal(transform, true);

        /// <summary>Invalidates arrange dependencies while retaining measured sizes.</summary>
        public static void InvalidateArrange(UITransform transform)
            => InvalidateLocal(transform, false);

        private static void InvalidateLocal(UITransform transform, bool measure)
        {
            transform.RecordUIChange((measure ? UIChangeDomain.Measure : UIChangeDomain.None) |
                UIChangeDomain.Arrange | UIChangeDomain.Transform | UIChangeDomain.Clip | UIChangeDomain.HitTest);
            // The current layout entry walks through ancestor cache guards. Keep that path
            // dirty, but retain all unrelated descendant caches. Worker scheduling consumes
            // the journal later; no snapshot or descendant scan is needed at mutation time.
            for (TransformBase? current = transform; current is not null; current = current.Parent)
            {
                if (current is not UITransform ui)
                    continue;
                if (measure)
                    ui.IncrementLayoutVersionInternal();
                else
                    ui.IncrementArrangeVersionInternal();
                if (!ReferenceEquals(ui, transform))
                    UIRuntimeCounters.Add(UIWorkCounter.AncestorsInvalidated);
                if (ui is UICanvasTransform canvas)
                    canvas.SetLayoutInvalidated(true);
            }
            transform.GetMutationCanvas()?.SetLayoutInvalidated(true);
        }

        #endregion

        #region Measure Phase

        /// <summary>
        /// Measure phase entry point for UITransform.
        /// </summary>
        public static Vector2 MeasureTransform(UITransform transform, Vector2 availableSize)
        {
            return MeasureAcceptedOrDetached(transform, availableSize);
        }

        /// <summary>
        /// Measure phase for UIBoundableTransform.
        /// </summary>
        public static Vector2 MeasureBoundable(UIBoundableTransform transform, Vector2 availableSize)
        {
            return MeasureAcceptedOrDetached(transform, availableSize);
        }

        #endregion

        #region Arrange Phase

        /// <summary>
        /// Arrange phase entry point for UITransform.
        /// </summary>
        public static void ArrangeTransform(UITransform transform, BoundingRectangleF finalBounds)
        {
            RequestAttachedLayout(transform);
        }

        /// <summary>
        /// Arrange phase for UIBoundableTransform.
        /// </summary>
        public static void ArrangeBoundable(UIBoundableTransform transform, BoundingRectangleF finalBounds)
        {
            RequestAttachedLayout(transform);
        }

        /// <summary>
        /// Arrange children of a basic UITransform.
        /// </summary>
        public static void ArrangeChildren(UITransform transform, BoundingRectangleF childRegion)
        {
            RequestAttachedLayout(transform);
        }

        /// <summary>
        /// Arrange children of a UIBoundableTransform within the padded region.
        /// </summary>
        public static void ArrangeChildrenBoundable(UIBoundableTransform transform, BoundingRectangleF childRegion)
        {
            RequestAttachedLayout(transform);
        }

        #endregion

        #region Bounds Calculation

        /// <summary>
        /// Calculates the actual size and bottom left translation of a boundable component.
        /// </summary>
        public static void GetActualBounds(
            UIBoundableTransform transform,
            BoundingRectangleF parentBounds,
            out Vector2 bottomLeftTranslation,
            out Vector2 size)
        {
            using var profiler = RuntimeEngine.Profiler.Start();

            GetAnchors(
                transform,
                parentBounds.Width,
                parentBounds.Height,
                out float minX,
                out float minY,
                out float maxX,
                out float maxY);

            bool sameX = XRMath.Approx(maxX, minX);
            bool sameY = XRMath.Approx(maxY, minY);

            size = Vector2.Zero;
            if (sameX)
            {
                // If the min and max anchors are the same, use the width of the component.
                size.X = transform.GetWidth();
            }
            else
            {
                // Otherwise, calculate the size based on the anchors.
                // Translation is used as the translation from the min anchor, and width is used as the translation from the max anchor.
                float rawWidth = (maxX + (transform.Width ?? 0)) - (minX + transform.Translation.X);
                // Subtract both left and right margins for stretched anchors
                size.X = rawWidth - transform.Margins.X - transform.Margins.Z;
            }
            if (sameY)
            {
                // If the min and max anchors are the same, use the height of the component.
                size.Y = transform.GetHeight();
            }
            else
            {
                // Otherwise, calculate the size based on the anchors.
                // Translation is used as the translation from the min anchor, and height is used as the translation from the max anchor.
                float rawHeight = (maxY + (transform.Height ?? 0)) - (minY + transform.Translation.Y);
                // Subtract both bottom and top margins for stretched anchors
                size.Y = rawHeight - transform.Margins.Y - transform.Margins.W;
            }

            // Clamp the size to the min and max size.
            ClampSize(transform, ref size);

            // Adjust the translation based on the pivot.
            minX -= transform.NormalizedPivot.X * size.X;
            minY -= transform.NormalizedPivot.Y * size.Y;

            // If the min and max anchors are the same, add the translation to the min anchor position.
            if (sameX)
                minX += transform.Translation.X;
            if (sameY)
                minY += transform.Translation.Y;

            // Apply margins based on anchor position
            // For point anchors: use the margin corresponding to the anchor edge
            // For stretched anchors: size was already reduced above; position offset by left/bottom margin
            float marginX, marginY;
            if (sameX)
            {
                // Point anchor in X: use left margin for left-anchored, right margin (negative) for right-anchored
                float anchorX = transform.MinAnchor.X;
                // Lerp between left margin (at anchor 0) and negative right margin (at anchor 1)
                marginX = transform.Margins.X * (1.0f - anchorX) - transform.Margins.Z * anchorX;
            }
            else
            {
                // Stretched anchor: left margin offsets position from left edge
                marginX = transform.Margins.X;
            }

            if (sameY)
            {
                // Point anchor in Y: use bottom margin for bottom-anchored, top margin (negative) for top-anchored
                float anchorY = transform.MinAnchor.Y;
                // Lerp between bottom margin (at anchor 0) and negative top margin (at anchor 1)
                marginY = transform.Margins.Y * (1.0f - anchorY) - transform.Margins.W * anchorY;
            }
            else
            {
                // Stretched anchor: bottom margin offsets position from bottom edge
                marginY = transform.Margins.Y;
            }

            bottomLeftTranslation = new(minX + marginX, minY + marginY);

            if (EnableDebugLogging) LogUI($"  GetActualBounds: anchors minX={minX} minY={minY} maxX={maxX} maxY={maxY} sameX={sameX} sameY={sameY}");
            if (EnableDebugLogging) LogUI($"    -> bottomLeft={bottomLeftTranslation} size={size}");
        }

        #endregion

        #region Helper Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ClampSize(UIBoundableTransform transform, ref Vector2 size)
        {
            if (transform.MinWidth.HasValue)
                size.X = Math.Max(size.X, transform.MinWidth.Value);
            if (transform.MinHeight.HasValue)
                size.Y = Math.Max(size.Y, transform.MinHeight.Value);
            if (transform.MaxWidth.HasValue)
                size.X = Math.Min(size.X, transform.MaxWidth.Value);
            if (transform.MaxHeight.HasValue)
                size.Y = Math.Min(size.Y, transform.MaxHeight.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GetAnchors(
            UIBoundableTransform transform,
            float parentWidth,
            float parentHeight,
            out float minX,
            out float minY,
            out float maxX,
            out float maxY)
        {
            minX = parentWidth * transform.MinAnchor.X;
            maxX = parentWidth * transform.MaxAnchor.X;
            minY = parentHeight * transform.MinAnchor.Y;
            maxY = parentHeight * transform.MaxAnchor.Y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ApplyHorizontalMargins(UIBoundableTransform transform, float width)
            => width + transform.Margins.X + transform.Margins.Z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ApplyVerticalMargins(UIBoundableTransform transform, float height)
            => height + transform.Margins.Y + transform.Margins.W;

        /// <summary>
        /// Applies padding to the bounds, reducing the available area for children.
        /// </summary>
        public static BoundingRectangleF ApplyPadding(UIBoundableTransform transform, BoundingRectangleF bounds)
        {
            var padding = transform.Padding;
            float left = padding.X;
            float bottom = padding.Y;
            float right = padding.Z;
            float top = padding.W;

            Vector2 size = bounds.Extents;
            Vector2 pos = bounds.Translation;

            pos += new Vector2(left, bottom);
            size -= new Vector2(left + right, bottom + top);
            return new BoundingRectangleF(pos, size);
        }

        /// <summary>
        /// Applies margins to the bounds, reducing the available area.
        /// </summary>
        public static BoundingRectangleF ApplyMargins(UIBoundableTransform transform, BoundingRectangleF bounds)
        {
            var margins = transform.Margins;
            float left = margins.X;
            float bottom = margins.Y;
            float right = margins.Z;
            float top = margins.W;

            Vector2 size = bounds.Extents;
            Vector2 pos = bounds.Translation;

            pos += new Vector2(left, bottom);
            size -= new Vector2(left + right, bottom + top);
            return new BoundingRectangleF(pos, size);
        }

        #endregion

        #region Legacy FitLayout Support

        /// <summary>
        /// Requests canvas layout for a boundable transform.
        /// </summary>
        public static void FitLayout(UIBoundableTransform transform, BoundingRectangleF parentBounds)
        {
            UICanvasTransform canvas = transform.GetMutationCanvas() ??
                throw new InvalidOperationException("A boundable transform needs a canvas for layout.");
            canvas.SetLayoutInvalidated(true);
            canvas.UpdateLayoutAsync();
        }

        #endregion
    }
}
