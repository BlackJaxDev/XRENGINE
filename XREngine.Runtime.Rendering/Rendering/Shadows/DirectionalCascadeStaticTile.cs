using System.Numerics;
using System.Threading;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Shadows;

/// <summary>
/// Depth tile that holds the static shadow casters of one directional cascade of one light
/// (desktop source). The Advanced directional shadow lane draws the static casters into this
/// tile, copies it into the cascade tile of the atlas page, and draws the dynamic casters over
/// the copy. While the key of the tile matches the cascade, a refresh only copies the tile and
/// draws the dynamic casters. The tile is never published to receivers. It has the format of
/// the atlas page depth texture and the size of the cascade's inner tile rectangle.
/// The shadow atlas manager creates and owns the tile on the render thread. The backend lane
/// writes the key when it records a static draw.
/// </summary>
public sealed class DirectionalCascadeStaticTile
{
    private long _recordedRenderFrameId;
    private int _hasContent;

    internal DirectionalCascadeStaticTile(Guid lightId, int cascadeIndex, uint width, uint height)
    {
        LightId = lightId;
        CascadeIndex = cascadeIndex;
        Width = width;
        Height = height;
        DepthTexture = new XRTexture2DArray(
            1u,
            width,
            height,
            EPixelInternalFormat.DepthComponent24,
            EPixelFormat.DepthComponent,
            EPixelType.UnsignedInt,
            allocateData: false)
        {
            SamplerName = "ShadowAtlasStaticCascadeDepth",
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
            MinFilter = ETexMinFilter.Nearest,
            MagFilter = ETexMagFilter.Nearest,
            FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
            LargestMipmapLevel = 0,
            SmallestAllowedMipmapLevel = 0,
            AutoGenerateMipmaps = false,
        };
        FrameBuffer = new XRFrameBuffer((DepthTexture, EFrameBufferAttachment.DepthAttachment, 0, 0))
        {
            Name = "ShadowAtlas_StaticCascadeTileFBO",
        };
    }

    /// <summary>Light that owns the cascade.</summary>
    public Guid LightId { get; }
    /// <summary>Cascade index (0 is the nearest cascade).</summary>
    public int CascadeIndex { get; }
    /// <summary>Tile width in texels (the inner rectangle width of the cascade).</summary>
    public uint Width { get; }
    /// <summary>Tile height in texels.</summary>
    public uint Height { get; }
    /// <summary>One-layer depth texture with the format of the atlas page depth texture.</summary>
    public XRTexture2DArray DepthTexture { get; }
    /// <summary>Depth-only framebuffer of <see cref="DepthTexture"/>.</summary>
    public XRFrameBuffer FrameBuffer { get; }

    // Key of the last recorded static draw. The lane writes these fields together when it
    // records a static draw, and compares them before it copies the tile without a draw.
    /// <summary>Cascade world-to-clip matrix that the static casters were drawn with.</summary>
    public Matrix4x4 RecordedViewProjection { get; private set; }
    /// <summary>Inner tile rectangle of the cascade on the atlas page at the static draw.</summary>
    public BoundingRectangle RecordedTileRect { get; private set; }
    /// <summary>Atlas page framebuffer that the tile was drawn for.</summary>
    public XRFrameBuffer? RecordedPage { get; private set; }
    /// <summary>Static caster epoch of the cascade at the static draw.</summary>
    public ulong RecordedStaticEpoch { get; private set; }
    /// <summary>64-bit order-independent hash of the static caster records of the cascade at the static draw.</summary>
    public ulong RecordedRecordSetHash { get; private set; }
    /// <summary>Number of static caster records of the cascade at the static draw.</summary>
    public int RecordedRecordCount { get; private set; }

    /// <summary>Render frame of the last recorded static draw; 0 when none.</summary>
    public ulong RecordedRenderFrameId => unchecked((ulong)Interlocked.Read(ref _recordedRenderFrameId));

    /// <summary>True when a recorded static draw exists and no failed submission invalidated it.</summary>
    public bool HasContent => Volatile.Read(ref _hasContent) != 0;

    /// <summary>
    /// Stores the key of a static draw that the lane recorded in <paramref name="renderFrameId"/>.
    /// The lane calls it only for a group that it records, so a declined group keeps the key
    /// of the tile content.
    /// </summary>
    public void RecordStaticDraw(
        ulong renderFrameId,
        in Matrix4x4 viewProjection,
        in BoundingRectangle tileRect,
        XRFrameBuffer page,
        ulong staticEpoch,
        ulong recordSetHash,
        int recordCount)
    {
        RecordedViewProjection = viewProjection;
        RecordedTileRect = tileRect;
        RecordedPage = page;
        RecordedStaticEpoch = staticEpoch;
        RecordedRecordSetHash = recordSetHash;
        RecordedRecordCount = recordCount;
        Interlocked.Exchange(ref _recordedRenderFrameId, unchecked((long)renderFrameId));
        Volatile.Write(ref _hasContent, 1);
    }

    /// <summary>
    /// Marks the content as unusable, so the next refresh draws the static casters again.
    /// The atlas manager calls it when a submission that held the cascade failed.
    /// </summary>
    public void Invalidate()
        => Volatile.Write(ref _hasContent, 0);

    internal void Destroy()
    {
        FrameBuffer.Destroy();
        DepthTexture.Destroy();
    }
}
