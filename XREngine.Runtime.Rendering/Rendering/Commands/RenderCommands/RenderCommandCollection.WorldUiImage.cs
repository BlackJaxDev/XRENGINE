using System.Collections.Concurrent;
using XREngine.Components;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Commands;

public sealed partial class RenderCommandCollection
{
    private UIWorldCollectPacketSet? _updatingWorldUiPackets;
    private UIWorldCollectPacketSet? _renderingWorldUiPackets;
    private IRuntimeWorldContext? _updatingWorldUiWorld;
    private IRuntimeWorldContext? _renderingWorldUiWorld;
    private long _updatingWorldUiGeneration;
    private long _renderingWorldUiGeneration;
    private bool _updatingWorldUiConflict;
    private bool _renderingWorldUiConflict;
    private UIPublication? _updatingWorldUiReplacement;
    private UIPublication? _renderingWorldUiReplacement;
    private long _updatingWorldUiPackageGeneration;
    private long _renderingWorldUiPackageGeneration;
    private readonly ConcurrentQueue<UIWorldCollectPacketSet> _retiredWorldUiPackets = new();

    internal bool HasRenderingWorldUiReplacement
    {
        get
        {
            using (_lock.EnterScope())
                return _renderingWorldUiReplacement is not null;
        }
    }

    internal bool HasAnyWorldUiReplacement
    {
        get
        {
            using (_lock.EnterScope())
                return _updatingWorldUiReplacement is not null ||
                    _renderingWorldUiReplacement is not null;
        }
    }

    /// <summary>Forgets a selected image when its owning renderer retires.</summary>
    internal bool InvalidateWorldUiReplacementAfterRendererRetirement()
    {
        if (!HasAnyWorldUiReplacement)
            return false;
        ResetUnsubmittedCapture();
        return true;
    }

    /// <summary>Fixes one packet selection to the CPU command image before traversal.</summary>
    internal void ObserveWorldUiPacketSet(IRuntimeWorldContext world,
        UIWorldCollectPacketSet? selected)
    {
        using (_lock.EnterScope())
        {
            if (_updatingWorldUiConflict)
                return;
            if (selected is null || !selected.IsSealed || selected.Failed)
            {
                _updatingWorldUiConflict = true;
                return;
            }
            long generation = selected.Generation;
            if (_updatingWorldUiPackets is { } current)
            {
                if (!ReferenceEquals(current, selected) ||
                    _updatingWorldUiGeneration != generation ||
                    !ReferenceEquals(_updatingWorldUiWorld, world))
                    _updatingWorldUiConflict = true;
                return;
            }
            if (!selected.TryRetain(generation))
            {
                _updatingWorldUiConflict = true;
                return;
            }
            _updatingWorldUiPackets = selected;
            _updatingWorldUiWorld = world;
            _updatingWorldUiGeneration = generation;
        }
    }

    internal void ObserveUnsupportedWorldUiTraversal()
    {
        using (_lock.EnterScope())
            _updatingWorldUiConflict = true;
    }

    internal bool TryRetainUpdatingWorldUiPackets(IRuntimeWorldContext world,
        out UIWorldCollectPacketSet? selected)
    {
        using (_lock.EnterScope())
        {
            selected = _updatingWorldUiPackets;
            if (_updatingWorldUiConflict || selected is null ||
                !ReferenceEquals(_updatingWorldUiWorld, world) ||
                !selected.TryRetain(_updatingWorldUiGeneration))
            {
                selected = null;
                return false;
            }
            return true;
        }
    }

    /// <summary>Replaces one complete canvas in a private CPU command image.</summary>
    internal bool TryReplaceDirectWorldCanvas(IRuntimeWorldContext world,
        IRuntimeRenderCamera? camera)
    {
        if (!TryRetainUpdatingWorldUiPackets(world, out UIWorldCollectPacketSet? selected) ||
            selected is null)
            return false;
        try
        {
            if (selected.Count != 1 ||
                !selected.TryGetAt(0, out object? key, out UIPublication? packet) ||
                key is not UICanvasComponent canvas || packet is null ||
                !canvas.MatchesDirectWorldChildren(packet, world,
                    packet.WorldCollectionRevision))
                return false;
            ReadOnlySpan<UIPublishedWorldMeshCommand> frozen = packet.WorldCommands;
            if (frozen.Length == 0 || frozen.Length > 128)
                return false;
            for (int index = 0; index < frozen.Length; index++)
            {
                UIPublishedWorldMeshCommand draw = frozen[index];
                RenderCommandMesh3D? source = draw.SourceWorldCommand;
                if (source is null || draw.RenderPass != (int)EDefaultRenderPass.TransparentForward ||
                    source.RenderPass != draw.RenderPass ||
                    !source.Enabled || source.Instances == 0)
                    return false;
                draw.CollectedForRender(camera);
            }
            using (_lock.EnterScope())
            {
                if (_updatingWorldUiConflict || _updatingWorldUiReplacement is not null ||
                    !ReferenceEquals(_updatingWorldUiPackets, selected) ||
                    _updatingWorldUiGeneration != selected.Generation ||
                    !ReferenceEquals(_updatingWorldUiWorld, world) ||
                    !_updatingPasses.TryGetValue((int)EDefaultRenderPass.TransparentForward,
                        out ICollection<RenderCommand>? pass) ||
                    pass is not SnapshotSortedRenderCommandCollection sorted ||
                    !sorted.ContainsEveryFrozenWorldSource(frozen))
                    return false;
                int matches = 0;
                int total = 0;
                foreach (ICollection<RenderCommand> candidatePass in _updatingPasses.Values)
                    for (int commandIndex = 0; commandIndex < candidatePass.Count; commandIndex++)
                    {
                        total++;
                        RenderCommand command = GetCommandAt(candidatePass, commandIndex);
                        for (int index = 0; index < frozen.Length; index++)
                            if (ReferenceEquals(command, frozen[index].SourceWorldCommand))
                                matches++;
                    }
                if (matches != frozen.Length || total != frozen.Length ||
                    !canvas.MatchesDirectWorldChildren(packet, world,
                        packet.WorldCollectionRevision))
                    return false;

                _updatingSwapQueue.EnsureCapacity(_updatingSwapQueue.Count + frozen.Length);
                _updatingSwapQueueMembership.EnsureCapacity(
                    _updatingSwapQueueMembership.Count + frozen.Length);
                _updatingUiPublicationCommands.EnsureCapacity(
                    _updatingUiPublicationCommands.Count + 1);
                packet.Retain();
                if (!_updatingUiPublicationCommands.Add(packet))
                    packet.Release();
                sorted.ReplaceFrozenWorldSources(frozen);
                for (int index = 0; index < frozen.Length; index++)
                {
                    UIPublishedWorldMeshCommand draw = frozen[index];
                    if (IsRenderCommandSnapshotAuthority)
                        draw._authoritativePublishQueued = true;
                    if (_updatingSwapQueueMembership.Add(draw))
                        _updatingSwapQueue.Add(draw);
                }
                InvalidateMeshOrderNoLock("World UI replacement changed the collected command identities.");
                _updatingWorldUiReplacement = packet;
                _updatingRevision++;
                return true;
            }
        }
        finally { selected.Release(); }
    }

