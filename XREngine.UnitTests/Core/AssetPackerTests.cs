using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Text;
using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine.UnitTests.Core;

[TestFixture]
public class AssetPackerTests
{
    private string _tempRoot = string.Empty;
    private CompressionCodec _savedCodec;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "AssetPacker", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _savedCodec = AssetPacker.DefaultCodec;
    }

    [TearDown]
    public void TearDown()
    {
        // Always restore DefaultCodec so tests don't leak state.
        AssetPacker.DefaultCodec = _savedCodec;

        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, true);
    }

    // ═══════════════════ Helpers ═══════════════════════════════════════════

    private string MakeSourceDir(string name, params (string path, string content)[] files)
    {
        string dir = Path.Combine(_tempRoot, name);
        foreach (var (path, content) in files)
            WriteUtf8(Path.Combine(dir, path), content);
        return dir;
    }

    private string ArchivePath(string name = "test.pak") => Path.Combine(_tempRoot, name);

    private static void WriteUtf8(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ReadAssetUtf8(string archive, string assetPath)
        => Encoding.UTF8.GetString(AssetPacker.GetAsset(archive, assetPath));

    // ═══════════════════ V4 Format Basics ═════════════════════════════════

    [Test]
    public void Pack_WritesV4Header_WithDefaultLzmaCodec()
    {
        string src = MakeSourceDir("src",
            ("a.txt", "alpha"),
            ("nested/b.txt", "bravo"));
        string pak = ArchivePath();

        AssetPacker.Pack(src, pak);

        var info = AssetPacker.ReadArchiveInfo(pak);
        info.Version.ShouldBe(4);
        info.FileCount.ShouldBe(2);
        info.Flags.HasFlag(ArchiveFlags.HasContentHashes).ShouldBeTrue();
        info.Flags.HasFlag(ArchiveFlags.HasSourceTimestamps).ShouldBeTrue();
        info.Flags.HasFlag(ArchiveFlags.HasUncompressedSizes).ShouldBeTrue();
        info.Entries.ShouldAllBe(e => e.UncompressedSize > 0);
        info.Entries.ShouldAllBe(e => e.ContentHash != 0);
        info.Entries.ShouldAllBe(e => e.Codec == CompressionCodec.Lzma);
    }

    [Test]
    public void Pack_Repack_Compact_StaleDetection_RoundTrips()
    {
        string sourceDir = MakeSourceDir("src",
            ("a.txt", "alpha-v1"),
            ("nested/b.txt", "bravo-v1"));
        string deltaDir = Path.Combine(_tempRoot, "delta");
        string pak = ArchivePath("content.pak");

        AssetPacker.Pack(sourceDir, pak);

        // Verify initial content.
        var paths = AssetPacker.GetAssetPaths(pak).OrderBy(x => x).ToArray();
        paths.ShouldBe(new[] { "a.txt", "nested/b.txt" });
        ReadAssetUtf8(pak, "a.txt").ShouldBe("alpha-v1");
        ReadAssetUtf8(pak, "nested/b.txt").ShouldBe("bravo-v1");

        // Repack: modify a.txt, add c.txt, remove nested/b.txt.
        WriteUtf8(Path.Combine(deltaDir, "a.txt"), "alpha-v2");
        WriteUtf8(Path.Combine(deltaDir, "c.txt"), "charlie-v1");
        AssetPacker.Repack(pak, deltaDir, "nested/b.txt");

        var infoAfterRepack = AssetPacker.ReadArchiveInfo(pak);
        infoAfterRepack.Version.ShouldBe(4);
        infoAfterRepack.FileCount.ShouldBe(2);
        infoAfterRepack.DeadBytes.ShouldBeGreaterThan(0);
        ReadAssetUtf8(pak, "a.txt").ShouldBe("alpha-v2");
        ReadAssetUtf8(pak, "c.txt").ShouldBe("charlie-v1");
        Should.Throw<FileNotFoundException>(() => AssetPacker.GetAsset(pak, "nested/b.txt"));

        // Compact should reclaim dead space.
        AssetPacker.Compact(pak);

        var infoAfterCompact = AssetPacker.ReadArchiveInfo(pak);
        infoAfterCompact.DeadBytes.ShouldBe(0);
        infoAfterCompact.Flags.HasFlag(ArchiveFlags.AppendOnly).ShouldBeFalse();
        ReadAssetUtf8(pak, "a.txt").ShouldBe("alpha-v2");
        ReadAssetUtf8(pak, "c.txt").ShouldBe("charlie-v1");

        // Stale detection.
        WriteUtf8(Path.Combine(sourceDir, "a.txt"), "alpha-v3");
        var stale = AssetPacker.GetStalePaths(pak, sourceDir);
        stale.ShouldContain("a.txt");
    }

    // ═══════════════════ Codec Selection — LZ4 ════════════════════════════

    [Test]
    public void Pack_WithLz4Codec_RoundTrips()
    {
        AssetPacker.DefaultCodec = CompressionCodec.Lz4;

        string src = MakeSourceDir("src-lz4",
            ("hello.txt", "Hello, LZ4 world!"),
            ("data/numbers.txt", "0123456789"));
        string pak = ArchivePath("lz4.pak");

        AssetPacker.Pack(src, pak);

        var info = AssetPacker.ReadArchiveInfo(pak);
        info.Version.ShouldBe(4);
        info.FileCount.ShouldBe(2);
        info.Entries.ShouldAllBe(e => e.Codec == CompressionCodec.Lz4);
        info.Entries.ShouldAllBe(e => e.UncompressedSize > 0);
        info.Entries.ShouldAllBe(e => e.ContentHash != 0);

        ReadAssetUtf8(pak, "hello.txt").ShouldBe("Hello, LZ4 world!");
        ReadAssetUtf8(pak, "data/numbers.txt").ShouldBe("0123456789");
    }

    [Test]
    public void Repack_WithLz4Codec_PreservesExistingAndAddsNew()
    {
        // Pack initial with LZMA.
        AssetPacker.DefaultCodec = CompressionCodec.Lzma;
        string src = MakeSourceDir("src-repack-lz4", ("original.txt", "lzma-content"));
        string pak = ArchivePath("repack-lz4.pak");
        AssetPacker.Pack(src, pak);

        // Repack with LZ4 codec for new files.
        AssetPacker.DefaultCodec = CompressionCodec.Lz4;
        string delta = Path.Combine(_tempRoot, "delta-lz4");
        WriteUtf8(Path.Combine(delta, "added.txt"), "lz4-content");
        AssetPacker.Repack(pak, delta);

        var info = AssetPacker.ReadArchiveInfo(pak);
        info.FileCount.ShouldBe(2);

        // Original entry preserves its LZMA codec from the source archive.
        var originalEntry = info.Entries.First(e => e.Path == "original.txt");
        originalEntry.Codec.ShouldBe(CompressionCodec.Lzma);

        // Newly added entry uses the active DefaultCodec (LZ4).
        var addedEntry = info.Entries.First(e => e.Path == "added.txt");
        addedEntry.Codec.ShouldBe(CompressionCodec.Lz4);

        // Both round-trip correctly.
        ReadAssetUtf8(pak, "original.txt").ShouldBe("lzma-content");
        ReadAssetUtf8(pak, "added.txt").ShouldBe("lz4-content");
    }

    [Test]
    public void Compact_WithLz4_PreservesCodecAndContent()
    {
        AssetPacker.DefaultCodec = CompressionCodec.Lz4;
        string src = MakeSourceDir("src-compact-lz4",
            ("keep.txt", "keep-me"),
            ("remove.txt", "remove-me"));
        string pak = ArchivePath("compact-lz4.pak");
        AssetPacker.Pack(src, pak);

        // Repack to remove one file (creates dead bytes).
        AssetPacker.Repack(pak, null!, "remove.txt");
        AssetPacker.ReadArchiveInfo(pak).DeadBytes.ShouldBeGreaterThan(0);

        // Compact.
        AssetPacker.Compact(pak);

        var info = AssetPacker.ReadArchiveInfo(pak);
        info.DeadBytes.ShouldBe(0);
        info.FileCount.ShouldBe(1);
        info.Entries[0].Codec.ShouldBe(CompressionCodec.Lz4);
        ReadAssetUtf8(pak, "keep.txt").ShouldBe("keep-me");
    }

    // ═══════════════════ Codec Selection — Zstd ═══════════════════════════

    [Test]
    public void Pack_WithZstdCodec_RoundTrips()
    {
        AssetPacker.DefaultCodec = CompressionCodec.Zstd;

        string src = MakeSourceDir("src-zstd",
            ("greeting.txt", "Hello, Zstd world!"),
            ("sub/payload.bin", "ABCDEFGHIJKLMNOPQRSTUVWXYZ"));
        string pak = ArchivePath("zstd.pak");

        AssetPacker.Pack(src, pak);

        var info = AssetPacker.ReadArchiveInfo(pak);
        info.Version.ShouldBe(4);
        info.FileCount.ShouldBe(2);
        info.Entries.ShouldAllBe(e => e.Codec == CompressionCodec.Zstd);
        info.Entries.ShouldAllBe(e => e.UncompressedSize > 0);

        ReadAssetUtf8(pak, "greeting.txt").ShouldBe("Hello, Zstd world!");
        ReadAssetUtf8(pak, "sub/payload.bin").ShouldBe("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
    }

    [Test]
    public void Repack_WithZstdCodec_PreservesExistingAndAddsNew()
    {
        AssetPacker.DefaultCodec = CompressionCodec.Lzma;
        string src = MakeSourceDir("src-repack-zstd", ("file1.txt", "original-lzma"));
        string pak = ArchivePath("repack-zstd.pak");
        AssetPacker.Pack(src, pak);

        AssetPacker.DefaultCodec = CompressionCodec.Zstd;
        string delta = Path.Combine(_tempRoot, "delta-zstd");
        WriteUtf8(Path.Combine(delta, "file2.txt"), "new-zstd");
        AssetPacker.Repack(pak, delta);

        var info = AssetPacker.ReadArchiveInfo(pak);
        info.Entries.First(e => e.Path == "file1.txt").Codec.ShouldBe(CompressionCodec.Lzma);
        info.Entries.First(e => e.Path == "file2.txt").Codec.ShouldBe(CompressionCodec.Zstd);
        ReadAssetUtf8(pak, "file1.txt").ShouldBe("original-lzma");
        ReadAssetUtf8(pak, "file2.txt").ShouldBe("new-zstd");
    }

    // ═══════════════════ Mixed Codec Archives ═════════════════════════════

    [Test]
    public void MixedCodecArchive_DecompressEntry_UsesPerEntryCodec()
    {
        // Build an archive with LZMA entry, then repack add LZ4, then repack add Zstd.
        string src = MakeSourceDir("src-mixed", ("lzma.txt", "lzma-data"));
        string pak = ArchivePath("mixed.pak");
        AssetPacker.DefaultCodec = CompressionCodec.Lzma;
        AssetPacker.Pack(src, pak);

        AssetPacker.DefaultCodec = CompressionCodec.Lz4;
        string d1 = Path.Combine(_tempRoot, "d1");
        WriteUtf8(Path.Combine(d1, "lz4.txt"), "lz4-data");
        AssetPacker.Repack(pak, d1);

        AssetPacker.DefaultCodec = CompressionCodec.Zstd;
        string d2 = Path.Combine(_tempRoot, "d2");
        WriteUtf8(Path.Combine(d2, "zstd.txt"), "zstd-data");
        AssetPacker.Repack(pak, d2);

        var info = AssetPacker.ReadArchiveInfo(pak);
        info.FileCount.ShouldBe(3);

        // Verify per-entry codec metadata.
        info.Entries.First(e => e.Path == "lzma.txt").Codec.ShouldBe(CompressionCodec.Lzma);
        info.Entries.First(e => e.Path == "lz4.txt").Codec.ShouldBe(CompressionCodec.Lz4);
        info.Entries.First(e => e.Path == "zstd.txt").Codec.ShouldBe(CompressionCodec.Zstd);

        // Verify GetAsset round-trips (uses TryLoadEntry codec dispatch).
        ReadAssetUtf8(pak, "lzma.txt").ShouldBe("lzma-data");
        ReadAssetUtf8(pak, "lz4.txt").ShouldBe("lz4-data");
        ReadAssetUtf8(pak, "zstd.txt").ShouldBe("zstd-data");

        // Verify DecompressEntry also works per-entry.
        foreach (var entry in info.Entries)
        {
            byte[] decompressed = AssetPacker.DecompressEntry(pak, entry);
            string text = Encoding.UTF8.GetString(decompressed);
            text.ShouldBe(entry.Path.Replace(".txt", "-data"));
        }
    }

    [Test]
    public void MixedCodecArchive_SurvivesCompact()
    {
        // Create mixed-codec archive, add dead bytes, compact, and verify everything survives.
        string src = MakeSourceDir("src-compact-mix",
            ("keep-lzma.txt", "lzma-keep"),
            ("dead.txt", "will-die"));
        string pak = ArchivePath("compact-mix.pak");
        AssetPacker.DefaultCodec = CompressionCodec.Lzma;
        AssetPacker.Pack(src, pak);

        // Add an LZ4 entry and remove dead.txt.
        AssetPacker.DefaultCodec = CompressionCodec.Lz4;
        string d1 = Path.Combine(_tempRoot, "dmix");
        WriteUtf8(Path.Combine(d1, "keep-lz4.txt"), "lz4-keep");
        AssetPacker.Repack(pak, d1, "dead.txt");

        AssetPacker.ReadArchiveInfo(pak).DeadBytes.ShouldBeGreaterThan(0);

        AssetPacker.Compact(pak);

        var info = AssetPacker.ReadArchiveInfo(pak);
        info.DeadBytes.ShouldBe(0);
        info.FileCount.ShouldBe(2);
        info.Entries.First(e => e.Path == "keep-lzma.txt").Codec.ShouldBe(CompressionCodec.Lzma);
        info.Entries.First(e => e.Path == "keep-lz4.txt").Codec.ShouldBe(CompressionCodec.Lz4);
        ReadAssetUtf8(pak, "keep-lzma.txt").ShouldBe("lzma-keep");
        ReadAssetUtf8(pak, "keep-lz4.txt").ShouldBe("lz4-keep");
    }

    // ═══════════════════ Compression Dispatch Unit Tests ══════════════════

    [Test]
    [TestCase(CompressionCodec.Lzma)]
    [TestCase(CompressionCodec.Lz4)]
    [TestCase(CompressionCodec.Zstd)]
    public void Compress_Decompress_RoundTrips_AllCpuCodecs(CompressionCodec codec)
    {
        byte[] original = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog. " +
            "Pack my box with five dozen liquor jugs. How vexingly quick daft zebras jump.");

        byte[] compressed = Compression.Compress(original.AsSpan(), codec);
        compressed.Length.ShouldBeGreaterThan(0);

        byte[] decompressed = Compression.Decompress(compressed.AsSpan(), codec, original.Length);
        decompressed.ShouldBe(original);
    }

    [Test]
    [TestCase(CompressionCodec.Lzma)]
    [TestCase(CompressionCodec.Lz4)]
    [TestCase(CompressionCodec.Zstd)]
    public void Compress_Decompress_EmptyInput(CompressionCodec codec)
    {
        byte[] empty = [];
        byte[] compressed = Compression.Compress(empty.AsSpan(), codec);
        byte[] decompressed = Compression.Decompress(compressed.AsSpan(), codec, 0);
        decompressed.Length.ShouldBe(0);
    }

    [Test]
    [TestCase(CompressionCodec.Lz4)]
    [TestCase(CompressionCodec.Zstd)]
    public void LargePayload_RoundTrips(CompressionCodec codec)
    {
        // 256 KB of patterned data — enough to exercise real compression.
        byte[] original = new byte[256 * 1024];
        var rng = new Random(42);
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 251 ^ (rng.Next() & 0x03)); // mostly compressible

        byte[] compressed = Compression.Compress(original.AsSpan(), codec);
        compressed.Length.ShouldBeLessThan(original.Length); // should actually compress

        byte[] decompressed = Compression.Decompress(compressed.AsSpan(), codec, original.Length);
        decompressed.ShouldBe(original);
    }

    [Test]
    public void NvComp_FallsBackToCpuLz4_WhenUnavailable()
    {
        // NvComp should fall back to CPU LZ4 on machines without nvcomp.dll.
        byte[] data = Encoding.UTF8.GetBytes("nvcomp fallback test data payload");

        byte[] compressed = Compression.CompressNvComp(data);
        compressed.Length.ShouldBeGreaterThan(0);

        byte[] decompressed = Compression.DecompressNvComp(compressed);
        decompressed.ShouldBe(data);

        // Should also work through the unified dispatch.
        byte[] compressed2 = Compression.Compress(data.AsSpan(), CompressionCodec.NvComp);
        byte[] decompressed2 = Compression.Decompress(compressed2.AsSpan(), CompressionCodec.NvComp, data.Length);
        decompressed2.ShouldBe(data);
    }

    // ═══════════════════ Span-native LZ4 / Zstd specifics ════════════════

    [Test]
    public void CompressLz4_DecompressLz4_SpanRoundTrips()
    {
        byte[] data = Encoding.UTF8.GetBytes("LZ4 span-native round trip test!!");
        byte[] compressed = Compression.CompressLz4(data);
        byte[] decompressed = Compression.DecompressLz4(compressed);
        decompressed.ShouldBe(data);
    }

    [Test]
    public void CompressZstd_DecompressZstd_SpanRoundTrips()
    {
        byte[] data = Encoding.UTF8.GetBytes("Zstd span-native round trip test!!");
        byte[] compressed = Compression.CompressZstd(data);
        byte[] decompressed = Compression.DecompressZstd(compressed);
        decompressed.ShouldBe(data);
    }
}
