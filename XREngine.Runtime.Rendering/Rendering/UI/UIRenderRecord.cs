using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Frozen per-component draw values captured by the canvas owner.</summary>
public readonly record struct UIRenderRecord
{
    public required RenderInfo2D RenderInfo { get; init; }
    public required long MembershipId { get; init; }
    public required UIElementIdentity Identity { get; init; }
    public required Matrix4x4 WorldMatrix { get; init; }
    public required Vector4 Bounds { get; init; }
    public BoundingRectangle? Crop { get; init; }
    public required int RenderPass { get; init; }
    public required int ZIndex { get; init; }
    public required XRMaterial? Material { get; init; }
    public bool Visible { get; init; }
    public bool SupportsBatch { get; init; }
    public bool IsText { get; init; }
    public Vector4 QuadColor { get; init; }
    public XRTexture2D? Texture { get; init; }
    public Vector4 UV { get; init; }
    public bool ImageSrgb { get; init; }
    public bool IsBorder { get; init; }
    public Vector4 BorderColor { get; init; }
    public float BorderWidth { get; init; }
    public Vector4 TextColor { get; init; }
    public Vector4 OutlineColor { get; init; }
    public float OutlineThickness { get; init; }
    public XRTexture2D? Atlas { get; init; }
    public int AtlasType { get; init; }
    public float DistanceRange { get; init; }
    public float DistanceRangeMiddle { get; init; }
    public float MsdfFillBias { get; init; }
    public int DebugMode { get; init; }
    public UITextLayoutResult? TextLayout { get; init; }
    public RenderCommandMesh2D? FrozenCommand { get; init; }
    public RenderCommandMesh3D? FrozenWorldCommand { get; init; }
    public bool WorldCastsShadows { get; init; }
    public int WorldLayer { get; init; }
    public Box? WorldCullingVolume { get; init; }
    public UIDrawPayload? Payload { get; init; }
    public string? CaptureFailure { get; init; }
    internal UITextureSnapshot? TextureSnapshot { get; init; }
    internal UITextureSnapshot? AtlasSnapshot { get; init; }
}
