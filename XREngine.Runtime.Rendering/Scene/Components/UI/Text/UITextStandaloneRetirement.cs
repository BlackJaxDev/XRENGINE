using System.Collections.Concurrent;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI;

/// <summary>Retires only the private resources of one text renderer on the render owner.</summary>
internal sealed class UITextStandaloneRetirement
{
    private static readonly ConcurrentDictionary<UITextStandaloneRetirement, byte> Pending = new();
    private static readonly ConcurrentDictionary<UITextStandaloneRetirement, byte> Quarantined = new();
    private readonly WeakReference<UITextComponent> _owner;
    private readonly XRMeshRenderer _renderer;
    private readonly XRMesh? _geometry;
    private readonly XRMaterial? _material;
    private readonly XRDataBuffer? _transforms;
    private readonly XRDataBuffer? _uvs;
    private readonly XRDataBuffer? _rotations;
    private readonly Action<Action>? _schedule;
    private int _retryCount;
    private bool _released;
    private bool _transferredToAuthor;

    internal UITextStandaloneRetirement(UITextComponent owner, XRMeshRenderer renderer,
        XRMesh? geometry, XRMaterial? material, XRDataBuffer? transforms,
        XRDataBuffer? uvs, XRDataBuffer? rotations, Action<Action>? schedule = null)
    {
        _owner = new(owner);
        _renderer = renderer;
        _geometry = geometry;
        _material = material;
        _transforms = transforms;
        _uvs = uvs;
        _rotations = rotations;
        _schedule = schedule;
    }

    internal void Queue()
    {
        Pending.TryAdd(this, 0);
        _renderer.Destroying += OnRendererDestroying;
        _renderer.Destroyed += OnRendererDestroyed;
        try
        {
            if (_schedule is not null)
                _schedule(RetireOnRenderOwner);
            else if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                RetireOnRenderOwner();
            else
                RuntimeEngine.EnqueueRenderThreadTask(RetireOnRenderOwner,
                    "UI.TextStandalone.Release", RenderThreadJobKind.RenderPipelineResource);
        }
        catch (Exception exception) { RetryOrQuarantine(exception); }
    }

    private void RetireOnRenderOwner()
    {
        try
        {
            if (_transferredToAuthor)
                return;
            if (ReferenceEquals(CurrentRenderer(), _renderer))
            {
                TransferToAuthor();
                return;
            }
            if (_renderer.IsDestroyed)
            {
                ReleaseResources();
                return;
            }
            _renderer.Destroy(true);
            // XRMeshRenderer defers destruction while a publication lease is held.
            // Keep the bundle rooted until its eventual Destroyed event releases it.
            if (!_transferredToAuthor && !_renderer.IsDestroyed)
                _renderer.Destroy();
        }
        catch (Exception exception) { RetryOrQuarantine(exception); }
    }

    private bool OnRendererDestroying(XRObjectBase destroying)
    {
        if (!ReferenceEquals(CurrentRenderer(), _renderer))
            return true;
        TransferToAuthor();
        return false;
    }

    private void TransferToAuthor()
    {
        _transferredToAuthor = true;
        _renderer.Destroying -= OnRendererDestroying;
        _renderer.Destroyed -= OnRendererDestroyed;
        Pending.TryRemove(this, out _);
        Quarantined.TryRemove(this, out _);
    }

    private void OnRendererDestroyed(XRObjectBase destroyed)
    {
        try
        {
            if (_schedule is not null)
                _schedule(ReleaseFromRendererEvent);
            else if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                ReleaseResources();
            else
                RuntimeEngine.EnqueueRenderThreadTask(ReleaseFromRendererEvent,
                    "UI.TextStandalone.ReleaseAfterRenderer", RenderThreadJobKind.RenderPipelineResource);
        }
        catch (Exception exception) { RetryOrQuarantine(exception); }
    }

    private void ReleaseFromRendererEvent()
    {
        try { ReleaseResources(); }
        catch (Exception exception) { RetryOrQuarantine(exception); }
    }

    private void ReleaseResources()
    {
        if (_released)
            return;
        DestroyIfUnborrowed(_transforms, "GlyphTransformsBuffer");
        DestroyIfUnborrowed(_uvs, "GlyphTexCoordsBuffer");
        DestroyIfUnborrowed(_rotations, "GlyphRotationsBuffer");
        if (_geometry is not null && !ReferenceEquals(CurrentRenderer()?.Mesh, _geometry))
        {
            _geometry.Destroy(true);
            if (!_geometry.IsDestroyed)
                throw new InvalidOperationException("UI.TextStandalone.GeometryRetirementPending");
        }
        if (_material is not null &&
            !ReferenceEquals(CurrentRenderer()?.Material, _material) &&
            (!_owner.TryGetTarget(out UITextComponent? owner) || !ReferenceEquals(owner.Material, _material)))
        {
            _material.Destroy(true);
            if (!_material.IsDestroyed)
                throw new InvalidOperationException("UI.TextStandalone.MaterialRetirementPending");
        }
        _released = true;
        _renderer.Destroying -= OnRendererDestroying;
        _renderer.Destroyed -= OnRendererDestroyed;
        Pending.TryRemove(this, out _);
    }

    private XRMeshRenderer? CurrentRenderer()
        => _owner.TryGetTarget(out UITextComponent? owner) ? owner.Mesh : null;

    private void DestroyIfUnborrowed(XRDataBuffer? buffer, string binding)
    {
        if (buffer is null)
            return;
        XRMeshRenderer? current = CurrentRenderer();
        if (current is not null && current.Buffers.TryGetValue(binding, out XRDataBuffer? borrowed) &&
            ReferenceEquals(borrowed, buffer))
            return;
        buffer.Destroy(true);
        if (!buffer.IsDestroyed)
            throw new InvalidOperationException($"UI.TextStandalone.BufferRetirementPending: {binding}");
    }

    private void Quarantine(Exception exception)
    {
        Quarantined.TryAdd(this, 0);
        Debug.LogWarning($"UI.TextStandalone.RetirementFailed: owned resources remain retained. {exception.Message}");
    }

    private void RetryOrQuarantine(Exception exception)
    {
        if (System.Threading.Interlocked.Increment(ref _retryCount) > 1)
        {
            Quarantine(exception);
            return;
        }
        try
        {
            if (_schedule is not null)
                _schedule(RetireOnRenderOwner);
            else if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                RetireOnRenderOwner();
            else
                RuntimeEngine.EnqueueRenderThreadTask(RetireOnRenderOwner,
                    "UI.TextStandalone.RetryRelease", RenderThreadJobKind.RenderPipelineResource);
        }
        catch (Exception retryFailure) { Quarantine(retryFailure); }
    }
}
