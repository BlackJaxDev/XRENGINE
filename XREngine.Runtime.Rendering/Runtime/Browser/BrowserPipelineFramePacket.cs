using System.Buffers.Binary;
using System.Numerics;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering;

/// <summary>Bounded reusable focused-pipeline wire arena, borrowed only during synchronous consumption.</summary>
public sealed class BrowserPipelineFramePacket : IDisposable
{
    public const int HeaderBytes = 256;
    public const int DrawBytes = 304;
    public const int UiBytes = 80;
    public const int MaximumDraws = 4096;
    public const int MaximumUiQuads = 4096;
    private byte[] _bytes;
    private BrowserFramePacketState _state;
    private bool _disposed;
    private int _width;
    private int _height;
    private int _uiCount;

    public BrowserPipelineFramePacket(int initialDrawCapacity = 64, int initialUiCapacity = 32)
    {
        if (initialDrawCapacity is < 1 or > MaximumDraws || initialUiCapacity is < 1 or > MaximumUiQuads)
            throw new ArgumentOutOfRangeException(nameof(initialDrawCapacity));
        DrawCapacity = initialDrawCapacity;
        UiCapacity = initialUiCapacity;
        _bytes = new byte[HeaderBytes + DrawCapacity * DrawBytes + UiCapacity * UiBytes];
    }

