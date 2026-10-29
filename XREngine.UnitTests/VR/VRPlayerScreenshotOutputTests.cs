using System.Buffers;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data;
using XREngine.Imaging;
using XREngine.Rendering;
using XREngine.Runtime.Platform.Desktop;

namespace XREngine.UnitTests.VR;

[TestFixture, NonParallelizable]
public sealed class VRPlayerScreenshotOutputTests
{
    private IRuntimeDiagnosticCaptureFileOutput? _previousOutput;
    private IRuntimeImageCodec? _previousCodec;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCallerThread;
    private ProbeOutput _output = null!;
    private ProbeCodec _codec = null!;

    [SetUp]
    public void SetUp()
    {
        _previousOutput = RuntimeDiagnosticCaptureFileOutput.Current;
        _previousCodec = RuntimeImageCodecs.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCallerThread = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        _output = new ProbeOutput();
        _codec = new ProbeCodec();
        RuntimeDiagnosticCaptureFileOutput.Current = _output;
        RuntimeImageCodecs.Current = _codec;
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeAssetReadServices.IsCallerThread = _previousCallerThread;
        RuntimeAssetReadServices.SetSource(_previousSource);
        RuntimeImageCodecs.Current = _previousCodec;
        RuntimeDiagnosticCaptureFileOutput.Current = _previousOutput;
    }

    [Test]
    public void CapturedOutputReceivesPngAndReleasesImage()
    {
        IRuntimeDiagnosticCaptureFileOutput captured = VRPlayerInputSet.CaptureScreenshotOutput();
        var replacement = new ProbeOutput();
        RuntimeDiagnosticCaptureFileOutput.Current = replacement;
        var owner = new ProbeOwner();
        VRPlayerInputSet.WriteScreenshot(captured, "virtual-directory", MakeImage(owner), 7);
        Assert.That(_output.Directory, Is.EqualTo("virtual-directory"));
        Assert.That(_output.Name, Is.EqualTo("Screenshot_0007.png"));
        Assert.That(_output.Bytes, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(replacement.Calls, Is.Zero);
        Assert.That(owner.Disposed, Is.True);
    }

    [Test]
    public void NullImageDoesNotWrite()
    {
        VRPlayerInputSet.WriteScreenshot(_output, "virtual-directory", null, 0);
        Assert.That(_output.Calls, Is.Zero);
        Assert.That(_codec.Calls, Is.Zero);
    }

    [Test]
    public void OutputFailureReleasesImage()
    {
        var owner = new ProbeOwner();
        _output.Throw = true;
        Assert.Throws<IOException>(() => VRPlayerInputSet.WriteScreenshot(_output, "virtual-directory", MakeImage(owner), 0));
        Assert.That(owner.Disposed, Is.True);
    }

    [Test]
    public void CodecFailureReleasesImageWithoutWriting()
    {
        var owner = new ProbeOwner();
        _codec.Throw = true;
        Assert.Throws<InvalidOperationException>(() => VRPlayerInputSet.WriteScreenshot(_output, "virtual-directory", MakeImage(owner), 0));
        Assert.That(owner.Disposed, Is.True);
        Assert.That(_output.Calls, Is.Zero);
    }

    [Test]
    public void CallerThreadRejectsOutputAdmission()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        Assert.Throws<NotSupportedException>(() => VRPlayerInputSet.CaptureScreenshotOutput());
        Assert.That(_output.Calls, Is.Zero);
    }

    [Test]
    public void MissingOutputFailsBeforeCapture()
    {
        RuntimeDiagnosticCaptureFileOutput.Current = null;
        Assert.Throws<InvalidOperationException>(() => VRPlayerInputSet.CaptureScreenshotOutput());
    }

    [Test]
    public void DesktopOutputWritesExpectedFile()
    {
        Type type = typeof(DesktopHostAssetFileOutput).Assembly.GetType(
            "XREngine.Runtime.Platform.Desktop.DesktopDiagnosticCaptureFileOutput", throwOnError: true)!;
        var output = (IRuntimeDiagnosticCaptureFileOutput)Activator.CreateInstance(type, nonPublic: true)!;
        string root = Path.Combine(Path.GetTempPath(), "vr-screenshot-" + Guid.NewGuid().ToString("N"));
        var owner = new ProbeOwner();
        try
        {
            VRPlayerInputSet.WriteScreenshot(output, root, MakeImage(owner), 12);
            Assert.That(File.ReadAllBytes(Path.Combine(root, "Screenshot_0012.png")), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(owner.Disposed, Is.True);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static RuntimeImage MakeImage(ProbeOwner owner) => new(1, 1, RuntimePixelFormat.Rgba8, owner);

    private sealed class ProbeOwner : IMemoryOwner<byte>
    {
        private readonly byte[] _pixels = new byte[4];
        public bool Disposed;
        public Memory<byte> Memory => _pixels;
        public void Dispose() => Disposed = true;
    }

    private sealed class ProbeOutput : IRuntimeDiagnosticCaptureFileOutput
    {
        public int Calls;
        public bool Throw;
        public string? Directory;
        public string? Name;
        public byte[]? Bytes;
        public void WritePngInDirectory(string directory, string name, byte[] bytes)
        {
            Calls++;
            if (Throw) throw new IOException("Expected output failure.");
            Directory = directory;
            Name = name;
            Bytes = bytes;
        }
        public void WritePngAndMetrics(string path, byte[] bytes, RenderedOutputCaptureMetrics metrics,
            Func<RenderedOutputCaptureMetrics, string, string, DateTimeOffset, string> serializeMetrics)
            => throw new NotSupportedException();
    }

    private sealed class ProbeCodec : IRuntimeImageCodec
    {
        public int Calls;
        public bool Throw;
        public byte[] EncodePng(RuntimeImage image)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("Expected codec failure.");
            return [1, 2, 3];
        }
        public RuntimeImage Decode(ReadOnlyMemory<byte> bytes) => throw new NotSupportedException();
        public IReadOnlyList<RuntimeImage> DecodeFrames(ReadOnlyMemory<byte> bytes) => throw new NotSupportedException();
        public byte[] Encode(RuntimeImage image, RuntimeImageFileFormat format, int quality, bool srgb) => throw new NotSupportedException();
        public RuntimeImage Resize(RuntimeImage image, uint width, uint height, RuntimeImageResizeMode mode) => throw new NotSupportedException();
        public RuntimeImage ReprojectEquirectangularToCubeCross(RuntimeImage image) => throw new NotSupportedException();
    }
}
