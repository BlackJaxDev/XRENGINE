using System.Numerics;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace BrowserUiParity;

/// <summary>Creates a tiny RGBA image through the shared texture and UI material factories.</summary>
[RequireComponents(typeof(UIMaterialComponent))]
public sealed class BrowserUiParityImageComponent : XRComponent
{
    private UIMaterialComponent? _quad;
    private XRMaterial? _previousMaterial;
    private XRMaterial? _imageMaterial;
    private XRTexture2D? _image;

    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        if (_image is not null)
            return;
        SetField(ref _quad, GetSiblingComponent<UIMaterialComponent>()
            ?? throw new InvalidOperationException("The saved image requires its UI quad."));
        SetField(ref _previousMaterial, _quad!.Material
            ?? throw new InvalidOperationException("The image quad has no default material."));

        // Row zero is sampled at v=0. No encoded image decoder or cooked payload is involved.
        ReadOnlySpan<byte> rgba = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 0, 128];
        SetField(ref _image, new XRTexture2D(2, 2, rgba)
        {
            Name = "UI RGBA Orientation Image",
            SizedInternalFormat = ESizedInternalFormat.Rgba8,
            ImportedColorSpace = ETextureColorSpace.Srgb,
            AutoGenerateMipmaps = false,
            MinFilter = ETexMinFilter.Nearest,
            MagFilter = ETexMagFilter.Nearest,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
            MaxAnisotropy = 1,
            MinLOD = 0,
            MaxLOD = 0,
        });
        SetField(ref _imageMaterial, UIMaterialComponent.CreateImageMaterial(_image!, Vector4.One));
        _quad!.SetQuadMaterial(_imageMaterial!);
    }

    protected override void OnEndPlay()
    {
        ReleaseImage();
        base.OnEndPlay();
    }

    protected override void OnDestroying()
    {
        try { ReleaseImage(); }
        finally { base.OnDestroying(); }
    }

    private void ReleaseImage()
    {
        if (_quad is { IsDestroyed: false } && _previousMaterial is { IsDestroyed: false })
            _quad.SetQuadMaterial(_previousMaterial);
        _imageMaterial?.Destroy(true);
        if (_imageMaterial is { IsDestroyed: false })
            throw new InvalidOperationException("UI image material retirement was vetoed.");
        SetField(ref _imageMaterial, null);
        if (_image is { } image)
        {
            image.Destroy(true);
            if (!image.IsDestroyed)
                throw new InvalidOperationException("UI image texture retirement was vetoed.");
            foreach (Mipmap2D mip in image.Mipmaps)
                mip.Data?.Dispose();
            SetField(ref _image, null);
        }
        SetField(ref _quad, null);
        SetField(ref _previousMaterial, null);
    }
}
