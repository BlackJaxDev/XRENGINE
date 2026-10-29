using System.Text;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Runtime.Platform.Desktop;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Core;

[TestFixture, NonParallelizable]
public sealed class XRAssetSaveTransactionTests
{
    private IHostAssetFileOutput? _previousOutput;
    private IRuntimeAssetReadSource? _previousSource;
    private bool _previousCallerThread;
    private string _root = null!;
    private IDisposable _cacheSuppression = null!;

    [SetUp]
    public void SetUp()
    {
        _previousOutput = HostAssetFileOutputServices.Current;
        _previousSource = RuntimeAssetReadServices.Source;
        _previousCallerThread = RuntimeAssetReadServices.IsCallerThread;
        RuntimeAssetReadServices.SetSource(null);
        RuntimeAssetReadServices.IsCallerThread = false;
        HostAssetFileOutputServices.Current = new DesktopHostAssetFileOutput();
        _cacheSuppression = XRObjectBase.SuppressObjectCacheRegistration();
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "XRAssetSave", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        HostAssetFileOutputServices.Current = _previousOutput;
        RuntimeAssetReadServices.IsCallerThread = _previousCallerThread;
        RuntimeAssetReadServices.SetSource(_previousSource);
        _cacheSuppression.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void SaveCreatesAndReplacesDestination()
    {
        string path = Path.Combine(_root, "nested", "asset.yaml");
        var asset = new TestAsset();
        asset.SerializeTo(path, new ProbeSerializer("first"));
        File.ReadAllText(path).ShouldBe("first");
        asset.SerializeTo(path, new ProbeSerializer("second"));
        File.ReadAllText(path).ShouldBe("second");
        TempFiles(path).ShouldBeEmpty();
    }

    [Test]
    public async Task SaveAsyncCreatesAndReplacesDestination()
    {
        string path = Path.Combine(_root, "nested", "asset.yaml");
        var asset = new TestAsset();
        await asset.SerializeToAsync(path, new ProbeSerializer("first"));
        File.ReadAllText(path).ShouldBe("first");
        await asset.SerializeToAsync(path, new ProbeSerializer("second"));
        File.ReadAllText(path).ShouldBe("second");
        TempFiles(path).ShouldBeEmpty();
    }

    [Test]
    public void SerializerFailureKeepsDestinationAndRemovesTemp()
    {
        string path = Path.Combine(_root, "asset.yaml");
        File.WriteAllText(path, "old");
        var asset = new TestAsset();
        Assert.Throws<InvalidOperationException>(() => asset.SerializeTo(path, new ProbeSerializer("partial", fail: true)));
        File.ReadAllText(path).ShouldBe("old");
        TempFiles(path).ShouldBeEmpty();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await asset.SerializeToAsync(path, new ProbeSerializer("partial", fail: true)));
        File.ReadAllText(path).ShouldBe("old");
        TempFiles(path).ShouldBeEmpty();
    }

    [Test]
    public void MissingTransactionCapabilityRejectsBeforeSerialization()
    {
        HostAssetFileOutputServices.Current = new BasicOutput();
        var serializer = new ProbeSerializer("new");
        string path = Path.Combine(_root, "asset.yaml");
        Assert.Throws<NotSupportedException>(() => new TestAsset().SerializeTo(path, serializer));
        serializer.Calls.ShouldBe(0);
        File.Exists(path).ShouldBeFalse();
    }

    [Test]
    public void MissingOutputProviderRejectsBeforeSerialization()
    {
        HostAssetFileOutputServices.Current = null;
        var serializer = new ProbeSerializer("new");
        Assert.Throws<NotSupportedException>(() => new TestAsset().SerializeTo(Path.Combine(_root, "asset.yaml"), serializer));
        serializer.Calls.ShouldBe(0);
    }

    [Test]
    public void MissingTransactionCapabilityReturnsFaultedAsyncTask()
    {
        HostAssetFileOutputServices.Current = new BasicOutput();
        var serializer = new ProbeSerializer("new");
        Task save = new TestAsset().SerializeToAsync(Path.Combine(_root, "asset.yaml"), serializer);
        Assert.ThrowsAsync<NotSupportedException>(async () => await save);
        serializer.Calls.ShouldBe(0);
    }

