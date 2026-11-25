using System.Collections.Frozen;
using System.Numerics;
using XREngine.Data;

namespace XREngine.Rendering.UI;

/// <summary>Writes accepted immutable glyph values without copying layout storage.</summary>
internal static class UIGlyphBufferWriter
{
    internal static bool HasStorage(int glyphCount, uint capacity, bool animated,
        XRDataBuffer? transformsBuffer, XRDataBuffer? uvsBuffer, XRDataBuffer? rotationsBuffer)
    {
        ulong vectorBytes = checked((ulong)glyphCount * 16UL);
        ulong rotationBytes = checked((ulong)glyphCount * 4UL);
        return glyphCount <= capacity &&
            transformsBuffer is { IsDestroyed: false, ClientSideSource: { } transforms } &&
            uvsBuffer is { IsDestroyed: false, ClientSideSource: { } uvs } &&
            transformsBuffer.ElementCount >= glyphCount && uvsBuffer.ElementCount >= glyphCount &&
            transforms.Length >= vectorBytes && uvs.Length >= vectorBytes &&
            (vectorBytes == 0 || (transforms.Address != VoidPtr.Zero && uvs.Address != VoidPtr.Zero)) &&
            (!animated ||
             (rotationsBuffer is { IsDestroyed: false, ClientSideSource: { } rotations } &&
              rotationsBuffer.ElementCount >= glyphCount && rotations.Length >= rotationBytes &&
              (rotationBytes == 0 || rotations.Address != VoidPtr.Zero)));
    }

    internal static unsafe int Write(UITextLayoutResult layout,
        FrozenDictionary<int, (Vector2 translation, Vector2 scale, float rotation)> overrides,
        bool animated, XRDataBuffer transformsBuffer, XRDataBuffer uvsBuffer,
        XRDataBuffer? rotationsBuffer, bool force)
    {
        if (!HasStorage(layout.Glyphs.Length, transformsBuffer.ElementCount, animated,
            transformsBuffer, uvsBuffer, rotationsBuffer))
            throw new InvalidOperationException("UI.Text.GlyphStoragePending: complete glyph CPU storage is required before drawing.");
        ReadOnlySpan<UITextLayoutGlyph> glyphs = layout.Glyphs.Span;
        Span<Vector4> transformValues = new(transformsBuffer.ClientSideSource!.Address.Pointer, glyphs.Length);
        Span<Vector4> uvValues = new(uvsBuffer.ClientSideSource!.Address.Pointer, glyphs.Length);
        Span<float> rotations = animated && rotationsBuffer?.ClientSideSource is { } rotationSource
            ? new Span<float>(rotationSource.Address.Pointer, glyphs.Length) : Span<float>.Empty;
        int transformFirst = glyphs.Length, transformLast = -1;
        int uvFirst = glyphs.Length, uvLast = -1;
        int rotationFirst = glyphs.Length, rotationLast = -1;
        int changedGlyphs = 0;
        for (int index = 0; index < glyphs.Length; index++)
        {
            Vector4 transform = glyphs[index].Transform;
            float rotation = 0;
            if (animated && overrides.TryGetValue(index, out var relative))
            {
                transform.X += relative.translation.X;
                transform.Y += relative.translation.Y;
                transform.Z *= relative.scale.X;
                transform.W *= relative.scale.Y;
                rotation = relative.rotation;
            }
            bool changed = false;
            if (force || transformValues[index] != transform)
            {
                transformValues[index] = transform;
                transformFirst = Math.Min(transformFirst, index);
                transformLast = index;
                changed = true;
            }
            if (force || uvValues[index] != glyphs[index].Uvs)
            {
                uvValues[index] = glyphs[index].Uvs;
                uvFirst = Math.Min(uvFirst, index);
                uvLast = index;
                changed = true;
            }
            if (!rotations.IsEmpty && (force || rotations[index] != rotation))
            {
                rotations[index] = rotation;
                rotationFirst = Math.Min(rotationFirst, index);
                rotationLast = index;
                changed = true;
            }
            if (changed)
                changedGlyphs++;
        }
        CommitGlyphRange(transformsBuffer, transformFirst, transformLast, 16u);
        CommitGlyphRange(uvsBuffer, uvFirst, uvLast, 16u);
        CommitGlyphRange(rotationsBuffer, rotationFirst, rotationLast, 4u);
        return changedGlyphs;
    }

    private static void CommitGlyphRange(XRDataBuffer? buffer, int first, int last, uint stride)
    {
        if (buffer is not null && last >= first)
            buffer.CommitDirtyBytes(checked((uint)first * stride), checked((uint)(last - first + 1) * stride));
    }
}
