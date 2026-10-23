using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>One complete owner-built canvas state. Readers retain this object while using it.</summary>
public sealed class UIPublication
{
    private readonly UIHitRecord[] _hits;
    private readonly UIRenderRecord[] _renders = [];
    private readonly UIScrollRecord[] _scrollReceivers;
    private readonly UIFocusScopeRecord[] _focusScopes;
    private UILayoutNodeOutput[]? _flatLayout;
    private int _retainedPayloadCount;
    private int _retainedTextureCount;
    private int _retainedAtlasCount;
    private int _claims = 1;
    private int _resourcesRetired;
    private readonly Dictionary<long, UIRenderRecord> _renderByMembership;
    private UIPublishedWorldMeshCommand[]? _worldCommands;
    private readonly object _worldCommandGate = new();

    internal UIPublication(ulong generation, ulong canvasLifetime, ulong layoutGeneration, ulong hitGeneration,
        long changeRevision, ReadOnlyMemory<UILayoutNodeOutput> layout,
        ReadOnlyMemory<UIElementIdentity> identities, UIHitRecord[] hits, UIRenderRecord[] renders,
        BoundingRectangleF canvasBounds, Matrix4x4 canvasToOutput, Matrix4x4 outputToCanvas,
        UIPublication? previous = null,
        UIScrollRecord[]? scrollReceivers = null,
        UIFocusScopeRecord[]? focusScopes = null,
        UILayoutOutputPageView? layoutPages = null,
        Matrix4x4? authoringCanvasWorldMatrix = null,
        ECanvasDrawSpace authoringDrawSpace = ECanvasDrawSpace.Screen,
        bool authoringOffscreen = false)
    {
        Generation = generation;
        CanvasLifetime = canvasLifetime;
        LayoutGeneration = layoutGeneration;
        HitGeneration = hitGeneration;
        ChangeRevision = changeRevision;
        _flatLayout = layout.IsEmpty ? null : layout.ToArray();
        LayoutPages = layoutPages;
        Identities = identities;
        _hits = hits;
        _renders = renders;
        _scrollReceivers = scrollReceivers ?? [];
        _focusScopes = focusScopes ?? [];
        UIElementIdentity activeScope = identities.Length > 0 ? identities.Span[0] : default;
        int painterOrder = int.MinValue;
        for (int index = 0; index < _focusScopes.Length; index++)
            if (_focusScopes[index].PainterOrder >= painterOrder)
            {
                activeScope = _focusScopes[index].Identity;
                painterOrder = _focusScopes[index].PainterOrder;
            }
        ActiveFocusScopeIdentity = activeScope;
        if (previous is not null && ReferenceEquals(renders, previous._renders))
            _renderByMembership = previous._renderByMembership;
        else
        {
            _renderByMembership = new Dictionary<long, UIRenderRecord>(renders.Length);
            for (int index = 0; index < renders.Length; index++)
                _renderByMembership.Add(renders[index].MembershipId, renders[index]);
        }
        try { RetainDrawPayloads(); }
        catch
        {
            RetireDrawPayloads();
            throw;
        }
        CanvasBounds = canvasBounds;
        AuthoringCanvasWorldMatrix = authoringCanvasWorldMatrix ?? Matrix4x4.Identity;
        AuthoringDrawSpace = authoringDrawSpace;
        AuthoringOffscreen = authoringOffscreen;
        CanvasToOutput = canvasToOutput;
        OutputToCanvas = outputToCanvas;
        DrawSpace = ECanvasDrawSpace.Screen;
        OutputRegion = canvasBounds;
        WorldToCanvas = Matrix4x4.Identity;
        ClipToWorld = Matrix4x4.Identity;
    }

    public ulong Generation { get; }
    public ulong CanvasLifetime { get; }
    public ulong LayoutGeneration { get; }
    public ulong HitGeneration { get; }
    public long ChangeRevision { get; }
    public ReadOnlyMemory<UILayoutNodeOutput> Layout
        => _flatLayout ??= LayoutPages?.ToArray() ?? [];
    public UILayoutOutputPageView? LayoutPages { get; }
    public ReadOnlyMemory<UIElementIdentity> Identities { get; }
    public ReadOnlySpan<UIHitRecord> Hits => _hits;
    public ReadOnlySpan<UIRenderRecord> Renders => _renders;
    public ReadOnlySpan<UIScrollRecord> ScrollReceivers => _scrollReceivers;
    public ReadOnlySpan<UIFocusScopeRecord> FocusScopes => _focusScopes;
    public UIElementIdentity ActiveFocusScopeIdentity { get; }
    internal UIHitRecord[] HitStorage => _hits;
    internal UIRenderRecord[] RenderStorage => _renders;
    internal UIScrollRecord[] ScrollStorage => _scrollReceivers;
    internal UIFocusScopeRecord[] FocusScopeStorage => _focusScopes;
    internal ReadOnlySpan<UIPublishedWorldMeshCommand> WorldCommands
        => _worldCommands ?? [];

