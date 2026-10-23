using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class UIImageAtlasTests
{
    [Test]
    public void PageOwnsInputAndExtrudesAllEdgesWithoutSharingMutablePixels()
    {
        byte[] source = [1, 2, 3, 255, 10, 20, 30, 255];
        UIImageAtlasSource image = new("pair", 2, 1, source);
        source[0] = 99;
        UIImageAtlas atlas = new([image], 4);
        atlas.GetRegion("pair").ShouldBe(new Vector4(.25f, .25f, .75f, .5f));
        byte[] pixels = atlas.CopyPixels();
        for (int row = 0; row < 3; row++)
        {
            pixels[row * 16].ShouldBe((byte)1);
            pixels[row * 16 + 4].ShouldBe((byte)1);
            pixels[row * 16 + 8].ShouldBe((byte)10);
            pixels[row * 16 + 12].ShouldBe((byte)10);
        }
        pixels[0] = 88;
        atlas.CopyPixels()[0].ShouldBe((byte)1);
        UIImageAtlas replacement = new([new("pair", 2, 1, source)], 4);
        replacement.CopyPixels()[0].ShouldBe((byte)99);
        atlas.CopyPixels()[0].ShouldBe((byte)1);
    }

    [Test]
    public void InvalidPageInputsFailBeforePublishingAPartialAtlas()
    {
        UIImageAtlasSource first = new("a", 1, 1, new byte[] { 1, 2, 3, 4 });
        Should.Throw<ArgumentException>(() => new UIImageAtlas([first, first], 8));
        Should.Throw<InvalidOperationException>(() => new UIImageAtlas([first, new("b", 1, 1, new byte[] { 1, 2, 3, 4 })], 4));
        Should.Throw<ArgumentException>(() => new UIImageAtlas([first], 4, srgb: false));
        Should.Throw<ArgumentOutOfRangeException>(() => new UIImageAtlas([first], 7));
    }

    [Test]
    public void TextureHasExactColorAndSamplerProfileAndRetainedPixelsSurviveSourceDestroy()
    {
        UIImageAtlas atlas = new([new("a", 1, 1, new byte[] { 1, 2, 3, 255 })], 4);
        XRTexture2D texture = atlas.CreateTexture();
        texture.SizedInternalFormat.ShouldBe(ESizedInternalFormat.Srgb8Alpha8);
        texture.Mipmaps[0].InternalFormat.ShouldBe(EPixelInternalFormat.Srgb8Alpha8);
        texture.ImportedColorSpace.ShouldBe(ETextureColorSpace.Srgb);
        texture.AutoGenerateMipmaps.ShouldBeFalse();
        texture.UWrap.ShouldBe(ETexWrapMode.ClampToEdge);
        UITextureSnapshot.TryAcquire(texture, out UITextureSnapshot? snapshot, out string? failure).ShouldBeTrue(failure);
        try
        {
            texture.Destroy(true);
            snapshot!.Texture.Mipmaps[0].DataBytes![0].ShouldBe((byte)1);
        }
        finally { snapshot!.Release(); }
    }

    [Test]
    public void AtlasOwnerRejectsForeignControlsAndTeardownLeavesLaterImageBindingAlone()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new();
        UIImageAtlasComponent owner = root.AddComponent<UIImageAtlasComponent>()!;
        UIImageAtlas atlas = new([new("a", 1, 1, new byte[] { 1, 2, 3, 255 })], 4);
        owner.Initialize(atlas);
        SceneNode child = new(root);
        UIMaterialComponent image = child.AddComponent<UIMaterialComponent>()!;
        SceneNode foreign = new();
        UIMaterialComponent foreignImage = foreign.AddComponent<UIMaterialComponent>()!;
        XRTexture2D other = atlas.CreateTexture();
        try
        {
            Should.Throw<ArgumentException>(() => owner.Bind(foreignImage, "a", Vector4.One));
            owner.Bind(image, "a", Vector4.One);
            image.SetBatchImage(other, Vector4.One, atlas.GetRegion("a"));
            owner.Destroy(true);
            image.ClearBatchImage(other).ShouldBeTrue();
        }
        finally
        {
            root.Destroy(true);
            foreign.Destroy(true);
            other.Destroy(true);
        }
    }

    [Test]
    public void BindingCallbackCanDestroyAtlasOwnerWithoutLeavingABorrowedTexture()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new();
        UIImageAtlasComponent owner = root.AddComponent<UIImageAtlasComponent>()!;
        owner.Initialize(new UIImageAtlas([new("a", 1, 1, new byte[] { 1, 2, 3, 255 })], 4));
        UIMaterialComponent image = new SceneNode(root).AddComponent<UIMaterialComponent>()!;
        bool destroy = true;
        image.PropertyChanged += (_, _) =>
        {
            if (!destroy) return;
            destroy = false;
            owner.Destroy(true);
            Should.Throw<ObjectDisposedException>(() => owner.Bind(image, "a", Vector4.One));
        };
        try
        {
            Should.Throw<ObjectDisposedException>(() => owner.Bind(image, "a", Vector4.One));
            owner.IsDestroyed.ShouldBeTrue();
            image.BatchImageRegion.ShouldBe(new Vector4(0, 0, 1, 1));
            image.Material!.Textures.Count.ShouldBe(0);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void InitializationCallbackCannotPublishAnAtlasAfterOwnerDestruction()
    {
        SceneNode root = new();
        UIImageAtlasComponent owner = root.AddComponent<UIImageAtlasComponent>()!;
        bool destroy = true;
        owner.PropertyChanged += (_, _) =>
        {
            if (!destroy) return;
            destroy = false;
            owner.Destroy(true);
        };
        try
        {
            Should.Throw<ObjectDisposedException>(() => owner.Initialize(
                new UIImageAtlas([new("a", 1, 1, new byte[] { 1, 2, 3, 255 })], 4)));
            owner.IsDestroyed.ShouldBeTrue();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ReentrantDestroyWithUnbindVetoKeepsOwnerAliveForCleanupRetry()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new();
        UIImageAtlasComponent owner = root.AddComponent<UIImageAtlasComponent>()!;
        owner.Initialize(new UIImageAtlas([new("a", 1, 1, new byte[] { 1, 2, 3, 255 })], 4));
        UIMaterialComponent image = new SceneNode(root).AddComponent<UIMaterialComponent>()!;
        bool veto = true, destroy = true;
        image.PropertyChanging += (_, args) =>
        {
            if (veto && args is XRPropertyChangingEventArgs<XRTexture2D?> change &&
                args.PropertyName == nameof(UIMaterialComponent.SetBatchSolidColor) && change.NewValue is null)
                change.AllowChange = false;
        };
        image.PropertyChanged += (_, _) =>
        {
            if (!destroy) return;
            destroy = false;
            owner.Destroy(true);
        };
        try
        {
            Should.Throw<AggregateException>(() => owner.Bind(image, "a", Vector4.One));
            owner.IsDestroyed.ShouldBeFalse();
            veto = false;
            owner.Destroy(true);
            owner.IsDestroyed.ShouldBeTrue();
            image.Material!.Textures.Count.ShouldBe(0);
        }
        finally
        {
            veto = false;
            owner.Destroy(true);
            root.Destroy(true);
        }
    }
}