    [Test]
    public void CallerThreadRejectsBeforeSerialization()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        var serializer = new ProbeSerializer("new");
        Assert.Throws<NotSupportedException>(() => new TestAsset().SerializeTo(Path.Combine(_root, "asset.yaml"), serializer));
        serializer.Calls.ShouldBe(0);
    }

    [Test]
    public void CallerThreadReturnsFaultedAsyncTaskBeforeSerialization()
    {
        RuntimeAssetReadServices.IsCallerThread = true;
        var serializer = new ProbeSerializer("new");
        Task save = new TestAsset().SerializeToAsync(Path.Combine(_root, "asset.yaml"), serializer);
        Assert.ThrowsAsync<NotSupportedException>(async () => await save);
        serializer.Calls.ShouldBe(0);
    }

    [Test]
    public void SaveUsesCapturedOutputWhenProviderChangesDuringSerialization()
    {
        var replacement = new ProbeOutput();
        var original = new ProbeOutput { OnWrite = () => HostAssetFileOutputServices.Current = replacement };
        HostAssetFileOutputServices.Current = original;
        new TestAsset().SerializeTo(Path.Combine(_root, "asset.yaml"), new ProbeSerializer("new"));
        original.Calls.ShouldBe(1);
        replacement.Calls.ShouldBe(0);
    }

    [Test]
    public async Task SaveAsyncUsesCapturedOutputWhenProviderChangesDuringSerialization()
    {
        var replacement = new ProbeOutput();
        var original = new ProbeOutput { OnWrite = () => HostAssetFileOutputServices.Current = replacement };
        HostAssetFileOutputServices.Current = original;
        await new TestAsset().SerializeToAsync(Path.Combine(_root, "asset.yaml"), new ProbeSerializer("new"));
        original.Calls.ShouldBe(1);
        replacement.Calls.ShouldBe(0);
    }

    [Test]
    public void ReplacementFailureKeepsDestination()
    {
        string path = Path.Combine(_root, "asset.yaml");
        File.WriteAllText(path, "old");
        string missingTemp = path + ".missing.tmp";
        MethodInfo replace = typeof(DesktopHostAssetFileOutput).GetMethod(
            "ReplaceSerializedAssetFile", BindingFlags.NonPublic | BindingFlags.Static)!;
        TargetInvocationException error = Assert.Throws<TargetInvocationException>(
            () => replace.Invoke(null, [missingTemp, path]))!;
        error.InnerException.ShouldBeAssignableTo<IOException>();
        File.ReadAllText(path).ShouldBe("old");
        File.Exists(missingTemp).ShouldBeFalse();
    }

    private IEnumerable<string> TempFiles(string path)
        => Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp");

    private sealed class TestAsset : XRAsset { }

    private sealed class ProbeSerializer(string text, bool fail = false) : ISerializer
    {
        public int Calls { get; private set; }
        public string Serialize(object? graph) => Serialize(graph, graph?.GetType() ?? typeof(object));
        public string Serialize(object? graph, Type type)
        {
            Calls++;
            if (fail) throw new InvalidOperationException("serializer failed");
            return text;
        }
        public void Serialize(TextWriter writer, object? graph) => Serialize(writer, graph, graph?.GetType() ?? typeof(object));
        public void Serialize(TextWriter writer, object? graph, Type type)
        {
            Calls++;
            writer.Write(text);
            if (fail) throw new InvalidOperationException("serializer failed");
        }
        public void Serialize(IEmitter emitter, object? graph) => throw new NotSupportedException();
        public void Serialize(IEmitter emitter, object? graph, Type type) => throw new NotSupportedException();
    }

    private sealed class BasicOutput : IHostAssetFileOutput
    {
        public Stream CreateFileWithParentDirectory(string path) => throw new NotSupportedException();
        public void WriteAllText(string path, string text, Encoding encoding) => throw new NotSupportedException();
        public Task WriteAllTextAsync(string path, string text, Encoding encoding) => throw new NotSupportedException();
    }

    private sealed class ProbeOutput : IHostAssetFileOutput, IHostAssetFileTransactionalOutput
    {
        public int Calls { get; private set; }
        public Action? OnWrite { get; init; }
        public void WriteTextWithReplacement(string path, Encoding encoding, Action<TextWriter> serialize)
        {
            Calls++;
            OnWrite?.Invoke();
            serialize(TextWriter.Null);
        }
        public Task WriteTextWithReplacementAsync(string path, Encoding encoding, Func<string> serialize)
        {
            Calls++;
            OnWrite?.Invoke();
            serialize();
            return Task.CompletedTask;
        }
        public Stream CreateFileWithParentDirectory(string path) => throw new NotSupportedException();
        public void WriteAllText(string path, string text, Encoding encoding) => throw new NotSupportedException();
        public Task WriteAllTextAsync(string path, string text, Encoding encoding) => throw new NotSupportedException();
    }
}