    internal void BuildWorldCommands(XREngine.Components.UICanvasComponent canvas)
    {
        if (AuthoringDrawSpace == ECanvasDrawSpace.Screen || AuthoringOffscreen)
            return;
        lock (_worldCommandGate)
        {
            if (_worldCommands is not null)
                return;
            List<UIPublishedWorldMeshCommand> commands = [];
            for (int index = 0; index < _renders.Length; index++)
            {
                UIRenderRecord record = _renders[index];
                if (!record.Visible)
                    continue;
                RenderCommandMesh3D template = record.FrozenWorldCommand ??
                    throw new InvalidOperationException("A visible direct-world UI draw has no frozen command.");
                commands.Add(new UIPublishedWorldMeshCommand(canvas, this, template,
                    record.WorldCastsShadows));
            }
            _worldCommands = commands.ToArray();
        }
    }
    internal bool ResourcesRetired => Volatile.Read(ref _resourcesRetired) != 0;
    public BoundingRectangleF CanvasBounds { get; }
    /// <summary>The owner-captured canvas pose used by world draw authoring.</summary>
    public Matrix4x4 AuthoringCanvasWorldMatrix { get; }
    public ECanvasDrawSpace AuthoringDrawSpace { get; }
    public bool AuthoringOffscreen { get; }
    public Matrix4x4 CanvasToOutput { get; }
    public Matrix4x4 OutputToCanvas { get; }
    public ECanvasDrawSpace DrawSpace { get; }
    public BoundingRectangleF OutputRegion { get; }
    public Matrix4x4 WorldToCanvas { get; }
    public Matrix4x4 ClipToWorld { get; }
    public float NearClipZ { get; }
    public float FarClipZ { get; }

    public bool TryGetRenderRecord(long membershipId, out UIRenderRecord record)
        => _renderByMembership.TryGetValue(membershipId, out record);

    internal UIPublication WithOutputMapping(Matrix4x4 canvasToOutput, Matrix4x4 outputToCanvas,
        ECanvasDrawSpace drawSpace, BoundingRectangleF outputRegion, Matrix4x4 worldToCanvas,
        Matrix4x4 clipToWorld, float nearClipZ, float farClipZ)
        => new(this, canvasToOutput, outputToCanvas, drawSpace, outputRegion,
            worldToCanvas, clipToWorld, nearClipZ, farClipZ);

    private UIPublication(UIPublication source, Matrix4x4 canvasToOutput, Matrix4x4 outputToCanvas,
        ECanvasDrawSpace drawSpace, BoundingRectangleF outputRegion, Matrix4x4 worldToCanvas,
        Matrix4x4 clipToWorld, float nearClipZ, float farClipZ)
    {
        Generation = source.Generation;
        CanvasLifetime = source.CanvasLifetime;
        LayoutGeneration = source.LayoutGeneration;
        HitGeneration = source.HitGeneration;
        ChangeRevision = source.ChangeRevision;
        _flatLayout = source._flatLayout;
        LayoutPages = source.LayoutPages;
        Identities = source.Identities;
        _hits = source._hits;
        _renders = source._renders;
        _scrollReceivers = source._scrollReceivers;
        _focusScopes = source._focusScopes;
        ActiveFocusScopeIdentity = source.ActiveFocusScopeIdentity;
        _renderByMembership = source._renderByMembership;
        try { RetainDrawPayloads(); }
        catch
        {
            RetireDrawPayloads();
            throw;
        }
        CanvasBounds = source.CanvasBounds;
        AuthoringCanvasWorldMatrix = source.AuthoringCanvasWorldMatrix;
        AuthoringDrawSpace = source.AuthoringDrawSpace;
        AuthoringOffscreen = source.AuthoringOffscreen;
        CanvasToOutput = canvasToOutput;
        OutputToCanvas = outputToCanvas;
        DrawSpace = drawSpace;
        OutputRegion = outputRegion;
        WorldToCanvas = worldToCanvas;
        ClipToWorld = clipToWorld;
        NearClipZ = nearClipZ;
        FarClipZ = farClipZ;
    }

