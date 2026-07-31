using System;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free value snapshot of dynamic-rendering local-read attachment
/// mappings used by primary/secondary inheritance validation.
/// </summary>
internal readonly struct DynamicRenderingLocalReadSignature :
    IEquatable<DynamicRenderingLocalReadSignature>
{
    [InlineArray(DynamicRenderingFormatSignature.MaxColorAttachmentCount)]
    private struct MappingStorage
    {
        private uint _element0;
    }

    private readonly MappingStorage _colorAttachmentLocations;
    private readonly MappingStorage _colorInputAttachmentIndices;
    private readonly byte _colorAttachmentLocationCount;
    private readonly byte _colorInputAttachmentIndexCount;

    private DynamicRenderingLocalReadSignature(
        in DynamicRenderingLocalReadPlan plan)
    {
        if (plan.ColorAttachmentLocations.Length >
                DynamicRenderingFormatSignature.MaxColorAttachmentCount ||
            plan.ColorInputAttachmentIndices.Length >
                DynamicRenderingFormatSignature.MaxColorAttachmentCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(plan),
                "Dynamic-rendering local-read mappings exceed the engine's portable color-attachment limit.");
        }

        MappingStorage locations = default;
        for (int index = 0;
             index < plan.ColorAttachmentLocations.Length;
             index++)
        {
            locations[index] =
                plan.ColorAttachmentLocations[index];
        }

        MappingStorage inputIndices = default;
        for (int index = 0;
             index < plan.ColorInputAttachmentIndices.Length;
             index++)
        {
            inputIndices[index] =
                plan.ColorInputAttachmentIndices[index];
        }

        _colorAttachmentLocations = locations;
        _colorInputAttachmentIndices = inputIndices;
        _colorAttachmentLocationCount =
            checked((byte)plan.ColorAttachmentLocations.Length);
        _colorInputAttachmentIndexCount =
            checked((byte)plan.ColorInputAttachmentIndices.Length);
        DepthInputAttachmentIndex =
            plan.DepthInputAttachmentIndex;
        StencilInputAttachmentIndex =
            plan.StencilInputAttachmentIndex;
    }

    public int ColorAttachmentLocationCount
        => _colorAttachmentLocationCount;

    public int ColorInputAttachmentIndexCount
        => _colorInputAttachmentIndexCount;

    public uint? DepthInputAttachmentIndex { get; }

    public uint? StencilInputAttachmentIndex { get; }

    public bool Enabled =>
        _colorAttachmentLocationCount > 0 ||
        _colorInputAttachmentIndexCount > 0 ||
        DepthInputAttachmentIndex.HasValue ||
        StencilInputAttachmentIndex.HasValue;

    public uint GetColorAttachmentLocation(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= _colorAttachmentLocationCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _colorAttachmentLocations[index];
    }

    public uint GetColorInputAttachmentIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= _colorInputAttachmentIndexCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _colorInputAttachmentIndices[index];
    }

    public static DynamicRenderingLocalReadSignature Create(
        in DynamicRenderingLocalReadPlan plan)
        => plan.Enabled
            ? new DynamicRenderingLocalReadSignature(in plan)
            : default;

    public void CopyColorAttachmentLocations(
        Span<uint> destination)
    {
        if (destination.Length < _colorAttachmentLocationCount)
            throw new ArgumentException(
                "The destination cannot hold every attachment-location mapping.",
                nameof(destination));

        for (int index = 0;
             index < _colorAttachmentLocationCount;
             index++)
        {
            destination[index] =
                GetColorAttachmentLocation(index);
        }
    }

    public void CopyColorInputAttachmentIndices(
        Span<uint> destination)
    {
        if (destination.Length < _colorInputAttachmentIndexCount)
            throw new ArgumentException(
                "The destination cannot hold every input-attachment mapping.",
                nameof(destination));

        for (int index = 0;
             index < _colorInputAttachmentIndexCount;
             index++)
        {
            destination[index] =
                GetColorInputAttachmentIndex(index);
        }
    }

    public bool Equals(
        DynamicRenderingLocalReadSignature other)
    {
        if (_colorAttachmentLocationCount !=
                other._colorAttachmentLocationCount ||
            _colorInputAttachmentIndexCount !=
                other._colorInputAttachmentIndexCount ||
            DepthInputAttachmentIndex !=
                other.DepthInputAttachmentIndex ||
            StencilInputAttachmentIndex !=
                other.StencilInputAttachmentIndex)
        {
            return false;
        }

        for (int index = 0;
             index < _colorAttachmentLocationCount;
             index++)
        {
            if (GetColorAttachmentLocation(index) !=
                other.GetColorAttachmentLocation(index))
            {
                return false;
            }
        }

        for (int index = 0;
             index < _colorInputAttachmentIndexCount;
             index++)
        {
            if (GetColorInputAttachmentIndex(index) !=
                other.GetColorInputAttachmentIndex(index))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj)
        => obj is DynamicRenderingLocalReadSignature other &&
            Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(_colorAttachmentLocationCount);
        for (int index = 0;
             index < _colorAttachmentLocationCount;
             index++)
        {
            hash.Add(GetColorAttachmentLocation(index));
        }

        hash.Add(_colorInputAttachmentIndexCount);
        for (int index = 0;
             index < _colorInputAttachmentIndexCount;
             index++)
        {
            hash.Add(GetColorInputAttachmentIndex(index));
        }

        hash.Add(DepthInputAttachmentIndex);
        hash.Add(StencilInputAttachmentIndex);
        return hash.ToHashCode();
    }
}