    internal bool TryRetainRenderingWorldUiImage(IRuntimeWorldContext world,
        long packageGeneration, out UIWorldCollectPacketSet? packets,
        out UIPublication? replacement)
    {
        using (_lock.EnterScope())
        {
            packets = _renderingWorldUiPackets;
            replacement = _renderingWorldUiReplacement;
            if (_renderingWorldUiConflict || packets is null || replacement is null ||
                !ReferenceEquals(_renderingWorldUiWorld, world) ||
                packageGeneration == 0 ||
                _renderingWorldUiPackageGeneration != packageGeneration ||
                _renderingBackendReadyPackage.PackageGeneration != packageGeneration ||
                !packets.TryRetain(_renderingWorldUiGeneration))
            {
                packets = null;
                replacement = null;
                return false;
            }
            return true;
        }
    }

    /// <summary>Rejects a changed source after package preparation reads the image.</summary>
    internal void SealUpdatingWorldUiReplacement(IRuntimeWorldContext world)
    {
        using (_lock.EnterScope())
        {
            if (_updatingWorldUiReplacement is not { } packet)
                return;
            if (_updatingWorldUiConflict || _updatingWorldUiPackets is not { } packets ||
                !ReferenceEquals(_updatingWorldUiWorld, world) ||
                packets.Generation != _updatingWorldUiGeneration ||
                packets.Count != 1 ||
                !packets.TryGetAt(0, out object? key, out UIPublication? selected) ||
                key is not UICanvasComponent canvas ||
                !ReferenceEquals(selected, packet) ||
                !canvas.MatchesDirectWorldChildren(packet, world,
                    packet.WorldCollectionRevision) ||
                _updatingWorldUiPackageGeneration <= 0 ||
                _updatingBackendReadyPackage.PackageGeneration !=
                    _updatingWorldUiPackageGeneration)
                _updatingWorldUiConflict = true;
        }
    }

    private void RetireWorldUiImagesNoLock()
    {
        if (_updatingWorldUiPackets is { } updating)
            _retiredWorldUiPackets.Enqueue(updating);
        if (_renderingWorldUiPackets is { } rendering)
            _retiredWorldUiPackets.Enqueue(rendering);
        _updatingWorldUiPackets = _renderingWorldUiPackets = null;
        _updatingWorldUiWorld = _renderingWorldUiWorld = null;
        _updatingWorldUiGeneration = _renderingWorldUiGeneration = 0;
        _updatingWorldUiConflict = _renderingWorldUiConflict = false;
        _updatingWorldUiReplacement = _renderingWorldUiReplacement = null;
        _updatingWorldUiPackageGeneration = _renderingWorldUiPackageGeneration = 0;
    }

    private void SwapWorldUiImagesNoLock()
    {
        if (_renderingWorldUiPackets is { } old)
            _retiredWorldUiPackets.Enqueue(old);
        _renderingWorldUiPackets = _updatingWorldUiPackets;
        _renderingWorldUiWorld = _updatingWorldUiWorld;
        _renderingWorldUiGeneration = _updatingWorldUiGeneration;
        _renderingWorldUiConflict = _updatingWorldUiConflict;
        _renderingWorldUiReplacement = _updatingWorldUiReplacement;
        _renderingWorldUiPackageGeneration = _updatingWorldUiPackageGeneration;
        _updatingWorldUiPackets = null;
        _updatingWorldUiWorld = null;
        _updatingWorldUiGeneration = 0;
        _updatingWorldUiConflict = false;
        _updatingWorldUiReplacement = null;
        _updatingWorldUiPackageGeneration = 0;
    }

    private void DrainRetiredWorldUiImages()
    {
        List<Exception>? failures = null;
        while (_retiredWorldUiPackets.TryDequeue(out UIWorldCollectPacketSet? packets))
            try { packets.Release(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null)
            throw new AggregateException("World UI image claims could not retire.", failures);
    }
}