    private void RetainDrawPayloads()
    {
        for (int index = 0; index < _renders.Length; index++)
        {
            _renders[index].Payload?.Retain();
            _retainedPayloadCount++;
            _renders[index].TextureSnapshot?.Retain();
            _retainedTextureCount++;
            _renders[index].AtlasSnapshot?.Retain();
            _retainedAtlasCount++;
        }
    }

    /// <summary>Keep draw resources while a holder or reader uses this packet.</summary>
    public void Retain()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _claims);
            ObjectDisposedException.ThrowIf(current == 0, this);
        } while (Interlocked.CompareExchange(ref _claims, checked(current + 1), current) != current);
    }

    /// <summary>End one packet claim after its reader or GPU artifact retires.</summary>
    public void Release()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _claims);
            if (current == 0)
                throw new InvalidOperationException("A UI publication claim was released twice.");
        } while (Interlocked.CompareExchange(ref _claims, current - 1, current) != current);
        if (current != 1)
            return;
        RetireDrawPayloads();
        GC.SuppressFinalize(this);
    }

    ~UIPublication() => RetireDrawPayloads();

    private void RetireDrawPayloads()
    {
        if (Interlocked.Exchange(ref _resourcesRetired, 1) != 0)
            return;
        for (int index = 0; index < _renders.Length; index++)
        {
            try
            {
                if (index < _retainedPayloadCount)
                    _renders[index].Payload?.Release();
            }
            catch (Exception)
            {
                // A terminal payload claim must not stop finalization of other claims.
            }
            try
            {
                if (index < _retainedTextureCount)
                    _renders[index].TextureSnapshot?.Release();
            }
            catch (Exception)
            {
                // Keep retiring independent image claims.
            }
            try
            {
                if (index < _retainedAtlasCount)
                    _renders[index].AtlasSnapshot?.Release();
            }
            catch (Exception)
            {
                // Keep retiring independent image claims.
            }
        }
    }

    /// <summary>Maps a raw top-left window pointer to the displayed canvas plane.</summary>
    public bool TryMapPointer(Vector2 raw, out Vector2 canvasPoint, bool requireInsideOutput = true)
    {
        canvasPoint = default;
        if (!float.IsFinite(raw.X) || !float.IsFinite(raw.Y))
            return false;
        if (OutputRegion.Width <= 0 || OutputRegion.Height <= 0 ||
            requireInsideOutput &&
            (raw.X < OutputRegion.MinX || raw.X >= OutputRegion.MinX + OutputRegion.Width ||
             raw.Y < OutputRegion.MinY || raw.Y >= OutputRegion.MinY + OutputRegion.Height))
            return false;
        if (DrawSpace == ECanvasDrawSpace.Screen)
        {
            canvasPoint = Vector2.Transform(raw, OutputToCanvas);
            return float.IsFinite(canvasPoint.X) && float.IsFinite(canvasPoint.Y);
        }
        float x = 2 * (raw.X - OutputRegion.MinX) / OutputRegion.Width - 1;
        float y = 1 - 2 * (raw.Y - OutputRegion.MinY) / OutputRegion.Height;
        Vector4 near = Vector4.Transform(new Vector4(x, y, NearClipZ, 1), ClipToWorld);
        Vector4 far = Vector4.Transform(new Vector4(x, y, FarClipZ, 1), ClipToWorld);
        if (!float.IsFinite(near.W) || !float.IsFinite(far.W) || near.W == 0 || far.W == 0)
            return false;
        Vector3 nearCanvas = Vector3.Transform(new Vector3(near.X, near.Y, near.Z) / near.W,
            WorldToCanvas);
        Vector3 farCanvas = Vector3.Transform(new Vector3(far.X, far.Y, far.Z) / far.W,
            WorldToCanvas);
        float deltaZ = farCanvas.Z - nearCanvas.Z;
        if (!float.IsFinite(deltaZ) || MathF.Abs(deltaZ) < 1e-7f)
            return false;
        float t = -nearCanvas.Z / deltaZ;
        if (!float.IsFinite(t) || t < 0 || t > 1)
            return false;
        Vector3 hit = Vector3.Lerp(nearCanvas, farCanvas, t);
        canvasPoint = new Vector2(hit.X, hit.Y);
        return float.IsFinite(canvasPoint.X) && float.IsFinite(canvasPoint.Y);
    }
}
