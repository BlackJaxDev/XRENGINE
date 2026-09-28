using XREngine.Components;

namespace XREngine.Browser;

/// <summary>Scene-owned browser renderable referring to immutable upload descriptors and current GPU handles.</summary>
public sealed class BrowserMeshComponent : XRComponent
{
    private BrowserMeshData? _mesh;
    private BrowserMaterialData? _material;
    private int _meshHandle;
    private int _materialHandle;
    private bool _renderEnabled = true;
    private bool _inPlay;

    public BrowserMeshData? Mesh
    {
        get => _mesh;
        internal set => SetField(ref _mesh, value);
    }

    public BrowserMaterialData? Material
    {
        get => _material;
        internal set => SetField(ref _material, value);
    }

    public int MeshHandle
    {
        get => _meshHandle;
        internal set => SetField(ref _meshHandle, value);
    }

    public int MaterialHandle
    {
        get => _materialHandle;
        internal set => SetField(ref _materialHandle, value);
    }

    /// <summary>Allows scene composition to suppress one component without changing shared uploads.</summary>
    public bool RenderEnabled
    {
        get => _renderEnabled;
        set => SetField(ref _renderEnabled, value);
    }

    internal bool IsRenderable => _inPlay && _renderEnabled && _mesh is not null && _material is not null;

    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        SetField(ref _inPlay, true);
    }

    protected override void OnEndPlay()
    {
        SetField(ref _inPlay, false);
        base.OnEndPlay();
    }
}
