using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>World-owned active decal discovery; capture storage is independent of mutable component state.</summary>
internal sealed class AdvancedAuthoredDecalRegistry
{
    private static readonly ConditionalWeakTable<IRuntimeRenderWorld, AdvancedAuthoredDecalRegistry> Worlds = new();
    private readonly List<DeferredDecalComponent> _sources = [];
    private readonly List<AdvancedAuthoredDecalCaptureBuffer> _buffers = [];
    private readonly Dictionary<DeferredDecalComponent, (ulong Frame, Matrix4x4 Current, Matrix4x4 Previous)> _previous = new(ReferenceEqualityComparer.Instance);

    internal static void Register(IRuntimeRenderWorld world, DeferredDecalComponent source)
    {
        AdvancedAuthoredDecalRegistry registry = Worlds.GetValue(world, static _ => new());
        lock (registry)
            if (!registry._sources.Contains(source)) registry._sources.Add(source);
    }
    internal static void Unregister(IRuntimeRenderWorld world, DeferredDecalComponent source)
    {
        if (!Worlds.TryGetValue(world, out AdvancedAuthoredDecalRegistry? registry)) return;
        lock (registry)
        {
            registry._sources.Remove(source);
            registry._previous.Remove(source);
        }
    }

    internal static AdvancedAuthoredDecalCaptureLease Capture(IRuntimeRenderWorld world, ulong frameId)
    {
        if (!Worlds.TryGetValue(world, out AdvancedAuthoredDecalRegistry? registry)) return default;
        lock (registry) return registry.Capture(frameId);
    }

    private AdvancedAuthoredDecalCaptureLease Capture(ulong frameId)
    {
        if (_sources.Count == 0) return default;
        AdvancedAuthoredDecalCaptureBuffer? buffer = null;
        for (int index = 0; index < _buffers.Count; index++)
        {
            AdvancedAuthoredDecalCaptureBuffer candidate = _buffers[index];
            lock (candidate)
                if (!candidate.Leased) { candidate.Leased = true; buffer = candidate; break; }
        }
        if (buffer is null) { buffer = new() { Leased = true }; _buffers.Add(buffer); }
        if (buffer.Storage.Length < _sources.Count)
            Array.Resize(ref buffer.Storage, (int)BitOperations.RoundUpToPowerOf2((uint)_sources.Count));
        if (++buffer.Generation == 0) ++buffer.Generation;
        AdvancedAuthoredDecalCaptureLease lease = new(buffer, buffer.Generation);
        try
        {
            for (int index = 0; index < _sources.Count; index++)
            {
                DeferredDecalComponent source = _sources[index];
                if (!source.IsActiveInHierarchy || !source.RenderInfo.IsVisible || !source.RenderCommandDecal.Enabled || source.Material is null) continue;
                uint flags = AdvancedDecalRecord.EnabledFlag | AdvancedDecalRecord.AuthoredAlbedoFlag;
                if (source.UseForwardOit) flags |= AdvancedDecalRecord.ForwardOitFlag | AdvancedDecalRecord.UnsupportedAuthoredFlag;
                if (!source.HasDefaultDecalDrawContract())
                    flags |= AdvancedDecalRecord.UnsupportedDrawCommandFlag | AdvancedDecalRecord.UnsupportedAuthoredFlag;
                bool valid = source.GetType() == typeof(DeferredDecalComponent) &&
                    source.RenderInfo.PreCollectCommandsCallback is null && source.RenderInfo.CullingIntersectionOverride is null &&
                    DeferredDecalMaterialContract.TryRead(source.Material, out _, out _);
                AdvancedGpuResourceBindingSource image = default;
                if (!valid || (flags & AdvancedDecalRecord.UnsupportedAuthoredFlag) != 0 ||
                    !AdvancedGpuResourceSourceEncoder.TryEncode(source.Material.Textures.Count > 4 ? source.Material.Textures[4] : null,
                        EAdvancedResourceFallback.Zero, out image, out _, out _))
                    flags |= AdvancedDecalRecord.UnsupportedAuthoredFlag;
                Matrix4x4 world = source.Transform.RenderMatrix;
                Vector3 extents = source.HalfExtents;
                if (!Matrix4x4.Invert(world, out Matrix4x4 inverse) || !Finite(inverse) ||
                    world.M14 != 0 || world.M24 != 0 || world.M34 != 0 || world.M44 != 1 ||
                    !float.IsFinite(extents.X) || !float.IsFinite(extents.Y) || !float.IsFinite(extents.Z) ||
                    extents.X <= 0 || extents.Y <= 0 || extents.Z <= 0)
                    flags |= AdvancedDecalRecord.UnsupportedAuthoredFlag;
                Matrix4x4 previous = inverse;
                if (_previous.TryGetValue(source, out var last))
                    previous = last.Frame == frameId ? last.Previous : last.Frame + 1 == frameId ? last.Current : inverse;
                _previous[source] = (frameId, inverse, previous);
                buffer.Storage[buffer.Count++] = new(source, new AdvancedDecalRecord
                {
                    Material = new(source.RenderCommandDecal.StableQueryKey, 0),
                    Flags = flags,
                    WorldToDecal = inverse,
                    PreviousWorldToDecal = previous,
                    HalfExtentsAndFade = new(extents, 0),
                }, (flags & AdvancedDecalRecord.UnsupportedAuthoredFlag) == 0 ? image : default);
            }
            return lease;
        }
        catch { lease.Release(); throw; }
    }

    private static bool Finite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
