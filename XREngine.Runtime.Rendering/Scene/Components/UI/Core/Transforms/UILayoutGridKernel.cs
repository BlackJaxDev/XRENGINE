using System;
using System.Buffers;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.UI;

/// <summary>Resolves fixed, auto, and proportional grid tracks from frozen layout values.</summary>
internal static class UILayoutGridKernel
{
    internal static void ArrangeGrid(
        in UILayoutNodeInput node, BoundingRectangleF region,
        UILayoutValuePageView<UILayoutNodeInput> nodes, ReadOnlySpan<UILayoutChildInput> children,
        UILayoutOutputPageBuilder outputs, bool visible, UILayoutArrangeCache? cache = null)
    {
        ReadOnlySpan<UILayoutGridTrack> rows = node.GridRows.Span;
        ReadOnlySpan<UILayoutGridTrack> columns = node.GridColumns.Span;
        if (node.ChildCount == 0)
            return;
        if (rows.IsEmpty || columns.IsEmpty)
            throw new InvalidOperationException("A grid with children requires rows and columns.");

        float[]? rentedRows = null;
        float[]? rentedColumns = null;
        Span<float> rowSizes = rows.Length <= 256 ? stackalloc float[rows.Length] :
            (rentedRows = ArrayPool<float>.Shared.Rent(rows.Length)).AsSpan(0, rows.Length);
        Span<float> columnSizes = columns.Length <= 256 ? stackalloc float[columns.Length] :
            (rentedColumns = ArrayPool<float>.Shared.Rent(columns.Length)).AsSpan(0, columns.Length);
        try
        {
            ResolveAxis(rows, region.Height, false, node, nodes, children, outputs, rowSizes);
            ResolveAxis(columns, region.Width, true, node, nodes, children, outputs, columnSizes);

            for (int i = 0; i < node.ChildCount; i++)
            {
                UILayoutChildInput child = children[node.FirstChild + i];
                ValidatePlacement(child, rows.Length, columns.Length);
                float x = 0;
                for (int c = 0; c < child.Column; c++)
                    x += columnSizes[c];
                float width = 0;
                for (int c = child.Column; c < child.Column + child.ColumnSpan; c++)
                    width += columnSizes[c];
                float before = 0;
                for (int r = 0; r < child.Row; r++)
                    before += rowSizes[r];
                float height = 0;
                for (int r = child.Row; r < child.Row + child.RowSpan; r++)
                    height += rowSizes[r];
                float y = (node.OperatorFlags & 1) != 0 ? before : region.Height - before - height;
                Vector2 position = new(x, y);
                UILayoutValueKernel.Arrange(child.NodeIndex,
                    new BoundingRectangleF(position, new Vector2(width, height)),
                    position + region.Translation, visible, nodes, children, outputs, cache);
            }
        }
        finally
        {
            if (rentedRows is not null)
                ArrayPool<float>.Shared.Return(rentedRows);
            if (rentedColumns is not null)
                ArrayPool<float>.Shared.Return(rentedColumns);
        }
    }