    public int DrawCapacity { get; private set; }
    public int UiCapacity { get; private set; }
    public int DrawCount { get; private set; }
    public int UiCount => _uiCount;
    public int ArenaGeneration { get; private set; } = 1;
    public int GrowthCount { get; private set; }
    public uint FrameSequence { get; private set; }
    public int ByteLength => HeaderBytes + DrawCount * DrawBytes + UiCount * UiBytes;
    public ReadOnlySpan<byte> WrittenBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is not (BrowserFramePacketState.Sealed or BrowserFramePacketState.Consuming))
                throw new InvalidOperationException("Packet must be sealed.");
            return _bytes.AsSpan(0, ByteLength);
        }
    }

    public void EnsureCapacity(int draws, int ui)
    {
        Require(BrowserFramePacketState.Idle);
        if (draws < 0 || draws > MaximumDraws || ui < 0 || ui > MaximumUiQuads)
            throw new ArgumentOutOfRangeException(nameof(draws));
        if (draws <= DrawCapacity && ui <= UiCapacity) return;
        if (ArenaGeneration == int.MaxValue || GrowthCount == int.MaxValue)
            throw new InvalidOperationException("Packet arena generation exhausted.");
        while (DrawCapacity < draws) DrawCapacity = Math.Min(DrawCapacity * 2, MaximumDraws);
        while (UiCapacity < ui) UiCapacity = Math.Min(UiCapacity * 2, MaximumUiQuads);
        _bytes = new byte[HeaderBytes + DrawCapacity * DrawBytes + UiCapacity * UiBytes];
        ArenaGeneration++;
        GrowthCount++;
    }

    public void Begin(int sessionId, int surfaceGeneration, int width, int height,
        in BrowserPipelineEnvironment environment, bool updateShadow = true)
    {
        Require(BrowserFramePacketState.Idle);
        if (sessionId <= 0 || surfaceGeneration <= 0 || width <= 0 || height <= 0 || FrameSequence == uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        if (!Finite(environment.ViewProjection) || !Finite(environment.ShadowViewProjection) ||
            !Finite(environment.LightDirection) || environment.LightDirection.LengthSquared() < 0.000001f ||
            !float.IsFinite(environment.LightIntensity) || environment.LightIntensity < 0 ||
            !Finite(environment.Ambient) || environment.Ambient.X < 0 || environment.Ambient.Y < 0 || environment.Ambient.Z < 0 ||
            !float.IsFinite(environment.Exposure) || environment.Exposure <= 0 ||
            !Color(environment.SkyTop) || !Color(environment.SkyBottom) || environment.SkyTop.W != 1 || environment.SkyBottom.W != 1)
            throw new ArgumentException("Frame matrices, directional light and linear environment colors must be finite and valid.", nameof(environment));
        _width = width; _height = height;
        DrawCount = 0; _uiCount = 0; FrameSequence++;
        Span<byte> header = _bytes.AsSpan(0, HeaderBytes);
        header.Clear();
        Write(header, 0, 0x46524558); Write(header, 4, 2);
        Write(header, 16, sessionId); Write(header, 20, surfaceGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], FrameSequence);
        Write(header, 32, width); Write(header, 36, height); Write(header, 40, updateShadow ? 1 : 0);
        BrowserShaderAbi.WriteTransform(header.Slice(64, 64), environment.ViewProjection);
        BrowserShaderAbi.WriteTransform(header.Slice(128, 64), environment.ShadowViewProjection);
        Vector3 direction = Vector3.Normalize(environment.LightDirection);
        WriteVector(header, 192, new Vector4(direction, environment.LightIntensity));
        WriteVector(header, 208, new Vector4(environment.Ambient, environment.Exposure));
        WriteVector(header, 224, environment.SkyTop); WriteVector(header, 240, environment.SkyBottom);
        _state = BrowserFramePacketState.Writing;
    }

    public void AddDraw(in BrowserPipelineDraw draw)
    {
        Require(BrowserFramePacketState.Writing);
        if (_uiCount != 0) throw new InvalidOperationException("All mesh draws must precede UI commands.");
        if (DrawCount == DrawCapacity) { _state = BrowserFramePacketState.Faulted; throw new BrowserArenaCapacityException("focused draw", DrawCount + 1, DrawCapacity, MaximumDraws); }
        if (draw.Mesh.Packed <= 0 || draw.Material.Packed <= 0 || draw.ViewportX < 0 || draw.ViewportY < 0 ||
            draw.ViewportWidth <= 0 || draw.ViewportHeight <= 0 || (long)draw.ViewportX + draw.ViewportWidth > _width ||
            (long)draw.ViewportY + draw.ViewportHeight > _height || draw.FirstIndex < 0 || draw.FirstIndex % 3 != 0 ||
            draw.IndexCount <= 0 || draw.IndexCount % 3 != 0 || (long)draw.FirstIndex + draw.IndexCount > int.MaxValue ||
            !Finite(draw.Model) || !Finite(draw.Mvp) || !Finite(draw.ViewProjection) || !float.IsFinite(draw.ViewDepth) ||
            draw.Model.M14 != 0 || draw.Model.M24 != 0 || draw.Model.M34 != 0 || draw.Model.M44 != 1 ||
            !Finite(draw.WorldBounds.BoundingSphere) || draw.WorldBounds.BoundingSphere.W < 0 ||
            !Finite(draw.WorldBounds.AabbMin) || !Finite(draw.WorldBounds.AabbMax) ||
            draw.WorldBounds.AabbMin.X > draw.WorldBounds.AabbMax.X ||
            draw.WorldBounds.AabbMin.Y > draw.WorldBounds.AabbMax.Y ||
            draw.WorldBounds.AabbMin.Z > draw.WorldBounds.AabbMax.Z ||
            (draw.Occluder && (draw.AlphaMode != "opaque" || draw.DisableCulling)) ||
            draw.AlphaMode is not ("opaque" or "masked" or "transparent") || (draw.ShadowOnly && !draw.CastShadow))
        { _state = BrowserFramePacketState.Faulted; throw new ArgumentException("Invalid focused draw; discard packet.", nameof(draw)); }
        Span<byte> bytes = _bytes.AsSpan(HeaderBytes + DrawCount * DrawBytes, DrawBytes);
        bytes.Clear();
        Write(bytes, 0, draw.Mesh.Packed); Write(bytes, 4, draw.Material.Packed);
        Write(bytes, 8, draw.ViewportX); Write(bytes, 12, draw.ViewportY); Write(bytes, 16, draw.ViewportWidth); Write(bytes, 20, draw.ViewportHeight);
        Write(bytes, 24, draw.FirstIndex); Write(bytes, 28, draw.IndexCount);
        BrowserShaderAbi.WriteTransform(bytes.Slice(32, 64), draw.Model);
        BrowserShaderAbi.WriteTransform(bytes.Slice(96, 64), draw.Mvp);
        Write(bytes, 160, (draw.CastShadow ? 1 : 0) | (draw.ShadowOnly ? 2 : 0)
            | (draw.DisableCulling ? 4 : 0) | (draw.Occluder ? 8 : 0));
        // Preserve BoundsGpu's 64-byte ABI; reserved lanes stay zero in the cleared arena.
        WriteVector(bytes, 176, draw.WorldBounds.BoundingSphere);
        WriteVector(bytes, 192, new Vector4(draw.WorldBounds.AabbMin.X, draw.WorldBounds.AabbMin.Y, draw.WorldBounds.AabbMin.Z, 0));
        WriteVector(bytes, 208, new Vector4(draw.WorldBounds.AabbMax.X, draw.WorldBounds.AabbMax.Y, draw.WorldBounds.AabbMax.Z, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[224..], draw.WorldBounds.BoundsVersion);
        BrowserShaderAbi.WriteTransform(bytes.Slice(240, 64), draw.ViewProjection);
        DrawCount++;
    }

    public void AddUi(in BrowserPipelineUiQuad quad)
    {
        Require(BrowserFramePacketState.Writing);
        if (_uiCount == UiCapacity) { _state = BrowserFramePacketState.Faulted; throw new BrowserArenaCapacityException("focused UI", UiCount + 1, UiCapacity, MaximumUiQuads); }
        if (!Rect(quad.Rectangle) || !Rect(quad.Clip) || !Normalized(quad.UV) ||
            quad.UV.X + quad.UV.Z > 1 || quad.UV.Y + quad.UV.W > 1 ||
            !Normalized(quad.Tint) || quad.Texture.Packed < 0 ||
            quad.Clip.X < 0 || quad.Clip.Y < 0 || quad.Clip.X + quad.Clip.Z > _width || quad.Clip.Y + quad.Clip.W > _height ||
            quad.Clip.X != MathF.Truncate(quad.Clip.X) || quad.Clip.Y != MathF.Truncate(quad.Clip.Y) ||
            quad.Clip.Z != MathF.Truncate(quad.Clip.Z) || quad.Clip.W != MathF.Truncate(quad.Clip.W))
        { _state = BrowserFramePacketState.Faulted; throw new ArgumentException("Invalid UI rectangle, clip, UV or tint.", nameof(quad)); }
        Span<byte> bytes = _bytes.AsSpan(HeaderBytes + DrawCount * DrawBytes + UiCount * UiBytes, UiBytes);
        bytes.Clear(); Write(bytes, 0, quad.Texture.Packed);
        WriteVector(bytes, 4, quad.Rectangle); WriteVector(bytes, 20, quad.UV);
        WriteVector(bytes, 36, quad.Tint); WriteVector(bytes, 52, quad.Clip);
        _uiCount++;
    }

    public void Seal()
    {
        Require(BrowserFramePacketState.Writing);
        Write(_bytes, 8, ByteLength); Write(_bytes, 12, DrawCount); Write(_bytes, 28, UiCount);
        _state = BrowserFramePacketState.Sealed;
    }
    public Span<byte> BeginConsume() { Require(BrowserFramePacketState.Sealed); _state = BrowserFramePacketState.Consuming; return _bytes.AsSpan(0, ByteLength); }
    public void EndConsume() { Require(BrowserFramePacketState.Consuming); _state = BrowserFramePacketState.Idle; }
    public void Abort()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state == BrowserFramePacketState.Consuming) throw new InvalidOperationException("Borrowed bytes cannot be aborted.");
        _state = BrowserFramePacketState.Idle; DrawCount = 0; _uiCount = 0;
    }
    public void Dispose() { if (_disposed) return; Require(BrowserFramePacketState.Idle); _bytes = Array.Empty<byte>(); _disposed = true; }
    private void Require(BrowserFramePacketState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != state) throw new InvalidOperationException("Invalid focused packet ownership state.");
    }
    private static void Write(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);
    private static void WriteVector(Span<byte> bytes, int offset, Vector4 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes[offset..], value.X); BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 4)..], value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 8)..], value.Z); BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 12)..], value.W);
    }
    private static bool Rect(Vector4 value) => Finite(value) && value.Z > 0 && value.W > 0;
    private static bool Color(Vector4 value) => Finite(value) && value.X >= 0 && value.Y >= 0 && value.Z >= 0 && value.W >= 0;
    private static bool Normalized(Vector4 value) => Color(value) && value.X <= 1 && value.Y <= 1 && value.Z <= 1 && value.W <= 1;
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);
    internal static bool Finite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
