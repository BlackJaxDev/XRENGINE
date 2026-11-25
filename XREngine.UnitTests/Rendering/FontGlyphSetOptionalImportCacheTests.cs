using System.Collections.Concurrent;
using System.Reflection;
using NUnit.Framework;
using XREngine.Data;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class FontGlyphSetOptionalImportCacheTests
{
    [Test]
    public void RepeatedOptionalWindowsFontImportFailureUsesTheCachedFailure()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Optional Windows font imports require Windows.");

        string fontsDirectory = Path.Combine(Path.GetTempPath(),
            "XREngine.OptionalFontFailure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fontsDirectory);
        string fontPath = Path.Combine(fontsDirectory, "seguisym.ttf");
        File.WriteAllText(fontPath, "invalid font payload");

        IRuntimePlatformPaths? priorPaths = RuntimePlatformPaths.Current;
        ConcurrentDictionary<string, Lazy<FontGlyphSet?>> cache = GetCache();
        string? cacheKey = null;
        try
        {
            RuntimePlatformPaths.Current = new TestPlatformPaths(fontsDirectory);

            MethodInfo loadOptionalFont = typeof(FontGlyphSet).GetMethod(
                "TryLoadWindowsUIFont", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.That(loadOptionalFont.Invoke(null, ["seguisym.ttf"]), Is.Null);

            KeyValuePair<string, Lazy<FontGlyphSet?>> cachedFailure = cache.Single(pair =>
                pair.Key.StartsWith(fontPath + "|", StringComparison.OrdinalIgnoreCase));
            cacheKey = cachedFailure.Key;
            Assert.That(cachedFailure.Value.IsValueCreated, Is.True);
            Assert.That(cachedFailure.Value.Value, Is.Null);

            Assert.That(loadOptionalFont.Invoke(null, ["seguisym.ttf"]), Is.Null);
            Lazy<FontGlyphSet?> cachedAgain = cache.TryGetValue(cacheKey, out Lazy<FontGlyphSet?>? cached) &&
                cached is not null
                    ? cached
                    : throw new AssertionException("The optional font failure was not cached.");
            Assert.That(cachedAgain, Is.SameAs(cachedFailure.Value));
            Assert.That(cachedAgain.Value, Is.Null);
        }
        finally
        {
            if (cacheKey is not null)
                cache.TryRemove(cacheKey, out _);
            RuntimePlatformPaths.Current = priorPaths;
            if (Directory.Exists(fontsDirectory))
                Directory.Delete(fontsDirectory, recursive: true);
        }
    }

    private static ConcurrentDictionary<string, Lazy<FontGlyphSet?>> GetCache()
    {
        FieldInfo field = typeof(FontGlyphSet).GetField("WindowsUiFontCache",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (ConcurrentDictionary<string, Lazy<FontGlyphSet?>>)field.GetValue(null)!;
    }

    private sealed class TestPlatformPaths(string fontsDirectory) : IRuntimePlatformPaths
    {
        public string GetFolderPath(Environment.SpecialFolder folder)
            => folder == Environment.SpecialFolder.Fonts ? fontsDirectory : string.Empty;
    }
}
