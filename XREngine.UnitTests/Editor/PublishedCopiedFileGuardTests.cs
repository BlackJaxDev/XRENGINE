using System.Buffers.Binary;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Editor;
using XREngine.Editor.Publishing;
using XREngine.UnitTests.Cutscenes;

namespace XREngine.UnitTests.Editor;

/// <summary>
/// A publish must not ship a machine path in a file that it copies as it is (text, JSON, media, and other files), or in the
/// common-assets archive. The tests use representative text and binary samples, and they cover the full content cook, the
/// incremental content cook, and the full and incremental common-assets packs.
/// </summary>
[TestFixture, NonParallelizable]
public sealed class PublishedCopiedFileGuardTests
{
    private const int MiB = 1024 * 1024;

    private string _root = null!;
    private string _assets = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "CopiedFileGuard", Guid.NewGuid().ToString("N"));
        _assets = Path.Combine(_root, "Assets");
        Directory.CreateDirectory(_assets);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string MachinePath(params string[] parts)
        => Path.Combine(["C:\\", "Users", "someone", .. parts]);

    [Test]
    public void TextSamples_WithAMachinePath_FailTheGuard_AndTheErrorNamesTheFile()
    {
        string path = MachinePath("Art", "floor.png");
        (string Name, byte[] Bytes)[] samples =
        [
            ("data.json", Encoding.UTF8.GetBytes("{\"source\": \"" + path.Replace("\\", "\\\\") + "\", \"size\": 4}")),
            ("forward.yaml", Encoding.UTF8.GetBytes("texture: " + path.Replace('\\', '/') + "\n")),
            ("notes.txt", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Exported from " + path + "\r\n")]),
            ("common.glsl", Encoding.UTF8.GetBytes("#include \"D:/Work/Shaders/common.glsl\"\nvoid main() {}\n")),
            ("share.json", Encoding.UTF8.GetBytes("{\"root\": \"\\\\\\\\server\\\\share\\\\textures\"}")),
        ];

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        foreach ((string name, byte[] bytes) in samples)
        {
            string file = Path.Combine(_root, name);
            File.WriteAllBytes(file, bytes);
            CookedSourcePathException error = Should.Throw<CookedSourcePathException>(
                () => CookedOutputPathGuard.ThrowIfMachinePathInCopiedFile(file, "Data/" + name), name);
            error.Message.ShouldContain("Data/" + name);
        }
    }

    [Test]
    public void TextSamples_WithPortableReferencesAndUrls_PassTheGuard()
    {
        string text =
            "{\"texture\": \"game://Textures/floor.png\", \"shader\": \"engine://Shaders/Common/lit.fs\",\n" +
            " \"docs\": \"https://example.com/a/b/c/d\", \"relative\": \"Textures/Wall/a/b.png\", \"time\": \"12:30:15\",\n" +
            " \"ratio\": \"a:b\", \"escaped\": \"Textures\\\\Wall\\\\a.png\", \"file\": \"file://host/share\"}\n";
        string file = Path.Combine(_root, "clean.json");
        File.WriteAllText(file, text, Encoding.UTF8);

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        Should.NotThrow(() => CookedOutputPathGuard.ThrowIfMachinePathInCopiedFile(file, "clean.json"));
    }

    [Test]
    public void BinaryMediaSamples_WithNoPath_PassTheGuard()
    {
        // Compressed media looks like random bytes: seeded random data stands in for compressed image, audio, and video
        // payloads. Raw pixel data holds long runs of printable bytes with many backslashes, and quiet 16-bit audio holds
        // many small sample values, which look like UTF-16 text. 160 MiB in all.
        (string Name, byte[] Bytes)[] samples =
        [
            ("floor.png", CreatePng(RandomBytes(1, 32 * MiB), metadataText: null)),
            ("music.wav", CreateWav(RandomBytes(2, 32 * MiB))),
            ("intro.mp4", CreateMp4Like(RandomBytes(3, 32 * MiB))),
            ("wall.tga", CreateTga(PrintableBytes(12, 32 * MiB))),
            ("quiet.wav", CreateWav(QuietPcm(13, 32 * MiB))),
        ];

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        foreach ((string name, byte[] bytes) in samples)
        {
            string file = Path.Combine(_root, name);
            File.WriteAllBytes(file, bytes);
            Should.NotThrow(() => CookedOutputPathGuard.ThrowIfMachinePathInCopiedFile(file, name), name);
        }
    }

    [Test]
    public void BinaryMediaSamples_WithAMachinePathInMetadata_FailTheGuard()
    {
        string documentPath = MachinePath("Documents", "Art", "floor.psd");
        string utf16Path = Path.Combine("E:\\", "Users", "artist", "Desktop", "floor.png");
        (string Name, byte[] Bytes)[] samples =
        [
            // An image editor writes the source document path into the XMP text chunk.
            ("floor.png", CreatePng(RandomBytes(4, 2 * MiB), "<xmpMM:DerivedFrom stRef:filePath=\"" + documentPath + "\"/>")),
            // A model or document format writes a UTF-16 path in a user profile folder.
            ("model.bin", [.. RandomBytes(5, MiB), .. Encoding.Unicode.GetBytes(utf16Path), 0, 0, .. RandomBytes(6, MiB)]),
        ];

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        foreach ((string name, byte[] bytes) in samples)
        {
            string file = Path.Combine(_root, name);
            File.WriteAllBytes(file, bytes);
            CookedSourcePathException error = Should.Throw<CookedSourcePathException>(
                () => CookedOutputPathGuard.ThrowIfMachinePathInCopiedFile(file, name), name);
            error.Message.ShouldContain(name);
        }
    }

    [Test]
    public void BinaryMediaRule_FindsOnlyUserFolderPathsAndExactRoots()
    {
        // Raw pixel data can form a drive path such as this one. The binary rule ignores it; the text rule finds it.
        byte[] pixelLike = [0x89, 0x01, .. Encoding.ASCII.GetBytes(@"x:\o3Tg/Na0Qd/Pc0Qe0Re-Ma/"), 0x00, 0xFE];
        CookedOutputPathGuard.TryFindMachinePath(pixelLike, [], binaryMedia: true, out _).ShouldBeFalse();
        CookedOutputPathGuard.TryFindMachinePath(pixelLike, [], binaryMedia: false, out _).ShouldBeTrue();

        // A drive path outside a user profile folder is not found in binary media. This is the cost of the exact rule.
        byte[] otherDrive = [0x89, 0x01, .. Encoding.UTF8.GetBytes(Path.Combine("E:\\", "Art", "Car", "model.blend")), 0x00];
        CookedOutputPathGuard.TryFindMachinePath(otherDrive, [], binaryMedia: true, out _).ShouldBeFalse();

        byte[] userPath = [0x89, 0x01, .. Encoding.UTF8.GetBytes(MachinePath("Downloads", "tex.png")), 0x00, 0xFE];
        CookedOutputPathGuard.TryFindMachinePath(userPath, [], binaryMedia: true, out string? found).ShouldBeTrue();
        found.ShouldBe(MachinePath("Downloads", "tex.png"));

        string legacy = Path.Combine("C:\\", "Documents and Settings", "someone", "a.png");
        CookedOutputPathGuard.TryFindMachinePath([0, .. Encoding.Unicode.GetBytes(legacy), 0, 0], [], binaryMedia: true, out found).ShouldBeTrue();
        found.ShouldBe(legacy);
    }

    [Test]
    public void BinaryMediaRule_FindsAnExactRootWithAShortTail()
    {
        string root = Path.Combine("C:\\", "Users", "someone", "Game", "Assets");
        byte[] bytes = [.. RandomBytes(7, 4096), .. Encoding.UTF8.GetBytes(Path.Combine(root, "a.png")), .. RandomBytes(8, 4096)];
        CookedOutputPathGuard.TryFindMachinePath(bytes, [root], binaryMedia: true, out string? found).ShouldBeTrue();
        found.ShouldStartWith(root);
    }

    [Test]
    public void BinaryFile_FindsAPathThatCrossesAChunkEdge()
    {
        string path = MachinePath("Documents", "Art", "floor.psd");
        byte[] bytes = RandomBytes(9, 9 * MiB);
        bytes[0] = 0;
        Encoding.UTF8.GetBytes(path).CopyTo(bytes, 4 * MiB - 10);
        string file = Path.Combine(_root, "edge.bin");
        File.WriteAllBytes(file, bytes);

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        Should.Throw<CookedSourcePathException>(() => CookedOutputPathGuard.ThrowIfMachinePathInCopiedFile(file, "edge.bin"));
    }

    [Test]
    public void LooksLikeText_DetectsBomsUtf8AndBinary()
    {
        CookedOutputPathGuard.LooksLikeText(Encoding.UTF8.GetBytes("plain text\n")).ShouldBeTrue();
        CookedOutputPathGuard.LooksLikeText([0xFF, 0xFE, (byte)'a', 0]).ShouldBeTrue();
        CookedOutputPathGuard.LooksLikeText(Encoding.UTF8.GetBytes("caf\u00E9")[..4]).ShouldBeTrue();
        CookedOutputPathGuard.LooksLikeText([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]).ShouldBeFalse();
        CookedOutputPathGuard.LooksLikeText([(byte)'a', 0xC3, 0x28]).ShouldBeFalse();
    }

    [Test]
    public void ContentCook_FullAndIncremental_FailForACopiedFileWithAMachinePath()
    {
        string source = Path.Combine(_assets, "Data", "level.json");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        string machinePath = MachinePath("Levels", "draft.json");
        File.WriteAllText(source, "{\"draft\": \"" + machinePath.Replace("\\", "\\\\") + "\"}", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(_assets, "Data", "clean.png"), CreatePng(RandomBytes(10, MiB), metadataText: null));

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        CookedSourcePathException error = Should.Throw<CookedSourcePathException>(
            () => ProjectBuilder.PrepareCookedContentDirectoryForTests(_assets, Path.Combine(_root, "Intermediate")));
        error.Message.ShouldContain("Data/level.json");

        string destination = Path.Combine(_root, "Delta", "Data", "level.json");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        error = Should.Throw<CookedSourcePathException>(() => ProjectBuilder.TransformIncrementalContentFile(source, destination));
        error.Message.ShouldContain("level.json");

        File.WriteAllText(source, "{\"draft\": \"game://Levels/draft.json\"}", Encoding.UTF8);
        Should.NotThrow(() => ProjectBuilder.PrepareCookedContentDirectoryForTests(_assets, Path.Combine(_root, "Intermediate2")));
        Should.NotThrow(() => ProjectBuilder.TransformIncrementalContentFile(source, destination));
    }

    [Test]
    public void CommonAssets_FullAndIncrementalPacks_FailForAFileWithAMachinePath()
    {
        string engine = Path.Combine(_root, "Engine");
        Directory.CreateDirectory(Path.Combine(engine, "Shaders"));
        File.WriteAllText(Path.Combine(engine, "Shaders", "lit.fs"), "void main() {}\n", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(engine, "noise.png"), CreatePng(RandomBytes(11, MiB), metadataText: null));
        string content = Path.Combine(_root, "Content");
        string intermediate = Path.Combine(_root, "Intermediate");

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));

        // A clean folder packs in both modes.
        string archive = ProjectBuilder.PackFullCommonAssetsForTests(engine, intermediate, content, incremental: false);
        File.Exists(archive).ShouldBeTrue();
        ProjectBuilder.PackFullCommonAssetsForTests(engine, intermediate, content, incremental: true);

        // The incremental repack checks the changed file.
        string leaking = Path.Combine(engine, "Shaders", "common.glsl");
        File.WriteAllText(leaking, "// " + MachinePath("Shaders", "Work", "common.glsl") + "\n", Encoding.UTF8);
        CookedSourcePathException error = Should.Throw<CookedSourcePathException>(
            () => ProjectBuilder.PackFullCommonAssetsForTests(engine, intermediate, content, incremental: true));
        error.Message.ShouldContain("common.glsl");

        // The full pack checks every file.
        error = Should.Throw<CookedSourcePathException>(
            () => ProjectBuilder.PackFullCommonAssetsForTests(engine, intermediate, content, incremental: false));
        error.Message.ShouldContain("Shaders/common.glsl");

        // The runtime shader pack checks its staged files.
        error = Should.Throw<CookedSourcePathException>(
            () => ProjectBuilder.PackageRuntimeShaderAssetsForTests(engine, intermediate, content));
        error.Message.ShouldContain("Shaders/common.glsl");
    }

    [Test]
    public void GuardCopiedDirectory_FailsForALicenseNoticeWithAMachinePath()
    {
        // The browser route checks its staged sources and its license notices with this step.
        string licenses = Path.Combine(_root, "site", "licenses");
        Directory.CreateDirectory(licenses);
        File.WriteAllText(Path.Combine(licenses, "Roboto-LICENSE.txt"), "Apache License, Version 2.0\n", Encoding.UTF8);
        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        Should.NotThrow(() => ProjectBuilder.GuardCopiedDirectory(licenses));

        File.WriteAllText(Path.Combine(licenses, "Font-NOTICE.txt"), "Built at " + MachinePath("Fonts", "Build", "out") + "\n", Encoding.UTF8);
        CookedSourcePathException error = Should.Throw<CookedSourcePathException>(() => ProjectBuilder.GuardCopiedDirectory(licenses));
        error.Message.ShouldContain("Font-NOTICE.txt");
    }

    /// <summary>Random bytes in the printable range, as raw mid-tone pixel data holds them.</summary>
    private static byte[] PrintableBytes(int seed, int length)
    {
        byte[] bytes = new byte[length];
        Random random = new(seed);
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)random.Next(0x20, 0x7F);
        return bytes;
    }

    /// <summary>16-bit little-endian samples of low amplitude.</summary>
    private static byte[] QuietPcm(int seed, int length)
    {
        byte[] bytes = new byte[length & ~1];
        Random random = new(seed);
        for (int i = 0; i < bytes.Length; i += 2)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), (short)random.Next(-300, 301));
        return bytes;
    }

    /// <summary>Makes an uncompressed true-color TGA-shaped file.</summary>
    private static byte[] CreateTga(byte[] pixels)
        => [0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 4, 24, 0, .. pixels];

    private static byte[] RandomBytes(int seed, int length)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Makes a PNG-shaped file: signature, header chunk, optional international text chunk, data chunk, end chunk.</summary>
    private static byte[] CreatePng(byte[] compressedData, string? metadataText)
    {
        using MemoryStream stream = new();
        stream.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(stream, "IHDR", [0, 0, 4, 0, 0, 0, 4, 0, 8, 6, 0, 0, 0]);
        if (metadataText is not null)
            WriteChunk(stream, "iTXt", [.. Encoding.ASCII.GetBytes("XML:com.adobe.xmp"), 0, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(metadataText)]);
        WriteChunk(stream, "IDAT", compressedData);
        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(Encoding.ASCII.GetBytes(type));
        stream.Write(data);
        stream.Write([0, 0, 0, 0]);
    }

    private static byte[] CreateWav(byte[] samples)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + samples.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(48000);
        writer.Write(48000 * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(samples.Length);
        writer.Write(samples);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] CreateMp4Like(byte[] payload)
    {
        using MemoryStream stream = new();
        stream.Write([0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2', 0, 0, 0, 0,
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'m', (byte)'p', (byte)'4', (byte)'2']);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, payload.Length + 8);
        stream.Write(size);
        stream.Write(Encoding.ASCII.GetBytes("mdat"));
        stream.Write(payload);
        return stream.ToArray();
    }
}
