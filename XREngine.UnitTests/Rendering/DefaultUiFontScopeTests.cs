using System.Numerics;
using NUnit.Framework;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class DefaultUiFontScopeTests
{
    [Test]
    public void ExplicitIconFontResolvesIndependentlyOfTheTextFont()
    {
        FontGlyphSet text = CreateFont("A");
        FontGlyphSet icons = CreateFont("\u2713");
        try
        {
            using IDisposable scope = FontGlyphSet.InstallDefaultUiFont(text, icons);
            Assert.That(FontGlyphSet.LoadDefaultUIFont(), Is.SameAs(text));
            Assert.That(FontGlyphSet.LoadDefaultUIIconFont(), Is.SameAs(icons));
            Assert.Throws<InvalidOperationException>(() => FontGlyphSet.InstallDefaultUiFont(text));
        }
        finally { DestroyFont(icons); DestroyFont(text); }
    }

    [Test]
    public void DisposedScopeCannotReleaseReplacementFontsOrDestroyCallerResources()
    {
        FontGlyphSet text = CreateFont("A");
        FontGlyphSet firstIcons = CreateFont("\u2713");
        FontGlyphSet replacementIcons = CreateFont("\u2716");
        IDisposable? first = null;
        IDisposable? replacement = null;
        try
        {
            first = FontGlyphSet.InstallDefaultUiFont(text, firstIcons);
            first.Dispose();
            Assert.That(text.IsDestroyed || firstIcons.IsDestroyed, Is.False);
            Assert.That(text.Atlas!.IsDestroyed || firstIcons.Atlas!.IsDestroyed, Is.False);
            replacement = FontGlyphSet.InstallDefaultUiFont(text, replacementIcons);
            first.Dispose();
            Assert.That(FontGlyphSet.LoadDefaultUIFont(), Is.SameAs(text));
            Assert.That(FontGlyphSet.LoadDefaultUIIconFont(), Is.SameAs(replacementIcons));
        }
        finally
        {
            replacement?.Dispose();
            first?.Dispose();
            DestroyFont(replacementIcons);
            DestroyFont(firstIcons);
            DestroyFont(text);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InvalidFontDoesNotInstallEitherRole(bool invalidIcon)
    {
        FontGlyphSet valid = CreateFont("A");
        FontGlyphSet invalid = new();
        try
        {
            Assert.Throws<NotSupportedException>(() => FontGlyphSet.InstallDefaultUiFont(
                invalidIcon ? valid : invalid, invalidIcon ? invalid : valid));
            using IDisposable scope = FontGlyphSet.InstallDefaultUiFont(valid, valid);
            Assert.That(FontGlyphSet.LoadDefaultUIFont(), Is.SameAs(valid));
            Assert.That(FontGlyphSet.LoadDefaultUIIconFont(), Is.SameAs(valid));
        }
        finally { DestroyFont(invalid); DestroyFont(valid); }
    }

    [Test]
    public void TextOnlyInstallerRemainsAvailable()
    {
        FontGlyphSet text = CreateFont("A");
        try
        {
            using IDisposable scope = FontGlyphSet.InstallDefaultUiFont(text);
            Assert.That(FontGlyphSet.LoadDefaultUIFont(), Is.SameAs(text));
        }
        finally { DestroyFont(text); }
    }

    private static FontGlyphSet CreateFont(string glyph)
        => new()
        {
            Atlas = new XRTexture2D(8, 8, EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba, EPixelType.UnsignedByte, true),
            AtlasType = EFontAtlasType.Bitmap,
            Glyphs = new() { [glyph] = new(new Vector2(8), Vector2.Zero) },
            LayoutEmSize = 8
        };

    private static void DestroyFont(FontGlyphSet font)
    {
        font.Atlas?.Destroy(true);
        font.Destroy(true);
    }
}
