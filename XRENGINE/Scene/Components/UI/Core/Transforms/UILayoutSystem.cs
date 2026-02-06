using System.Collections;
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
        // Controlled by Engine.EditorPreferences.Debug.EnableUILayoutDebugLogging
        public static bool EnableDebugLogging = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void LogUI(string message)
        {
            if (EnableDebugLogging)
                Debug.Log(ELogCategory.UI, message);
        }

        #region Canvas Layout Entry Points

        /// <summary>
        /// Root method to update the layout of a canvas synchronously.
        /// </summary>
        public static void UpdateCanvasLayout(UICanvasTransform canvas)
        {
            using var sample = Engine.Profiler.Start();

            // If the layout is not invalidated, or a parent canvas will control its layouting, don't update it as root canvas.
            if (!canvas.IsLayoutInvalidated || canvas.IsNestedCanvas)
                return;

            // Cancel any pending async layout
            canvas.CancelLayoutJob();

            canvas.SetLayoutInvalidated(false);
            canvas.SetUpdatingLayout(true);
            canvas.RaiseLayoutingStarted();

            BoundingRectangleF bounds;
            using (Engine.Profiler.Start("UpdateCanvasLayout.GetRootCanvasBounds"))
                bounds = canvas.GetRootCanvasBounds();

            LogUI($"Canvas UpdateLayout: bounds={bounds.Translation} size={bounds.Extents}");

            // Phase 1: Measure (bottom-up, can skip unchanged subtrees)
            using (Engine.Profiler.Start("UpdateCanvasLayout.MeasureTransform"))
                MeasureTransform(canvas, bounds.Extents);

            // Phase 2: Arrange (top-down, can skip unchanged subtrees)
            using (Engine.Profiler.Start("UpdateCanvasLayout.ArrangeTransform"))
                ArrangeTransform(canvas, bounds);

            canvas.SetUpdatingLayout(false);
            canvas.RaiseLayoutingFinished();
        }

        /// <summary>
        /// Invalidates the layout of a canvas.
        /// </summary>
        public static void InvalidateCanvasLayout(UICanvasTransform canvas)
        {
            canvas.IncrementLayoutVersionInternal();
            canvas.SetLayoutInvalidated(true);

            // When the canvas layout is invalidated (e.g., size change), we must also
            // invalidate all children so they recalculate positions based on anchors.
            InvalidateChildrenRecursive(canvas);
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
            transform.IncrementLayoutVersionInternal();
            if (transform.ParentCanvas != null && transform.ParentCanvas != transform)
                transform.ParentCanvas.InvalidateLayout();
            // NOTE: MarkLocalModified is NOT called here. Layout invalidation marks
            // the layout as dirty (version-based) but does not dirty the matrix.
            // The matrix is only marked dirty during the arrange phase when
            // actual positions change (via OnResizeActual → MarkLocalModified).
            // Properties that directly affect the matrix (Scale, DepthTranslation)
            // call MarkLocalModified explicitly in OnPropertyChanged.
            transform.RaiseLayoutInvalidated();
        }

        /// <summary>
        /// Invalidates only the measure phase.
        /// </summary>
        public static void InvalidateMeasure(UITransform transform)
        {
            transform.IncrementLayoutVersionInternal();

            // Propagate measure invalidation up for auto-sizing parents
            if (transform.Parent is UIBoundableTransform parentBoundable && parentBoundable.UsesAutoSizing)
                InvalidateMeasure(parentBoundable);

            // Invalidate layout on the parent canvas, or on ourselves if we ARE the root canvas
            if (transform.ParentCanvas != null && transform.ParentCanvas != transform)
                transform.ParentCanvas.InvalidateLayout();
            else if (transform is UICanvasTransform canvasSelf)
                canvasSelf.InvalidateLayout();
        }

        /// <summary>
        /// Invalidates only the arrange phase.
        /// </summary>
        public static void InvalidateArrange(UITransform transform)
        {
            // Only increment if we haven't already invalidated measure
            if (!transform.NeedsMeasure)
                transform.IncrementLayoutVersionInternal();

            // Invalidate layout on the parent canvas, or on ourselves if we ARE the root canvas
            if (transform.ParentCanvas != null && transform.ParentCanvas != transform)
                transform.ParentCanvas.InvalidateLayout();
            else if (transform is UICanvasTransform canvasSelf)
                canvasSelf.InvalidateLayout();
        }

        #endregion

        #region Measure Phase

        /// <summary>
        /// Measure phase entry point for UITransform.
        /// </summary>
        public static Vector2 MeasureTransform(UITransform transform, Vector2 availableSize)
        {
            // For boundable transforms, use the specialized measure
            if (transform is UIBoundableTransform boundable)
                return MeasureBoundable(boundable, availableSize);

            // Skip if already measured with same constraints
            if (!transform.NeedsMeasure && XRMath.VectorsEqual(transform.LastMeasureConstraint, availableSize))
                return transform.DesiredSize;

            using var profiler = Engine.Profiler.Start();

            LogUI($"Measure UITransform: {transform.GetType().Name} available={availableSize}");

            transform.SetLastMeasureConstraint(availableSize);

            // Measure children and aggregate their sizes
            Vector2 childrenSize = MeasureChildren(transform, availableSize);
            transform.SetDesiredSize(childrenSize);

            transform.SetLastMeasuredVersion();
            LogUI($"  -> desired={childrenSize}");
            return childrenSize;
        }

        /// <summary>
        /// Measure phase for UIBoundableTransform.
        /// </summary>
        public static Vector2 MeasureBoundable(UIBoundableTransform transform, Vector2 availableSize)
        {
            if (transform.IsCollapsed)
            {
                transform.SetDesiredSize(Vector2.Zero);
                transform.SetLastMeasuredVersion();
                return Vector2.Zero;
            }

            // Skip if already measured with same constraints and not dirty
            if (!transform.NeedsMeasure && XRMath.VectorsEqual(transform.LastMeasureConstraint, availableSize))
                return transform.DesiredSize;

            using var profiler = Engine.Profiler.Start();

            LogUI($"Measure UIBoundableTransform: {transform.GetType().Name} available={availableSize}");

            transform.SetLastMeasureConstraint(availableSize);

            // Calculate desired size based on explicit dimensions or auto-sizing
            float desiredWidth;
            float desiredHeight;

            if (transform.Width.HasValue)
            {
                desiredWidth = transform.Width.Value;
            }
            else
            {
                // Auto width: use callback or call instance method (allows overrides in derived classes)
                if (transform.CalcAutoWidthCallback != null)
                    desiredWidth = transform.CalcAutoWidthCallback(transform);
                else
                    desiredWidth = transform.InvokeMeasureChildrenWidth(availableSize);
            }

            if (transform.Height.HasValue)
            {
                desiredHeight = transform.Height.Value;
            }
            else
            {
                // Auto height: use callback or call instance method (allows overrides in derived classes)
                if (transform.CalcAutoHeightCallback != null)
                    desiredHeight = transform.CalcAutoHeightCallback(transform);
                else
                    desiredHeight = transform.InvokeMeasureChildrenHeight(availableSize);
            }

            Vector2 desiredSize = new(desiredWidth, desiredHeight);

            // Apply size constraints
            ClampSize(transform, ref desiredSize);

            // Add margins to desired size for parent layout calculations
            desiredSize = new Vector2(
                ApplyHorizontalMargins(transform, desiredSize.X),
                ApplyVerticalMargins(transform, desiredSize.Y)
            );

            // Always ensure all children are measured so their DesiredSize is populated.
            // When Width/Height are explicit (e.g., the root canvas), the auto-sizing
            // InvokeMeasureChildrenWidth/Height paths above are skipped, but the arrange
            // phase (e.g., UIListTransform.ArrangeChildren) reads child DesiredSize for
            // item sizing. Without this, children have DesiredSize = Zero and items overlap.
            // The NeedsMeasure + LastMeasureConstraint guard prevents redundant work when
            // children were already measured by the auto-sizing path.
            MeasureChildren(transform, availableSize);

            transform.SetDesiredSize(desiredSize);
            transform.SetLastMeasuredVersion();

            LogUI($"  -> desired={desiredSize}");
            return desiredSize;
        }

        /// <summary>
        /// Measures all child transforms and returns the aggregate size.
        /// </summary>
        public static Vector2 MeasureChildren(UITransform transform, Vector2 availableSize)
        {
            Vector2 maxSize = Vector2.Zero;
            try
            {
                foreach (var child in transform.Children)
                {
                    if (child is UITransform uiChild && !uiChild.IsCollapsed)
                    {
                        var childSize = MeasureTransform(uiChild, availableSize);
                        maxSize = Vector2.Max(maxSize, childSize);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.UI($"UILayoutSystem.MeasureChildren exception: {ex.Message}");
            }
            return maxSize;
        }

        /// <summary>
        /// Measures children to determine required width.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float MeasureChildrenWidth(UIBoundableTransform transform, Vector2 availableSize)
        {
            return transform.GetMaxChildWidth();
        }

        /// <summary>
        /// Measures children to determine required height.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float MeasureChildrenHeight(UIBoundableTransform transform, Vector2 availableSize)
        {
            return transform.GetMaxChildHeight();
        }

        #endregion

        #region Arrange Phase

        /// <summary>
        /// Arrange phase entry point for UITransform.
        /// </summary>
        public static void ArrangeTransform(UITransform transform, BoundingRectangleF finalBounds)
        {
            // For boundable transforms, use the specialized arrange
            if (transform is UIBoundableTransform boundable)
            {
                ArrangeBoundable(boundable, finalBounds);
                return;
            }

            // Skip if already arranged with same bounds
            if (!transform.NeedsArrange && transform.LastArrangeBounds.Equals(finalBounds))
                return;

            using var profiler = Engine.Profiler.Start();

            LogUI($"Arrange UITransform: {transform.GetType().Name} bounds={finalBounds.Translation} size={finalBounds.Extents}");

            transform.SetLastArrangeBounds(finalBounds);
            transform.OnResizeActualInternal(finalBounds);
            transform.SetLastArrangedVersion();

            // Arrange children within the same region
            ArrangeChildren(transform, finalBounds);
        }

        /// <summary>
        /// Arrange phase for UIBoundableTransform.
        /// </summary>
        public static void ArrangeBoundable(UIBoundableTransform transform, BoundingRectangleF finalBounds)
        {
            // Skip if already arranged with same bounds and not dirty
            if (!transform.NeedsArrange && transform.LastArrangeBounds.Equals(finalBounds))
                return;

            using var profiler = Engine.Profiler.Start();

            LogUI($"Arrange UIBoundableTransform: {transform.GetType().Name} bounds={finalBounds.Translation} size={finalBounds.Extents}");

            transform.SetLastArrangeBounds(finalBounds);
            
            // Pass the full parent bounds to OnResizeActual.
            // Margin handling is done entirely within GetActualBounds to avoid double-application.
            transform.OnResizeActualInternal(finalBounds);

            // OnResizeActual already calls MarkLocalModified(deferred) when bounds changed.
            // No need for a second ShouldMarkLocalMatrixChanged / MarkLocalModified call here.

            transform.SetLastArrangedVersion();

            // Get actual bounds for children
            var actualBounds = transform.GetActualBounds();
            LogUI($"  -> actual pos={actualBounds.Translation} size={actualBounds.Extents}");

            // Arrange children - calls the virtual method which can be overridden
            transform.InvokeArrangeChildren(actualBounds);
        }

        /// <summary>
        /// Arrange children of a basic UITransform.
        /// </summary>
        public static void ArrangeChildren(UITransform transform, BoundingRectangleF childRegion)
        {
            try
            {
                foreach (var child in transform.Children)
                {
                    if (child is UITransform uiChild)
                        ArrangeTransform(uiChild, childRegion);
                }
            }
            catch (Exception ex)
            {
                Debug.UI($"UILayoutSystem.ArrangeChildren exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Arrange children of a UIBoundableTransform within the padded region.
        /// </summary>
        public static void ArrangeChildrenBoundable(UIBoundableTransform transform, BoundingRectangleF childRegion)
        {
            var paddedRegion = ApplyPadding(transform, childRegion);
            try
            {
                foreach (var child in transform.Children)
                {
                    if (child is UIBoundableTransform uiChild)
                        ArrangeBoundable(uiChild, paddedRegion);
                    else if (child is UITransform uiTfm)
                        ArrangeTransform(uiTfm, paddedRegion);
                }
            }
            catch (Exception ex)
            {
                Debug.UI($"UILayoutSystem.ArrangeChildrenBoundable exception: {ex.Message}");
            }
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
            using var profiler = Engine.Profiler.Start();

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

            LogUI($"  GetActualBounds: anchors minX={minX} minY={minY} maxX={maxX} maxY={maxY} sameX={sameX} sameY={sameY}");
            LogUI($"    -> bottomLeft={bottomLeftTranslation} size={size}");
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

        #region Async Layout (Coroutine-based)

        /// <summary>
        /// Helper class to track processed count across coroutine calls.
        /// </summary>
        public class LayoutCounter
        {
            public int Count;
            public void Increment() => Count++;
            public void Reset() => Count = 0;
        }

        /// <summary>
        /// Coroutine that performs layout in two phases, yielding periodically to avoid frame hitches.
        /// </summary>
        public static IEnumerable LayoutCoroutine(UICanvasTransform canvas, int maxItemsPerFrame)
        {
            var bounds = canvas.GetRootCanvasBounds();
            var counter = new LayoutCounter();

            // Phase 1: Measure pass (bottom-up traversal with yields)
            foreach (var step in MeasureCoroutine(canvas, bounds.Extents, counter, maxItemsPerFrame))
            {
                if (counter.Count >= maxItemsPerFrame)
                {
                    counter.Reset();
                    yield return null; // Yield to next frame
                }
            }

            yield return null; // Yield between phases

            counter.Reset();

            // Phase 2: Arrange pass (top-down traversal with yields)
            foreach (var step in ArrangeCoroutine(canvas, bounds, counter, maxItemsPerFrame))
            {
                if (counter.Count >= maxItemsPerFrame)
                {
                    counter.Reset();
                    yield return null; // Yield to next frame
                }
            }
        }

        /// <summary>
        /// Coroutine for the measure phase.
        /// </summary>
        public static IEnumerable MeasureCoroutine(UITransform transform, Vector2 availableSize, LayoutCounter counter, int maxItemsPerFrame)
        {
            // Measure this transform
            MeasureTransform(transform, availableSize);
            counter.Increment();
            yield return null;

            // Recursively measure children
            foreach (var child in transform.Children)
            {
                if (child is UIBoundableTransform boundableChild)
                {
                    foreach (var step in MeasureChildCoroutine(boundableChild, availableSize, counter, maxItemsPerFrame))
                        yield return step;
                }
                else if (child is UITransform uiChild)
                {
                    MeasureTransform(uiChild, availableSize);
                    counter.Increment();
                    yield return null;
                }
            }
        }

        private static IEnumerable MeasureChildCoroutine(UIBoundableTransform child, Vector2 availableSize, LayoutCounter counter, int maxItemsPerFrame)
        {
            // Skip if child doesn't need measuring
            if (!child.NeedsMeasure)
                yield break;

            MeasureBoundable(child, availableSize);
            counter.Increment();
            yield return null;

            // Recursively measure grandchildren
            foreach (var grandchild in child.Children)
            {
                if (grandchild is UIBoundableTransform boundableGrandchild)
                {
                    foreach (var step in MeasureChildCoroutine(boundableGrandchild, availableSize, counter, maxItemsPerFrame))
                        yield return step;
                }
                else if (grandchild is UITransform uiGrandchild)
                {
                    MeasureTransform(uiGrandchild, availableSize);
                    counter.Increment();
                    yield return null;
                }
            }
        }

        /// <summary>
        /// Coroutine for the arrange phase.
        /// </summary>
        public static IEnumerable ArrangeCoroutine(UIBoundableTransform transform, BoundingRectangleF bounds, LayoutCounter counter, int maxItemsPerFrame)
        {
            // Arrange this transform
            ArrangeBoundable(transform, bounds);
            counter.Increment();
            yield return null;

            // Get child region after arranging self
            var childRegion = ApplyPadding(transform, transform.GetActualBounds());

            // Recursively arrange children
            foreach (var child in transform.Children)
            {
                if (child is UIBoundableTransform boundableChild)
                {
                    foreach (var step in ArrangeChildCoroutine(boundableChild, childRegion, counter, maxItemsPerFrame))
                        yield return step;
                }
                else if (child is UITransform uiChild)
                {
                    ArrangeTransform(uiChild, childRegion);
                    counter.Increment();
                    yield return null;
                }
            }
        }

        private static IEnumerable ArrangeChildCoroutine(UIBoundableTransform child, BoundingRectangleF bounds, LayoutCounter counter, int maxItemsPerFrame)
        {
            // Skip if child doesn't need arranging AND bounds haven't changed.
            // Using the same dual guard as the synchronous ArrangeBoundable path
            // to handle cases where NeedsArrange was cleared but position changed
            // (e.g., split panel Y offset shifts on height-only window resize).
            if (!child.NeedsArrange && child.LastArrangeBounds.Equals(bounds))
                yield break;

            ArrangeBoundable(child, bounds);
            counter.Increment();
            yield return null;

            // Get child's child region
            var grandchildRegion = ApplyPadding(child, child.GetActualBounds());

            // Recursively arrange grandchildren
            foreach (var grandchild in child.Children)
            {
                if (grandchild is UIBoundableTransform boundableGrandchild)
                {
                    foreach (var step in ArrangeChildCoroutine(boundableGrandchild, grandchildRegion, counter, maxItemsPerFrame))
                        yield return step;
                }
                else if (grandchild is UITransform uiGrandchild)
                {
                    ArrangeTransform(uiGrandchild, grandchildRegion);
                    counter.Increment();
                    yield return null;
                }
            }
        }

        #endregion

        #region Legacy FitLayout Support

        /// <summary>
        /// Legacy single-pass layout method for boundable transforms.
        /// </summary>
        public static void FitLayout(UIBoundableTransform transform, BoundingRectangleF parentBounds)
        {
            using var profiler = Engine.Profiler.Start();

            // Use the two-phase approach for better dirty tracking
            MeasureBoundable(transform, parentBounds.Extents);
            ArrangeBoundable(transform, parentBounds);
        }

        #endregion
    }
}
