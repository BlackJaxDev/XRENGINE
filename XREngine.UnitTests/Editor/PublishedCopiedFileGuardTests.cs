using System.Buffers.Binary;
using System.Text;
using System.IO.Compression;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Editor;
using XREngine.Editor.Publishing;
using XREngine.UnitTests.Cutscenes;
using YamlDotNet.RepresentationModel;

namespace XREngine.UnitTests.Editor;

/// <summary>
/// A publish strips machine paths from the files that it ships: the copied files (text, JSON, media, and other files), the
/// cooked assets, and the entries of the common-assets archive. The strip changes only the copy in the build output, keeps
/// the file length, and lists each stripped path. The tests use representative text and binary samples.
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
    public void TextSamples_LoseTheirMachinePaths_KeepTheirLengthAndSyntax_AndAreListed()
    {
        string path = MachinePath("Art", "floor.png");
        (string Name, byte[] Bytes, string Removed)[] samples =
        [
            ("data.json", Encoding.UTF8.GetBytes("{\"source\": \"" + path.Replace("\\", "\\\\") + "\", \"size\": 4}"), "someone"),
            ("forward.yaml", Encoding.UTF8.GetBytes("texture: " + path.Replace('\\', '/') + "\nnext: 1\n"), "someone"),
            ("quoted.yaml", Encoding.UTF8.GetBytes("texture: '" + path + "'\nnext: 1\n"), "someone"),
            ("notes.txt", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Exported from " + path + "\r\n")], "someone"),
            ("common.glsl", Encoding.UTF8.GetBytes("#include \"D:/Work/Shaders/common.glsl\"\nvoid main() {}\n"), "Work/Shaders"),
            ("share.json", Encoding.UTF8.GetBytes("{\"root\": \"\\\\\\\\server\\\\share\\\\textures\"}"), "server"),
            ("wood.mtl", Encoding.UTF8.GetBytes("newmtl wood\nmap_Kd E:\\Textures\\wood.png\n"), "Textures"),
            ("profile.json", Encoding.UTF8.GetBytes("{\"home\": \"C:\\\\Users\\\\alice\"}"), "alice"),
        ];

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        using PublishPathRedactionLog log = PublishPathRedactionLog.Begin();
        foreach ((string name, byte[] bytes, string removed) in samples)
        {
            string file = Path.Combine(_root, name);
            File.WriteAllBytes(file, bytes);

            CookedOutputPathGuard.StripCopiedFile(file, "Data/" + name).ShouldNotBeEmpty(name);

            byte[] stripped = File.ReadAllBytes(file);
            stripped.Length.ShouldBe(bytes.Length, name);
            string text = name == "notes.txt" ? Encoding.Unicode.GetString(stripped, 2, stripped.Length - 2) : Encoding.UTF8.GetString(stripped);
            text.ShouldNotContain(removed, Case.Sensitive, name);
            CookedOutputPathGuard.ContainsMachinePathInCopiedFile(file, file).ShouldBeFalse(name);
            if (name.EndsWith(".json", StringComparison.Ordinal))
                Should.NotThrow(() => JsonDocument.Parse(stripped).Dispose(), name);
            if (name == "quoted.yaml")
                text.ShouldContain("'\nnext: 1");
            log.Entries.ShouldContain(entry => entry.File == "Data/" + name, name);
        }

        File.ReadAllText(Path.Combine(_root, "wood.mtl")).ShouldContain(@"\wood.png");
        File.ReadAllText(Path.Combine(_root, "data.json")).ShouldContain(@"\\floor.png");
    }

    [Test]
    public void EscapedTextSamples_StayValidJsonAndYaml_AndKeepTheirFileNames()
    {
        (string Name, string Text, string FileName, string Removed)[] samples =
        [
            ("cmd.json", "{\"cmd\": \"\\\"C:\\\\Tools\\\\bin\\\\app.exe\\\" --x\"}", "app.exe", "Tools"),
            ("unicode.json", "{\"p\": \"C:\\\\Users\\\\b\\u00f6b\\\\a.png\", \"n\": 1}", "a.png", "\\u00f6"),
            ("tab.json", "{\"p\": \"D:\\\\Art\\\\new\\\\tex\\\\a.png\\t\"}", "a.png", "Art"),
            ("end.json", "{\"dir\": \"C:\\\\Tools\\\\bin\\\\\"}", "", "Tools"),
            ("cmd.yaml", "cmd: \"\\\"C:\\\\Tools\\\\bin\\\\app.exe\\\" --x\"\nnext: 1\n", "app.exe", "Tools"),
            ("hex.yaml", "p: \"C:\\\\Users\\\\b\\xf6b\\\\a.png\"\n", "a.png", "\\xf6"),
        ];

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        foreach ((string name, string text, string fileName, string removed) in samples)
        {
            string file = Path.Combine(_root, name);
            File.WriteAllText(file, text, new UTF8Encoding(false));

            CookedOutputPathGuard.StripCopiedFile(file, name).ShouldNotBeEmpty(name);

            string stripped = File.ReadAllText(file);
            stripped.Length.ShouldBe(text.Length, name);
            stripped.ShouldNotContain(removed, Case.Sensitive, name);
            stripped.ShouldContain(fileName, Case.Sensitive, name);
            if (name.EndsWith(".json", StringComparison.Ordinal))
            {
                using JsonDocument document = JsonDocument.Parse(stripped);
            }
            else
            {
                Should.NotThrow(() => new YamlStream().Load(new StringReader(stripped)), name);
            }
        }
    }

    [Test]
    public void TextSamples_WithPortableReferencesAndRelativePaths_DoNotChange()
    {
        string text =
            "{\"texture\": \"game://Textures/floor.png\", \"shader\": \"engine://Shaders/Common/lit.fs\",\n" +
            " \"docs\": \"https://example.com/a/b/c/d\", \"relative\": \"Textures/Wall/a/b.png\", \"time\": \"12:30:15\",\n" +
            " \"ratio\": \"a:b\", \"escaped\": \"Textures\\\\Wall\\\\Brick\\\\albedo.png\", \"up\": \"..\\\\Textures\\\\Wall\\\\a.png\",\n" +
            " \"file\": \"file://host/share\"}\n";
        string file = Path.Combine(_root, "clean.json");
        File.WriteAllText(file, text, Encoding.UTF8);
        byte[] before = File.ReadAllBytes(file);

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        CookedOutputPathGuard.StripCopiedFile(file, "clean.json").ShouldBeEmpty();
        File.ReadAllBytes(file).ShouldBe(before);
    }

    [Test]
    public void BinaryMediaSamples_WithNoPath_DoNotChange()
    {
        // Compressed media looks like random bytes: seeded random data stands in for compressed image, audio, and video
        // payloads. Raw pixel data holds long runs of printable bytes with many backslashes, an RLE HDR image holds runs of
        // mid-range bytes, and quiet 16-bit audio holds many small sample values, which look like UTF-16 text.
        (string Name, byte[] Bytes)[] samples =
        [
            ("floor.png", CreatePng(RandomBytes(1, 16 * MiB), metadataText: null)),
            ("music.wav", CreateWav(RandomBytes(2, 16 * MiB))),
            ("intro.mp4", CreateMp4Like(RandomBytes(3, 16 * MiB))),
            ("wall.tga", CreateTga(PrintableBytes(12, 32 * MiB))),
            ("sky.hdr", [.. Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 4096 +X 8192\n"), .. RleLikeBytes(14, 16 * MiB)]),
            ("quiet.wav", CreateWav(QuietPcm(13, 16 * MiB))),
        ];

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        foreach ((string name, byte[] bytes) in samples)
        {
            string file = Path.Combine(_root, name);
            File.WriteAllBytes(file, bytes);
            CookedOutputPathGuard.StripCopiedFile(file, name).ShouldBeEmpty(name);
            CookedOutputPathGuard.ContainsMachinePathInCopiedFile(file, file).ShouldBeFalse(name);
        }
    }

    [Test]
    public void BinaryMediaSamples_LoseTheirFolders_AndKeepTheirFileNamesAndStructure()
    {
        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        using PublishPathRedactionLog log = PublishPathRedactionLog.Begin();

        // PNG: the XMP text chunk holds the source document path. The chunk checksum is written again.
        string documentPath = MachinePath("Documents", "Art", "floor.psd");
        string png = Path.Combine(_root, "floor.png");
        File.WriteAllBytes(png, CreatePng(RandomBytes(4, MiB), "<xmpMM:DerivedFrom stRef:filePath=\"" + documentPath + "\"/>"));
        CookedOutputPathGuard.StripCopiedFile(png, "floor.png").ShouldBe([documentPath]);
        byte[] pngBytes = File.ReadAllBytes(png);
        AssertPngChecksums(pngBytes);
        Encoding.Latin1.GetString(pngBytes).ShouldContain("stRef:filePath=\"" + StripFolders(documentPath) + "\"");

        // FBX: a length-prefixed string property holds a drive path outside a user folder. The bytes after it stay.
        string blendPath = Path.Combine("E:\\", "Billy", "Blender", "Projects", "Car", "Corvette.blend");
        byte[] blend = Encoding.UTF8.GetBytes(blendPath);
        byte[] next = [(byte)'S', 7, 0, 0, 0, .. Encoding.ASCII.GetBytes("Comment")];
        string fbx = Path.Combine(_root, "model.fbx");
        byte[] fbxBytes = [.. Encoding.ASCII.GetBytes("Kaydara FBX Binary  "), 0, 0x1A, 0, .. RandomBytes(5, 4096), (byte)'S', .. LittleEndian(blend.Length), .. blend, .. next, .. RandomBytes(6, 4096)];
        File.WriteAllBytes(fbx, fbxBytes);
        CookedOutputPathGuard.StripCopiedFile(fbx, "model.fbx").ShouldBe([blendPath]);
        byte[] fbxStripped = File.ReadAllBytes(fbx);
        fbxStripped.Length.ShouldBe(fbxBytes.Length);
        int stringStart = fbxBytes.Length - 4096 - next.Length - blend.Length;
        Encoding.UTF8.GetString(fbxStripped, stringStart, blend.Length).ShouldBe(StripFolders(blendPath));
        fbxStripped.AsSpan(stringStart + blend.Length).ToArray().ShouldBe(fbxBytes.AsSpan(stringStart + blend.Length).ToArray());

        // EXR: a string attribute holds a user profile path, and the next attribute name follows without a separator.
        string skyPath = MachinePath("Poly Haven Dropbox", "Staging", "pureskies.blend");
        byte[] sky = Encoding.UTF8.GetBytes(skyPath);
        byte[] exrBytes = [0x76, 0x2F, 0x31, 0x01, 2, 0, 0, 0, .. Encoding.ASCII.GetBytes("source\0string\0"), .. LittleEndian(sky.Length), .. sky,
            .. Encoding.ASCII.GetBytes("Frame\0int\0"), 4, 0, 0, 0, 1, 0, 0, 0, 0, .. RandomBytes(7, 4096)];
        string exr = Path.Combine(_root, "sky.exr");
        File.WriteAllBytes(exr, exrBytes);
        CookedOutputPathGuard.StripCopiedFile(exr, "sky.exr").ShouldBe([skyPath]);
        Encoding.Latin1.GetString(File.ReadAllBytes(exr)).ShouldContain(StripFolders(skyPath) + "Frame\0int\0");

        // A UTF-16 user profile path in a document format.
        string utf16Path = Path.Combine("E:\\", "Users", "artist", "Desktop", "floor.png");
        string doc = Path.Combine(_root, "model.bin");
        File.WriteAllBytes(doc, [.. RandomBytes(8, MiB), .. Encoding.Unicode.GetBytes(utf16Path), 0, 0, .. RandomBytes(9, MiB)]);
        CookedOutputPathGuard.StripCopiedFile(doc, "model.bin").ShouldBe([utf16Path]);
        Encoding.Unicode.GetString(File.ReadAllBytes(doc)).ShouldNotContain("artist");

        log.Entries.Count.ShouldBe(4);
    }

    [Test]
    public void PrefixedStrings_StripOnlyThePathToken_AndKeepTheRestOfTheString()
    {
        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        string first = MachinePath("Art", "first.png");
        string second = Path.Combine("C:\\", "Users", "other", "Docs", "second.png");

        // A multi-line string attribute: the length covers both lines. Only the folder parts of the two paths change.
        byte[] attribute = Encoding.UTF8.GetBytes(first + "\nnotes about it\\here\n" + second);
        byte[] multiLine = [.. RandomBytes(16, 512), 0, .. LittleEndian(attribute.Length), .. attribute, 0, .. RandomBytes(17, 512)];
        string multiLineFile = Path.Combine(_root, "history.bin");
        File.WriteAllBytes(multiLineFile, multiLine);
        CookedOutputPathGuard.StripCopiedFile(multiLineFile, "history.bin");
        string expected = StripFolders(first) + "\nnotes about it\\here\n" + StripFolders(second);
        Encoding.UTF8.GetString(File.ReadAllBytes(multiLineFile), 517, attribute.Length).ShouldBe(expected);

        // A RIFF bext chunk: the chunk size covers the 256-byte description and binary fields that hold backslash bytes.
        byte[] description = new byte[256];
        Encoding.UTF8.GetBytes(first).CopyTo(description, 0);
        byte[] binaryFields = [.. Enumerable.Repeat((byte)0x5C, 8), 0x41, 0x5C, 0x2F, 0x42, .. RandomBytes(18, 64)];
        byte[] bext = [.. description, .. binaryFields];
        byte[] riff = [.. Encoding.ASCII.GetBytes("RIFF"), .. LittleEndian(4 + 8 + bext.Length), .. Encoding.ASCII.GetBytes("WAVEbext"),
            .. LittleEndian(bext.Length), .. bext];
        string wav = Path.Combine(_root, "take.wav");
        File.WriteAllBytes(wav, riff);
        CookedOutputPathGuard.StripCopiedFile(wav, "take.wav");
        byte[] wavStripped = File.ReadAllBytes(wav);
        int descriptionStart = 20;
        Encoding.UTF8.GetString(wavStripped, descriptionStart, first.Length).ShouldBe(StripFolders(first));
        wavStripped.AsSpan(descriptionStart + first.Length).ToArray().ShouldBe(riff.AsSpan(descriptionStart + first.Length).ToArray());

        // A false prefix: the four bytes before the path read as 4096, but the path ends at the zero byte after it.
        byte[] tail = [.. Enumerable.Repeat((byte)'\\', 64), .. Encoding.ASCII.GetBytes("Shared\\Notes\\plan.txt"), .. RandomBytes(19, 4096)];
        byte[] falsePrefix = [.. RandomBytes(20, 256), 0x00, 0x10, 0x00, 0x00, .. Encoding.UTF8.GetBytes(first), 0, .. tail];
        string falseFile = Path.Combine(_root, "false.bin");
        File.WriteAllBytes(falseFile, falsePrefix);
        CookedOutputPathGuard.StripCopiedFile(falseFile, "false.bin");
        byte[] falseStripped = File.ReadAllBytes(falseFile);
        Encoding.UTF8.GetString(falseStripped, 260, first.Length).ShouldBe(StripFolders(first));
        falseStripped.AsSpan(260 + first.Length).ToArray().ShouldBe(falsePrefix.AsSpan(260 + first.Length).ToArray());
    }

    [Test]
    public void CompressedContent_PastTheInflateLimit_FailsThePublish_AndNamesTheLimit()
    {
        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        string bomb = Path.Combine(_root, "bomb.zip");
        using (ZipArchive archive = ZipFile.Open(bomb, ZipArchiveMode.Create))
        using (Stream entry = archive.CreateEntry("zeros.bin", CompressionLevel.Optimal).Open())
        {
            byte[] zeros = new byte[MiB];
            for (int i = 0; i < 64; i++)
                entry.Write(zeros);
        }

        new FileInfo(bomb).Length.ShouldBeLessThan(MiB);
        long previous = CookedOutputPathGuard.MaxInflatedBytesPerItem;
        try
        {
            CookedOutputPathGuard.MaxInflatedBytesPerItem = 16L * MiB;
            CookedSourcePathException error = Should.Throw<CookedSourcePathException>(() => CookedOutputPathGuard.StripCopiedFile(bomb, "Data/bomb.zip"));
            error.Message.ShouldContain("Data/bomb.zip");
            error.Message.ShouldContain("inflated size");
            error.Message.ShouldContain("zeros.bin");
        }
        finally
        {
            CookedOutputPathGuard.MaxInflatedBytesPerItem = previous;
        }

        long previousFile = CookedOutputPathGuard.MaxInflatedBytesPerFile;
        try
        {
            CookedOutputPathGuard.MaxInflatedBytesPerFile = 8L * MiB;
            Should.Throw<CookedSourcePathException>(() => CookedOutputPathGuard.StripCopiedFile(bomb, "Data/bomb.zip"))
                .Message.ShouldContain("inflated size of the file");
        }
        finally
        {
            CookedOutputPathGuard.MaxInflatedBytesPerFile = previousFile;
        }
    }

    [Test]
    public void TextStartingWithBzh_IsNotABzip2Container()
    {
        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        string text = Path.Combine(_root, "notes.txt");
        File.WriteAllText(text, "BZh is the start of this note, game://Textures/a.png\n", new UTF8Encoding(false));
        CookedOutputPathGuard.StripCopiedFile(text, "notes.txt").ShouldBeEmpty();

        string bzip2 = Path.Combine(_root, "data.bz2");
        File.WriteAllBytes(bzip2, [(byte)'B', (byte)'Z', (byte)'h', (byte)'9', 0x31, 0x41, 0x59, 0x26, 0x53, 0x59, .. RandomBytes(21, 64)]);
        Should.Throw<CookedSourcePathException>(() => CookedOutputPathGuard.StripCopiedFile(bzip2, "Data/data.bz2"))
            .Message.ShouldContain("Data/data.bz2");
    }

    [Test]
    public void CompressedContent_WithAPath_FailsThePublish_AndCleanContentPasses()
    {
        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        string path = MachinePath("Documents", "Art", "floor.psd");

        // PNG zTXt and compressed iTXt chunks.
        string ztxt = Path.Combine(_root, "ztxt.png");
        File.WriteAllBytes(ztxt, CreatePngWithChunk("zTXt", [.. Encoding.ASCII.GetBytes("Comment"), 0, 0, .. Deflate(Encoding.UTF8.GetBytes("source " + path))]));
        Should.Throw<CookedSourcePathException>(() => CookedOutputPathGuard.StripCopiedFile(ztxt, "Data/ztxt.png")).Message.ShouldContain("Data/ztxt.png");

        string itxt = Path.Combine(_root, "itxt.png");
        File.WriteAllBytes(itxt, CreatePngWithChunk("iTXt", [.. Encoding.ASCII.GetBytes("XML:com.adobe.xmp"), 0, 1, 0, 0, 0, .. Deflate(Encoding.UTF8.GetBytes("<x path=\"" + path + "\"/>"))]));
        Should.Throw<CookedSourcePathException>(() => CookedOutputPathGuard.ContainsMachinePathInCopiedFile(itxt, "Data/itxt.png")).Message.ShouldContain("Data/itxt.png");

        string cleanPng = Path.Combine(_root, "clean.png");
        File.WriteAllBytes(cleanPng, CreatePngWithChunk("zTXt", [.. Encoding.ASCII.GetBytes("Comment"), 0, 0, .. Deflate(Encoding.UTF8.GetBytes("made with care"))]));
        CookedOutputPathGuard.StripCopiedFile(cleanPng, "clean.png").ShouldBeEmpty();

        // ZIP and GZIP content.
        string zip = Path.Combine(_root, "bundle.zip");
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (StreamWriter writer = new(archive.CreateEntry("notes/readme.txt", CompressionLevel.Optimal).Open()))
            writer.Write("Built at " + path + "\n");
        Should.Throw<CookedSourcePathException>(() => CookedOutputPathGuard.StripCopiedFile(zip, "Data/bundle.zip")).Message.ShouldContain("notes/readme.txt");

        string cleanZip = Path.Combine(_root, "clean.zip");
        using (ZipArchive archive = ZipFile.Open(cleanZip, ZipArchiveMode.Create))
        using (StreamWriter writer = new(archive.CreateEntry("readme.txt").Open()))
            writer.Write("game://Textures/a.png\n");
        CookedOutputPathGuard.StripCopiedFile(cleanZip, "clean.zip").ShouldBeEmpty();

        string gzip = Path.Combine(_root, "notes.txt.gz");
        using (FileStream stream = File.Create(gzip))
        using (GZipStream compressed = new(stream, CompressionLevel.Optimal))
            compressed.Write(Encoding.UTF8.GetBytes("Built at " + path + "\n"));
        Should.Throw<CookedSourcePathException>(() => CookedOutputPathGuard.StripCopiedFile(gzip, "Data/notes.txt.gz")).Message.ShouldContain("Data/notes.txt.gz");

        // A container that the guard cannot inflate, and damaged compressed data.
        string sevenZip = Path.Combine(_root, "pack.7z");
        File.WriteAllBytes(sevenZip, [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C, 0, 4, .. RandomBytes(12, 256)]);
        Should.Throw<CookedSourcePathException>(() => CookedOutputPathGuard.StripCopiedFile(sevenZip, "Data/pack.7z")).Message.ShouldContain("Data/pack.7z");

        string broken = Path.Combine(_root, "broken.png");
        File.WriteAllBytes(broken, CreatePngWithChunk("zTXt", [.. Encoding.ASCII.GetBytes("Comment"), 0, 0, 0xFF, 0xFF, 0xFF, 0xFF]));
        Should.Throw<CookedSourcePathException>(() => CookedOutputPathGuard.StripCopiedFile(broken, "Data/broken.png")).Message.ShouldContain("Data/broken.png");
    }

    [Test]
    public void BinaryMediaRule_IgnoresPixelLikeRuns_ThatTheTextRuleFinds()
    {
        string[] pixelLike = [@"x:\o3Tg/Na0Qd/Pc0Qe0Re-Ma/", @"Q:/C90EUPZ\SP\LMZKM_SS_TV[RT", @"M:/O=1R5+M20O34P23O/+J0+I22N30N4/O40O01L'.H/1D77@"];
        foreach (string run in pixelLike)
        {
            byte[] bytes = [0x89, 0x01, .. Encoding.ASCII.GetBytes(run), 0x00, 0xFE];
            CookedOutputPathGuard.TryFindMachinePath(bytes, [], EMachinePathScanProfile.BinaryMedia, out _).ShouldBeFalse(run);
        }

        CookedOutputPathGuard.TryFindMachinePath([.. Encoding.ASCII.GetBytes(pixelLike[0])], [], EMachinePathScanProfile.Text, out _).ShouldBeTrue();
        string legacy = Path.Combine("C:\\", "Documents and Settings", "someone", "a.png");
        CookedOutputPathGuard.TryFindMachinePath([0, .. Encoding.Unicode.GetBytes(legacy), 0, 0], [], EMachinePathScanProfile.BinaryMedia, out string? found).ShouldBeTrue();
        found.ShouldBe(legacy);
    }

    [Test]
    public void UnsafeFiles_FailThePublish_AndNameTheFile()
    {
        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        string path = MachinePath("Documents", "Art", "floor.psd");

        // A damaged ZIP file cannot be inspected.
        string zip = Path.Combine(_root, "bundle.zip");
        File.WriteAllBytes(zip, [(byte)'P', (byte)'K', 3, 4, 0, 0, .. Encoding.UTF8.GetBytes(path), 0]);
        Should.Throw<CookedSourcePathException>(() => CookedOutputPathGuard.StripCopiedFile(zip, "Data/bundle.zip"))
            .Message.ShouldContain("Data/bundle.zip");

        // A path in the image data of a PNG is inside a critical chunk.
        string png = Path.Combine(_root, "data.png");
        File.WriteAllBytes(png, CreatePng([.. RandomBytes(10, 1024), .. Encoding.UTF8.GetBytes(path), 0, .. RandomBytes(11, 1024)], metadataText: null));
        Should.Throw<CookedSourcePathException>(() => CookedOutputPathGuard.StripCopiedFile(png, "Data/data.png"))
            .Message.ShouldContain("Data/data.png");
    }

    [Test]
    public void BinaryFile_StripsAPathThatCrossesAChunkEdge()
    {
        string path = MachinePath("Documents", "Art", "floor.psd");
        byte[] bytes = RandomBytes(9, 9 * MiB);
        bytes[0] = 0;
        Encoding.UTF8.GetBytes(path).CopyTo(bytes, 4 * MiB - 10);
        string file = Path.Combine(_root, "edge.bin");
        File.WriteAllBytes(file, bytes);

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        // Random name bytes can follow the path, so the reported text can be longer than the path.
        CookedOutputPathGuard.StripCopiedFile(file, "edge.bin").ShouldContain(item => item.StartsWith(path, StringComparison.Ordinal));
        CookedOutputPathGuard.ContainsMachinePathInCopiedFile(file, file).ShouldBeFalse();
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
    public void ContentCook_FullAndIncremental_StripTheCookedCopy_AndKeepTheSource()
    {
        string source = Path.Combine(_assets, "Data", "level.json");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        string machinePath = MachinePath("Levels", "draft.json");
        string json = "{\"draft\": \"" + machinePath.Replace("\\", "\\\\") + "\"}";
        File.WriteAllText(source, json, Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(_assets, "Data", "clean.png"), CreatePng(RandomBytes(10, MiB), metadataText: null));

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        using PublishPathRedactionLog log = PublishPathRedactionLog.Begin();

        string cooked = ProjectBuilder.PrepareCookedContentDirectoryForTests(_assets, Path.Combine(_root, "Intermediate"));
        string cookedJson = File.ReadAllText(Path.Combine(cooked, "Data", "level.json"));
        cookedJson.ShouldNotContain("someone");
        cookedJson.Length.ShouldBe(json.Length);
        File.ReadAllText(source).ShouldBe(json);
        log.Entries.ShouldContain(entry => entry.File == "Data/level.json");

        string destination = Path.Combine(_root, "Delta", "Data", "level.json");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        ProjectBuilder.TransformIncrementalContentFile(source, destination);
        File.ReadAllText(destination).ShouldNotContain("someone");
        File.ReadAllText(source).ShouldBe(json);
    }

    [Test]
    public void CommonAssets_FullIncrementalAndRuntimeShaderPacks_StoreStrippedEntries_AndKeepTheSources()
    {
        string engine = Path.Combine(_root, "Engine");
        Directory.CreateDirectory(Path.Combine(engine, "Shaders"));
        File.WriteAllText(Path.Combine(engine, "Shaders", "lit.fs"), "void main() {}\n", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(engine, "noise.png"), CreatePng(RandomBytes(11, MiB), metadataText: null));
        string leaking = Path.Combine(engine, "Shaders", "common.glsl");
        string leakText = "// " + MachinePath("Shaders", "Work", "common.glsl") + "\n";
        File.WriteAllText(leaking, leakText, Encoding.UTF8);
        string content = Path.Combine(_root, "Content");
        string intermediate = Path.Combine(_root, "Intermediate");

        using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
        using PublishPathRedactionLog log = PublishPathRedactionLog.Begin();

        string archive = ProjectBuilder.PackFullCommonAssetsForTests(engine, intermediate, content, incremental: false);
        AssertEntryStripped(archive, "Shaders/common.glsl", (int)new FileInfo(leaking).Length);
        File.ReadAllText(leaking).ShouldBe(leakText);
        log.Entries.ShouldContain(entry => entry.File == "Shaders/common.glsl");

        // An archive that the current strip rules did not write (here, a plain pack) is packed again in full by the
        // incremental build, also when no source file changed.
        File.Delete(archive);
        AssetPacker.Pack(engine, archive);
        ProjectBuilder.PackFullCommonAssetsForTests(engine, intermediate, content, incremental: true);
        AssertEntryStripped(archive, "Shaders/common.glsl", (int)new FileInfo(leaking).Length);

        // A changed file in a current archive goes through the strip in the incremental repack.
        File.WriteAllText(leaking, leakText + "// " + MachinePath("Other", "Work", "x.glsl") + "\n", Encoding.UTF8);
        File.SetLastWriteTimeUtc(leaking, DateTime.UtcNow.AddMinutes(1));
        ProjectBuilder.PackFullCommonAssetsForTests(engine, intermediate, content, incremental: true);
        Encoding.UTF8.GetString(AssetPacker.GetAsset(archive, "Shaders/common.glsl")).ShouldNotContain("someone");

        // An incremental build that changes nothing still lists the stripped entries of the archive.
        using (PublishPathRedactionLog quiet = PublishPathRedactionLog.Begin())
        {
            ProjectBuilder.PackFullCommonAssetsForTests(engine, intermediate, content, incremental: true);
            quiet.Entries.ShouldContain(entry => entry.File.EndsWith("Shaders/common.glsl", StringComparison.Ordinal)
                && entry.Item.Contains("earlier build", StringComparison.Ordinal));
        }

        string shaders = ProjectBuilder.PackageRuntimeShaderAssetsForTests(engine, intermediate, Path.Combine(_root, "ShaderContent"));
        Encoding.UTF8.GetString(AssetPacker.GetAsset(shaders, "Shaders/common.glsl")).ShouldNotContain("someone");
        File.ReadAllText(leaking).ShouldContain("someone");
    }

    [Test]
    public void RedactionLog_WritesTheReport_AndSaysWhenNothingWasRemoved()
    {
        string report = Path.Combine(_root, "Intermediate", "Build", "Logs", "publish-report.txt");
        using (PublishPathRedactionLog log = PublishPathRedactionLog.Begin())
        {
            PublishPathRedactionLog.Record("Models/a.fbx", MachinePath("Downloads", "a.fbx"));
            PublishPathRedactionLog.Record("Models/a.fbx", MachinePath("Downloads", "a.fbx"));

            log.WriteReport(report).ShouldBe(report);
            File.ReadAllText(report).ShouldContain("Models/a.fbx: " + MachinePath("Downloads", "a.fbx"));
            log.Entries.Count.ShouldBe(1);
        }

        PublishPathRedactionLog.DeleteReport(report);
        File.Exists(report).ShouldBeFalse();
        using (PublishPathRedactionLog empty = PublishPathRedactionLog.Begin())
        {
            empty.WriteReport(report);
            File.ReadAllText(report).ShouldContain("removed no machine path");
        }
    }

    /// <summary>Replaces each character before the last separator with an underscore and keeps the separators.</summary>
    private static string StripFolders(string path)
    {
        int end = path.LastIndexOfAny(['\\', '/']) + 1;
        char[] result = path.ToCharArray();
        for (int i = 0; i < end; i++)
            if (result[i] is not ('\\' or '/'))
                result[i] = '_';
        return new string(result);
    }

    private static byte[] Deflate(byte[] data)
    {
        using MemoryStream stream = new();
        using (ZLibStream compressed = new(stream, CompressionLevel.Optimal, leaveOpen: true))
            compressed.Write(data);
        return stream.ToArray();
    }

    private static byte[] CreatePngWithChunk(string type, byte[] data)
    {
        using MemoryStream stream = new();
        stream.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(stream, "IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        WriteChunk(stream, type, data);
        WriteChunk(stream, "IDAT", RandomBytes(15, 1024));
        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void AssertEntryStripped(string archive, string entry, int length)
    {
        byte[] bytes = AssetPacker.GetAsset(archive, entry);
        bytes.Length.ShouldBe(length);
        Encoding.UTF8.GetString(bytes).ShouldNotContain("someone");
    }

    private static void AssertPngChecksums(byte[] png)
    {
        int position = 8;
        while (position + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            uint expected = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + 8 + length));
            System.IO.Hashing.Crc32.HashToUInt32(png.AsSpan(position + 4, 4 + length)).ShouldBe(expected);
            position += 12 + length;
        }
    }

    private static byte[] LittleEndian(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
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

    /// <summary>Bytes from 0x40 to 0xFF, as the run-length data of a Radiance HDR image holds them.</summary>
    private static byte[] RleLikeBytes(int seed, int length)
    {
        byte[] bytes = new byte[length];
        Random random = new(seed);
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)random.Next(0x40, 0x100);
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

    /// <summary>Makes a PNG-shaped file with valid chunk checksums: signature, header, optional text chunk, data, end.</summary>
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
        byte[] typeAndData = [.. Encoding.ASCII.GetBytes(type), .. data];
        stream.Write(typeAndData);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, System.IO.Hashing.Crc32.HashToUInt32(typeAndData));
        stream.Write(crc);
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
