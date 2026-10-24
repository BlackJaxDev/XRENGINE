using XREngine.Core.Files;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Editor;

/// <summary>Detects an unsupported getter being called during ordinary duplicate admission.</summary>
public sealed class NativeHierarchyDuplicateGetterProbe : Transform
{
    private TextFile? _asset;
    [YamlIgnore] public int Reads;
    public TextFile? Asset
    {
        get { Reads++; return _asset; }
        set => _asset = value;
    }
}