    private static void ResolveAxis(
        ReadOnlySpan<UILayoutGridTrack> tracks, float extent, bool horizontal,
        in UILayoutNodeInput node, UILayoutValuePageView<UILayoutNodeInput> nodes,
        ReadOnlySpan<UILayoutChildInput> children, UILayoutOutputPageBuilder outputs,
        Span<float> sizes)
    {
        if (!float.IsFinite(extent) || extent < 0)
            throw new InvalidOperationException("Grid extent must be finite and nonnegative.");
        sizes.Clear();
        for (int i = 0; i < tracks.Length; i++)
        {
            ValidateTrack(tracks[i]);
            if (tracks[i].Mode == ESizingMode.Fixed)
                sizes[i] = tracks[i].Value;
        }

        // Each span contributes only the size still needed after fixed tracks and prior auto contributions.
        for (int i = 0; i < node.ChildCount; i++)
        {
            UILayoutChildInput child = children[node.FirstChild + i];
            int start = horizontal ? child.Column : child.Row;
            int span = horizontal ? child.ColumnSpan : child.RowSpan;
            if (start < 0 || span < 1 || (long)start + span > tracks.Length)
                throw new InvalidOperationException("Grid child placement is outside the track range.");
            if (nodes[child.NodeIndex].Collapsed)
                continue;
            float desired = horizontal ? outputs[child.NodeIndex].DesiredSize.X : outputs[child.NodeIndex].DesiredSize.Y;
            if (!float.IsFinite(desired) || desired < 0)
                throw new InvalidOperationException("Grid child desired size must be finite and nonnegative.");
            float occupied = 0;
            int autoCount = 0;
            for (int t = start; t < start + span; t++)
            {
                occupied += sizes[t];
                if (tracks[t].Mode == ESizingMode.Auto)
                    autoCount++;
            }
            if (autoCount == 0 || desired <= occupied)
                continue;
            float addition = (desired - occupied) / autoCount;
            for (int t = start; t < start + span; t++)
                if (tracks[t].Mode == ESizingMode.Auto)
                    sizes[t] += addition;
        }

        float used = 0;
        double weight = 0;
        for (int i = 0; i < tracks.Length; i++)
        {
            if (tracks[i].Mode == ESizingMode.Proportional)
                weight += tracks[i].Value;
            else
            {
                sizes[i] = ClampTrack(sizes[i], tracks[i], extent);
                used += sizes[i];
            }
        }

        float remaining = MathF.Max(0, extent - used);
        if (weight <= 0)
        {
            for (int i = 0; i < tracks.Length; i++)
                if (tracks[i].Mode == ESizingMode.Proportional)
                    sizes[i] = ClampTrack(0, tracks[i], extent);
            return;
        }
        byte[]? rentedMask = null;
        Span<byte> fixedMask = tracks.Length <= 256 ? stackalloc byte[tracks.Length] :
            (rentedMask = ArrayPool<byte>.Shared.Rent(tracks.Length)).AsSpan(0, tracks.Length);
        try
        {
            fixedMask.Clear();
            for (int pass = 0; pass < tracks.Length && weight > 0; pass++)
            {
                bool changed = false;
                for (int i = 0; i < tracks.Length; i++)
                {
                    if (tracks[i].Mode != ESizingMode.Proportional || fixedMask[i] != 0 || tracks[i].Value == 0)
                        continue;
                    GetBounds(tracks[i], extent, sizes[i], out float min, out float max);
                    float proposed = (float)(remaining * tracks[i].Value / weight);
                    if (proposed >= min && proposed <= max)
                        continue;
                    sizes[i] = Math.Clamp(proposed, min, max);
                    remaining = MathF.Max(0, remaining - sizes[i]);
                    weight -= tracks[i].Value;
                    fixedMask[i] = 1;
                    changed = true;
                }
                if (!changed)
                    break;
            }
            if (weight > 0)
                for (int i = 0; i < tracks.Length; i++)
                    if (tracks[i].Mode == ESizingMode.Proportional && fixedMask[i] == 0)
                        sizes[i] = ClampTrack((float)(remaining * tracks[i].Value / weight), tracks[i], extent);
        }
        finally
        {
            if (rentedMask is not null)
                ArrayPool<byte>.Shared.Return(rentedMask);
        }
    }

    private static float ClampTrack(float value, in UILayoutGridTrack track, float extent)
    {
        GetBounds(track, extent, value, out float min, out float max);
        return Math.Clamp(value, min, max);
    }

    private static void GetBounds(in UILayoutGridTrack track, float extent, float value, out float min, out float max)
    {
        min = track.MinMode switch
        {
            ESizingMode.Fixed => track.MinValue,
            ESizingMode.Proportional => track.MinValue * extent,
            ESizingMode.Auto => value,
            _ => 0
        };
        max = track.MaxMode switch
        {
            ESizingMode.Fixed => track.MaxValue,
            ESizingMode.Proportional => track.MaxValue * extent,
            ESizingMode.Auto => value,
            _ => float.MaxValue
        };
        if (!float.IsFinite(min) || !float.IsFinite(max) || min > max)
            throw new InvalidOperationException("Grid track minimum exceeds maximum.");
    }

    private static void ValidateTrack(in UILayoutGridTrack track)
    {
        if (!Enum.IsDefined(track.Mode) || !float.IsFinite(track.Value) || track.Value < 0 ||
            (track.MinMode.HasValue && (!Enum.IsDefined(track.MinMode.Value) || !float.IsFinite(track.MinValue) || track.MinValue < 0)) ||
            (track.MaxMode.HasValue && (!Enum.IsDefined(track.MaxMode.Value) || !float.IsFinite(track.MaxValue) || track.MaxValue < 0)))
            throw new InvalidOperationException("Grid track sizing values must be valid and nonnegative.");
    }

    private static void ValidatePlacement(in UILayoutChildInput child, int rowCount, int columnCount)
    {
        if (child.Row < 0 || child.Column < 0 || child.RowSpan < 1 || child.ColumnSpan < 1 ||
            (long)child.Row + child.RowSpan > rowCount || (long)child.Column + child.ColumnSpan > columnCount)
            throw new InvalidOperationException("Grid child placement is outside the track range.");
    }
}
